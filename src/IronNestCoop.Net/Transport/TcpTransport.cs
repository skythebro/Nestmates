using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace IronNestCoop.Net.Transport
{
    /// <summary>
    /// Star-topology TCP transport: the host listens, guests connect to it. Used for two game copies
    /// on one PC (automated tests) and as a direct-IP fallback. Both delivery modes go over the one
    /// stream, so "unreliable" is never lost here; wrap in <see cref="ChaosTransport"/> to test loss.
    /// Frame: u32 length | u8 delivery | payload. A connecting guest first sends its u64 id, then the join key
    /// (u8 length | bytes). A host given a key admits only guests who send the same one; a wrong key is
    /// answered slowly (a guesser gets a few tries a second). Connections are capped, and one that sends nothing
    /// after its greeting is dropped.
    /// </summary>
    public sealed class TcpTransport : ITransport
    {
        const int MaxReliable = 512 * 1024, MaxUnreliable = 1200;
        const int MaxFrame = MaxReliable + 1;
        /// <summary>A connection whose unread data passes this is dropped (a peer can't fill our memory).</summary>
        const long MaxQueuedBytesPerConn = 64L << 20;
        const int HandshakeTimeoutMs = 5000, SendTimeoutMs = 3000, FirstFrameTimeoutMs = 15000;
        /// <summary>At most this many connections at once, and this many still greeting (a flood can't use up threads).</summary>
        public const int MaxConnections = 16, MaxGreeting = 4, MaxGreetingPerAddress = 2;
        public const int MaxKeyBytes = 64;
        const int WrongKeyDelayMs = 1000;

        sealed class Conn
        {
            public ulong Id;
            public TcpClient Client;
            public NetworkStream Stream;
            public readonly object SendLock = new();
            public volatile bool Dead, GotFrame;
            public Timer FirstFrame;   // kills a connection that never sends its first frame
            public long Queued;
        }

        readonly ConcurrentQueue<Packet> _inbox = new();
        readonly ConcurrentDictionary<ulong, Conn> _conns = new();
        readonly TcpListener _listener;
        readonly Thread _acceptThread;
        readonly string _desc;
        readonly byte[] _key = Array.Empty<byte>();
        int _greeting;
        readonly ConcurrentDictionary<IPAddress, int> _greetingFrom = new();
        volatile bool _disposed;

        public ulong LocalId { get; }

        /// <summary>Host: listen on 127.0.0.1 (or given address) and port. A non-empty key is required from guests.</summary>
        public static TcpTransport Listen(ulong localId, int port, IPAddress bind = null, string key = "")
            => new TcpTransport(localId, bind ?? IPAddress.Loopback, port, key);

        static byte[] KeyBytes(string key)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(key ?? "");
            if (b.Length > MaxKeyBytes) throw new ArgumentException($"the join key is longer than {MaxKeyBytes} bytes");
            return b;
        }

        /// <summary>Guest: connect to the host. hostId is the id the host uses (its LocalId). Blocks up to
        /// timeoutMs: call it off the game thread.</summary>
        public static TcpTransport Connect(ulong localId, ulong hostId, string host, int port, int timeoutMs = 5000, string key = "")
        {
            var keyBytes = KeyBytes(key);
            var t = new TcpTransport(localId, $"tcp guest -> {host}:{port}");
            var c = new TcpClient { NoDelay = true };
            try
            {
                if (!c.ConnectAsync(host, port).Wait(timeoutMs)) throw new IOException($"connect to {host}:{port} timed out");
            }
            catch (AggregateException e) { c.Dispose(); throw new IOException($"could not connect to {host}:{port}: {e.InnerException?.Message}"); }
            catch { c.Dispose(); throw; }
            Conn conn;
            try
            {
                c.SendTimeout = SendTimeoutMs;
                conn = new Conn { Id = hostId, Client = c, Stream = c.GetStream() };
                var hello = new byte[9 + keyBytes.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(hello, localId);
                hello[8] = (byte)keyBytes.Length;
                Buffer.BlockCopy(keyBytes, 0, hello, 9, keyBytes.Length);
                conn.Stream.Write(hello, 0, hello.Length);
            }
            catch (Exception e) { c.Dispose(); throw new IOException($"could not greet {host}:{port}: {e.Message}"); }
            t._conns[hostId] = conn;
            t.StartReader(conn);
            return t;
        }

        TcpTransport(ulong localId, string desc) { LocalId = localId; _desc = desc; }

        TcpTransport(ulong localId, IPAddress bind, int port, string key)
        {
            LocalId = localId;
            _key = KeyBytes(key);
            _listener = new TcpListener(bind, port);
            _listener.Start();
            _desc = $"tcp host on {_listener.LocalEndpoint}";
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "IronNestCoop.TcpAccept" };
            _acceptThread.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        void AcceptLoop()
        {
            while (!_disposed)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); }
                catch { if (_disposed) return; Thread.Sleep(100); continue; }
                // over the caps: refused at once (each connection costs a thread). Also at most two greetings from
                // one address: a few slow sockets from one machine must not hold every greeting place.
                IPAddress from = null;
                try { from = (c.Client.RemoteEndPoint as IPEndPoint)?.Address; } catch { }
                from ??= IPAddress.None;
                if (_conns.Count >= MaxConnections) { try { c.Dispose(); } catch { } continue; }
                int greetingNow = Interlocked.Increment(ref _greeting);
                int fromNow = _greetingFrom.AddOrUpdate(from, 1, (_, v) => v + 1);
                if (greetingNow > MaxGreeting || fromNow > MaxGreetingPerAddress)
                {
                    EndGreeting(from);   // both counts back
                    try { c.Dispose(); } catch { }
                    continue;
                }
                // The greeting is read on the connection's own thread with a deadline: a silent client must not
                // block everyone else's joins.
                try { new Thread(() => Greet(c, from)) { IsBackground = true, Name = "IronNestCoop.TcpGreet" }.Start(); }
                catch { EndGreeting(from); try { c.Dispose(); } catch { } }
            }
        }

        void EndGreeting(IPAddress from)
        {
            Interlocked.Decrement(ref _greeting);
            _greetingFrom.AddOrUpdate(from, 0, (_, v) => Math.Max(0, v - 1));
            if (_greetingFrom.TryGetValue(from, out var left) && left == 0) _greetingFrom.TryRemove(new KeyValuePair<IPAddress, int>(from, 0));
        }

        void Greet(TcpClient c, IPAddress from)
        {
            bool greeting = true;
            // one deadline for the whole greeting (the read timeout alone restarted with every byte trickled in)
            var open = new[] { true };
            using var deadline = new Timer(_ => { if (Volatile.Read(ref open[0])) try { c.Dispose(); } catch { } }, null, HandshakeTimeoutMs, Timeout.Infinite);
            try
            {
                c.NoDelay = true;
                c.SendTimeout = SendTimeoutMs;
                var s = c.GetStream();
                s.ReadTimeout = HandshakeTimeoutMs;
                var idBuf = ReadExact(s, 8);
                int keyLen = ReadExact(s, 1)[0];
                if (keyLen > MaxKeyBytes) { c.Dispose(); return; }
                var key = ReadExact(s, keyLen);
                if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(key, _key))
                {
                    Thread.Sleep(WrongKeyDelayMs);   // still counted as greeting: guesses stay slow
                    c.Dispose(); return;
                }
                var conn = new Conn { Id = BinaryPrimitives.ReadUInt64LittleEndian(idBuf), Client = c, Stream = s };
                // An id already connected is refused, not taken over (a newcomer must not evict a player), and no
                // one may use the host's own id or 0.
                if (_disposed || conn.Id == 0 || conn.Id == LocalId || !_conns.TryAdd(conn.Id, conn)) { c.Dispose(); return; }
                // the cap again now that it counts: several greetings can finish at once
                if (_conns.Count > MaxConnections) { _conns.TryRemove(conn.Id, out _); c.Dispose(); return; }
                Volatile.Write(ref open[0], false);
                EndGreeting(from); greeting = false;
                // its Hello must follow soon (one deadline, not per read); then reads wait as long as needed
                conn.FirstFrame = new Timer(_ => { if (!conn.GotFrame) Kill(conn); }, null, FirstFrameTimeoutMs, Timeout.Infinite);
                s.ReadTimeout = FirstFrameTimeoutMs;
                ReadLoop(conn);
            }
            catch { c.Dispose(); }
            finally { Volatile.Write(ref open[0], false); if (greeting) EndGreeting(from); }
        }

        void StartReader(Conn conn)
        {
            conn.Stream.ReadTimeout = Timeout.Infinite;
            new Thread(() => ReadLoop(conn)) { IsBackground = true, Name = "IronNestCoop.TcpRead" }.Start();
        }

        void ReadLoop(Conn conn)
        {
            try
            {
                while (!_disposed && !conn.Dead)
                {
                    var hdr = ReadExact(conn.Stream, 4);
                    int len = BinaryPrimitives.ReadInt32LittleEndian(hdr);
                    if (len < 1 || len > MaxFrame) throw new IOException($"bad frame length {len}");
                    var frame = ReadExact(conn.Stream, len);
                    if (!conn.GotFrame) { conn.GotFrame = true; conn.FirstFrame?.Dispose(); }
                    if (conn.Stream.ReadTimeout != Timeout.Infinite) conn.Stream.ReadTimeout = Timeout.Infinite;
                    if (frame[0] != (byte)Delivery.Reliable && frame[0] != (byte)Delivery.Unreliable) throw new IOException($"bad delivery {frame[0]}");
                    var data = new byte[len - 1];
                    Buffer.BlockCopy(frame, 1, data, 0, len - 1);
                    if (Interlocked.Add(ref conn.Queued, data.Length) > MaxQueuedBytesPerConn) throw new IOException("peer sends faster than we read");
                    _inbox.Enqueue(new Packet { From = conn.Id, Data = data, Delivery = (Delivery)frame[0], Conn = conn });
                }
            }
            catch { }
            Kill(conn);
        }

        static byte[] ReadExact(Stream s, int n)
        {
            var b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) throw new EndOfStreamException();
                got += r;
            }
            return b;
        }

        void Kill(Conn c)
        {
            c.Dead = true;
            try { c.Client.Dispose(); } catch { }
            _conns.TryRemove(new KeyValuePair<ulong, Conn>(c.Id, c));
        }

        public int MaxPayload(Delivery d) => d == Delivery.Reliable ? MaxReliable : MaxUnreliable;

        public bool Send(ulong peer, byte[] data, int offset, int count, Delivery d)
        {
            if (count > MaxPayload(d) || !_conns.TryGetValue(peer, out var c) || c.Dead) return false;
            var frame = new byte[5 + count];
            BinaryPrimitives.WriteInt32LittleEndian(frame, count + 1);
            frame[4] = (byte)d;
            Buffer.BlockCopy(data, offset, frame, 5, count);
            try
            {
                lock (c.SendLock) c.Stream.Write(frame, 0, frame.Length);   // SendTimeout: a frozen peer is dropped
                return true;
            }
            catch { Kill(c); return false; }
        }

        public void Poll(List<Packet> received)
        {
            while (_inbox.TryDequeue(out var p))
            {
                if (p.Conn is Conn c) Interlocked.Add(ref c.Queued, -p.Data.Length);
                p.Conn = null;
                received.Add(p);
            }
        }

        public void Close(ulong peer)
        {
            if (_conns.TryGetValue(peer, out var c)) Kill(c);
        }

        public string Describe() => _desc;

        public void Dispose()
        {
            _disposed = true;
            try { _listener?.Stop(); } catch { }
            foreach (var c in _conns.Values) Kill(c);
        }
    }
}
