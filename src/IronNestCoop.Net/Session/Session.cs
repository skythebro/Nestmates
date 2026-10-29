using System;
using System.Collections.Generic;
using System.Diagnostics;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;

namespace IronNestCoop.Net.Session
{
    public enum SessionRole { None, Host, Guest }
    public enum SessionState { Idle, Connecting, Connected, Closed }

    public sealed class Player
    {
        public byte Slot;
        public ulong PeerId;
        public string Name;
        /// <summary>Opaque per-player data owned by the game layer (cat look, colours, ...).</summary>
        public byte[] Info;
        public float RttMs;
        public bool IsLocal;
        internal double LastHeard;
        internal double NextPing;
    }

    public readonly struct MsgContext
    {
        public readonly byte FromSlot;
        public readonly Delivery Delivery;
        public MsgContext(byte from, Delivery d) { FromSlot = from; Delivery = d; }
        public bool FromHost => FromSlot == Protocol.HostSlot;
    }

    public delegate void MessageHandler(in MsgContext ctx, NetReader body);

    /// <summary>
    /// Star-topology session over an <see cref="ITransport"/>: one host, up to four guests.
    /// Guests talk only to the host; the host relays guest broadcasts to the other guests.
    /// Single-threaded: call <see cref="Tick"/> from the game's main thread every frame.
    /// </summary>
    public sealed class Session : IDisposable
    {
        readonly ITransport _t;
        readonly Func<double> _now;
        readonly Dictionary<ushort, MessageHandler> _handlers = new();
        readonly List<Packet> _rx = new();
        readonly NetWriter _w = new(1024);
        readonly Fragmenter _frag = new();
        readonly Player[] _slots = new Player[Protocol.MaxPlayers];
        readonly Dictionary<ulong, Player> _byPeer = new();
        readonly HashSet<ulong> _kicked = new();

        ulong _hostPeer;
        double _nextHello, _joinDeadline;
        double _hostClockOffset; bool _haveOffset; int _pongSamples;
        string _localName;
        byte[] _localInfo;

        public string ModVersion { get; }
        public SessionRole Role { get; private set; }
        public SessionState State { get; private set; }
        public byte LocalSlot { get; private set; } = 0xFE;
        public string CloseReason { get; private set; }
        public bool IsHost => Role == SessionRole.Host;
        public bool IsConnected => State == SessionState.Connected;
        public ITransport Transport => _t;

        /// <summary>Host only: return a reason to refuse a joiner (ban list, game state), or null.</summary>
        public Func<ulong, string, string> AdmitFilter;

        public event Action<Player> PlayerJoined;
        public event Action<Player, string> PlayerLeft;
        public event Action RosterChanged;
        public event Action Connected;
        public event Action<string> Closed;
        public event Action<string> Log;

        /// <summary>Counters for the debug overlay and tests.</summary>
        public long BytesSent, BytesReceived, PacketsSent, PacketsReceived, MalformedDropped;

        /// <param name="clock">Seconds, monotonic. Defaults to a Stopwatch; tests pass a fake clock.</param>
        public Session(ITransport transport, string modVersion, Func<double> clock = null)
        {
            _t = transport;
            ModVersion = modVersion;
            if (clock == null) { var sw = Stopwatch.StartNew(); clock = () => sw.Elapsed.TotalSeconds; }
            _now = clock;
        }

        public double LocalTime => _now();

        /// <summary>Estimated host clock (seconds). On the host it is the local clock.</summary>
        public double HostTime => IsHost || !_haveOffset ? LocalTime : LocalTime + _hostClockOffset;

        public IEnumerable<Player> Players
        {
            get { foreach (var p in _slots) if (p != null) yield return p; }
        }

        public Player GetPlayer(byte slot) => slot < _slots.Length ? _slots[slot] : null;
        public Player LocalPlayer => GetPlayer(LocalSlot);
        public int PlayerCount { get { int n = 0; foreach (var p in _slots) if (p != null) n++; return n; } }

        public void On(ushort type, MessageHandler h) => _handlers[type] = h;

        // ------------------------------------------------------------------ lifecycle

        public void Host(string name, byte[] info)
        {
            if (State != SessionState.Idle) throw new InvalidOperationException("session already used");
            Role = SessionRole.Host; State = SessionState.Connected; LocalSlot = Protocol.HostSlot;
            _localName = name = Sanitize(name); _localInfo = info = ClampInfo(info);
            _slots[0] = new Player { Slot = 0, PeerId = _t.LocalId, Name = name, Info = info, IsLocal = true };
            Emit($"hosting as '{name}' on {_t.Describe()}");
            RosterChanged?.Invoke();
        }

        public void Join(ulong hostPeer, string name, byte[] info)
        {
            if (State != SessionState.Idle) throw new InvalidOperationException("session already used");
            Role = SessionRole.Guest; State = SessionState.Connecting;
            _hostPeer = hostPeer; _localName = Sanitize(name); _localInfo = ClampInfo(info);
            _nextHello = 0; _joinDeadline = LocalTime + Protocol.JoinTimeoutSec;
            Emit($"joining host {hostPeer} via {_t.Describe()}");
        }

        /// <summary>Update this player's info blob (e.g. cat customisation changed) and share it.</summary>
        public void SetLocalInfo(string name, byte[] info)
        {
            _localName = name = Sanitize(name); _localInfo = info = ClampInfo(info);
            var me = LocalPlayer;
            if (me != null) { me.Name = name; me.Info = info; }
            if (!IsConnected) return;
            if (IsHost) { BroadcastRoster(); RosterChanged?.Invoke(); }
            else
            {
                _w.Reset(); _w.U8((byte)Protocol.Kind.PlayerInfo); _w.Str(name); _w.Bytes(info);
                Raw(_hostPeer, Delivery.Reliable);
            }
        }

        public void Leave(string reason = "left")
        {
            if (State == SessionState.Closed || State == SessionState.Idle) { State = SessionState.Closed; return; }
            _w.Reset(); _w.U8((byte)Protocol.Kind.Bye); _w.Str(reason);
            if (IsHost) foreach (var p in _slots) { if (p != null && !p.IsLocal) Raw(p.PeerId, Delivery.Reliable); }
            else Raw(_hostPeer, Delivery.Reliable);
            Close(reason);
        }

        /// <summary>Host only: remove a guest. If <paramref name="blockRejoin"/> they cannot rejoin this session.</summary>
        public void Kick(byte slot, string reason, bool blockRejoin)
        {
            if (!IsHost || slot == 0 || slot >= _slots.Length) return;
            var p = _slots[slot];
            if (p == null) return;
            if (blockRejoin) _kicked.Add(p.PeerId);
            _w.Reset(); _w.U8((byte)Protocol.Kind.Bye); _w.Str(reason);
            Raw(p.PeerId, Delivery.Reliable);
            DropPlayer(p, reason);
        }

        void Close(string reason)
        {
            if (State == SessionState.Closed) return;
            State = SessionState.Closed; CloseReason = reason;
            Emit($"session closed: {reason}");
            Closed?.Invoke(reason);
        }

        public void Dispose()
        {
            if (State != SessionState.Closed && State != SessionState.Idle) Leave("shutdown");
        }

        // ------------------------------------------------------------------ sending game data

        /// <summary>
        /// Send a game message. target: a slot, or <see cref="Protocol.TargetAll"/> for everyone else.
        /// Guests sending to TargetAll go via the host, which handles it too.
        /// </summary>
        public void Send(ushort type, NetWriter body, Delivery d, byte target = Protocol.TargetAll)
        {
            if (!IsConnected) return;
            var pkt = BuildData(type, LocalSlot, target, body.Buffer, 0, body.Length);
            if (IsHost)
            {
                if (target == Protocol.TargetAll)
                    foreach (var p in _slots) { if (p != null && !p.IsLocal) SendData(p.PeerId, pkt, d); }
                else if (target != LocalSlot && target < _slots.Length && _slots[target] is Player p) SendData(p.PeerId, pkt, d);
            }
            else if (target != LocalSlot) SendData(_hostPeer, pkt, d);
        }

        public void SendToHost(ushort type, NetWriter body, Delivery d) => Send(type, body, d, Protocol.HostSlot);

        static byte[] BuildData(ushort type, byte origin, byte target, byte[] body, int off, int len)
        {
            var pkt = new byte[5 + len];
            pkt[0] = (byte)Protocol.Kind.Data;
            pkt[1] = (byte)type; pkt[2] = (byte)(type >> 8);
            pkt[3] = origin; pkt[4] = target;
            Buffer.BlockCopy(body, off, pkt, 5, len);
            return pkt;
        }

        void SendData(ulong peer, byte[] pkt, Delivery d)
        {
            if (pkt.Length <= _t.MaxPayload(d)) { Count(_t.Send(peer, pkt, 0, pkt.Length, d), pkt.Length); return; }
            // Too big: reliable fragments (an oversized unreliable message is promoted, never dropped).
            foreach (var piece in _frag.Split(pkt, _t.MaxPayload(Delivery.Reliable)))
                Count(_t.Send(peer, piece, 0, piece.Length, Delivery.Reliable), piece.Length);
        }

        void Raw(ulong peer, Delivery d) => Count(_t.Send(peer, _w.Buffer, 0, _w.Length, d), _w.Length);

        void Count(bool ok, int n)
        {
            if (!ok) return;
            PacketsSent++; BytesSent += n;
        }

        // ------------------------------------------------------------------ pump

        public void Tick()
        {
            if (State == SessionState.Idle || State == SessionState.Closed) return;
            double now = LocalTime;
            _rx.Clear();
            _t.Poll(_rx);
            foreach (var p in _rx)
            {
                PacketsReceived++; BytesReceived += p.Data.Length;
                try { Handle(p.From, p.Data, p.Delivery, now); }
                catch (MalformedMessageException e) { MalformedDropped++; EmitLimited($"dropped malformed packet from {p.From}: {e.Message}", now); }
                catch (Exception e) { EmitLimited($"packet from {p.From} failed: {e}", now); }   // one bad packet must not drop the rest
                if (State == SessionState.Closed) return;
            }
            if (_frag.Expire(now) > 0) Emit("dropped an incomplete fragmented message (timed out)");
            if (Role == SessionRole.Guest) TickGuest(now); else TickHost(now);
        }

        void TickGuest(double now)
        {
            if (State == SessionState.Connecting)
            {
                if (now >= _joinDeadline) { Close("could not reach the host (timed out)"); return; }
                if (now >= _nextHello)
                {
                    _nextHello = now + Protocol.HelloIntervalSec;
                    _w.Reset(); _w.U8((byte)Protocol.Kind.Hello); _w.U16(Protocol.Version); _w.Str(ModVersion);
                    _w.Str(_localName); _w.Bytes(_localInfo);
                    Raw(_hostPeer, Delivery.Reliable);
                }
                return;
            }
            var host = _slots[0];
            if (host == null) return;
            if (now - host.LastHeard > Protocol.TimeoutSec) { Close("lost connection to the host"); return; }
            if (now >= host.NextPing) { host.NextPing = now + Protocol.PingIntervalSec; SendPing(_hostPeer, now); }
        }

        void TickHost(double now)
        {
            foreach (var p in _slots)
            {
                if (p == null || p.IsLocal) continue;
                if (now - p.LastHeard > Protocol.TimeoutSec) { DropPlayer(p, "timed out"); continue; }
                if (now >= p.NextPing) { p.NextPing = now + Protocol.PingIntervalSec; SendPing(p.PeerId, now); }
            }
        }

        void SendPing(ulong peer, double now)
        {
            _w.Reset(); _w.U8((byte)Protocol.Kind.Ping); _w.F64(now);
            Raw(peer, Delivery.Unreliable);
        }

        // ------------------------------------------------------------------ receive

        void Handle(ulong from, byte[] data, Delivery d, double now)
        {
            if (data.Length == 0) throw new MalformedMessageException("empty packet");
            var kind = (Protocol.Kind)data[0];
            var r = new NetReader(data, 1, data.Length - 1);

            if (_byPeer.TryGetValue(from, out var known)) known.LastHeard = now;

            switch (kind)
            {
                case Protocol.Kind.Hello: if (IsHost) OnHello(from, r, now); return;
                case Protocol.Kind.Welcome: if (!IsHost && from == _hostPeer) OnWelcome(r, now); return;
                case Protocol.Kind.Reject: if (!IsHost && from == _hostPeer) Close("host refused: " + r.Str(1024)); return;
                case Protocol.Kind.Bye: OnBye(from, r.Str(1024)); return;
                case Protocol.Kind.Ping:
                    if (known == null) return;
                    _w.Reset(); _w.U8((byte)Protocol.Kind.Pong); _w.F64(r.F64()); _w.F64(now);
                    Raw(from, Delivery.Unreliable);
                    return;
                case Protocol.Kind.Pong: if (known != null) OnPong(known, r, now); return;
                case Protocol.Kind.Roster: if (!IsHost && from == _hostPeer && State == SessionState.Connected) OnRoster(r); return;
                case Protocol.Kind.PlayerInfo:
                    if (IsHost && known != null)
                    {
                        known.Name = Sanitize(r.Str(256)); known.Info = r.Bytes(Protocol.MaxInfoBytes);
                        BroadcastRoster(); RosterChanged?.Invoke();
                    }
                    return;
                case Protocol.Kind.Fragment:
                    if (known == null) return;
                    var whole = _frag.Accept(from, r, _t.MaxPayload(Delivery.Reliable), now);
                    if (whole == null) return;
                    if (whole.Length > 0 && whole[0] == (byte)Protocol.Kind.Fragment) throw new MalformedMessageException("nested fragment");
                    Handle(from, whole, Delivery.Reliable, now);
                    return;
                case Protocol.Kind.Data:
                    if (known == null || State != SessionState.Connected) return;
                    OnData(known, data, d);
                    return;
                default:
                    throw new MalformedMessageException($"unknown kind {data[0]}");
            }
        }

        void OnHello(ulong from, NetReader r, double now)
        {
            ushort proto = r.U16();
            string ver = r.Str(64);
            string name = Sanitize(r.Str(256));
            byte[] info = r.Bytes(Protocol.MaxInfoBytes);

            if (_byPeer.TryGetValue(from, out var existing)) { SendWelcome(existing); return; } // hello retry

            string reason = null;
            if (proto != Protocol.Version || ver != ModVersion)
                reason = $"version mismatch: host runs Nestmates {ModVersion} (protocol {Protocol.Version}), you run {ver} (protocol {proto})";
            else if (_kicked.Contains(from)) reason = "you were removed from this lobby";
            else reason = AdmitFilter?.Invoke(from, name);

            byte slot = 0;
            if (reason == null)
            {
                for (byte i = 1; i < _slots.Length; i++) if (_slots[i] == null) { slot = i; break; }
                if (slot == 0) reason = "lobby is full";
            }
            if (reason != null)
            {
                _w.Reset(); _w.U8((byte)Protocol.Kind.Reject); _w.Str(reason);
                Raw(from, Delivery.Reliable);
                EmitLimited($"refused {name} ({from}): {reason}", now);
                _t.Close(from);
                return;
            }
            var p = new Player { Slot = slot, PeerId = from, Name = name, Info = info, LastHeard = now, NextPing = now };
            _slots[slot] = p; _byPeer[from] = p;
            SendWelcome(p);
            BroadcastRoster();
            Emit($"{name} joined as player {slot + 1}");
            PlayerJoined?.Invoke(p);
            RosterChanged?.Invoke();
        }

        void SendWelcome(Player p)
        {
            _w.Reset(); _w.U8((byte)Protocol.Kind.Welcome); _w.U8(p.Slot); _w.F64(LocalTime);
            Raw(p.PeerId, Delivery.Reliable);
        }

        void OnWelcome(NetReader r, double now)
        {
            if (State == SessionState.Connected) return;
            byte slot = r.U8();
            double hostNow = r.F64();
            if (slot == Protocol.HostSlot || slot >= _slots.Length) throw new MalformedMessageException($"welcome with bad slot {slot}");
            if (double.IsNaN(hostNow) || double.IsInfinity(hostNow)) throw new MalformedMessageException("welcome with bad clock");
            LocalSlot = slot;
            _hostClockOffset = hostNow - now; // rough (ignores latency): the first pong replaces it
            _haveOffset = true; _pongSamples = 0;
            State = SessionState.Connected;
            var host = new Player { Slot = 0, PeerId = _hostPeer, Name = "host", LastHeard = now, NextPing = now };
            _slots[0] = host; _byPeer[_hostPeer] = host;
            _slots[LocalSlot] = new Player { Slot = LocalSlot, PeerId = _t.LocalId, Name = _localName, Info = _localInfo, IsLocal = true };
            Emit($"connected as player {LocalSlot + 1}");
            Connected?.Invoke();
            RosterChanged?.Invoke();
        }

        void OnPong(Player p, NetReader r, double now)
        {
            double sent = r.F64(), remote = r.F64();
            double rtt = now - sent;
            if (!(rtt >= 0 && rtt <= 30) || double.IsNaN(remote) || double.IsInfinity(remote)) return;   // also rejects NaN
            p.RttMs = p.RttMs == 0 ? (float)(rtt * 1000) : p.RttMs * 0.8f + (float)(rtt * 1000) * 0.2f;
            if (!IsHost && p.Slot == 0)
            {
                double offset = remote + rtt / 2 - now;
                // Smooth, but jump on first sample or big change (host hitch, clock reset).
                _hostClockOffset = _pongSamples++ == 0 || Math.Abs(offset - _hostClockOffset) > 0.5 ? offset : _hostClockOffset * 0.9 + offset * 0.1;
                _haveOffset = true;
            }
        }

        void OnBye(ulong from, string reason)
        {
            if (IsHost)
            {
                if (_byPeer.TryGetValue(from, out var p)) DropPlayer(p, reason ?? "left");
            }
            else if (from == _hostPeer) Close(State == SessionState.Connected ? $"host ended the session ({reason})" : $"host refused: {reason}");
        }

        void DropPlayer(Player p, string reason)
        {
            _slots[p.Slot] = null;
            _byPeer.Remove(p.PeerId);
            _frag.Forget(p.PeerId);
            _t.Close(p.PeerId);
            Emit($"{p.Name} left: {reason}");
            BroadcastRoster();
            PlayerLeft?.Invoke(p, reason);
            RosterChanged?.Invoke();
        }

        void BroadcastRoster()
        {
            if (!IsHost) return;
            _w.Reset(); _w.U8((byte)Protocol.Kind.Roster);
            int n = 0; foreach (var p in _slots) if (p != null) n++;
            _w.U8((byte)n);
            foreach (var p in _slots)
            {
                if (p == null) continue;
                _w.U8(p.Slot); _w.U64(p.PeerId); _w.Str(p.Name); _w.Bytes(p.Info); _w.F32(p.RttMs);
            }
            foreach (var p in _slots) if (p != null && !p.IsLocal) Raw(p.PeerId, Delivery.Reliable);
        }

        void OnRoster(NetReader r)
        {
            int n = r.U8();
            var seen = new bool[_slots.Length];
            for (int i = 0; i < n; i++)
            {
                byte slot = r.U8(); ulong peer = r.U64(); string name = Sanitize(r.Str(256)); byte[] info = r.Bytes(Protocol.MaxInfoBytes); float rtt = r.F32();
                if (float.IsNaN(rtt) || float.IsInfinity(rtt)) rtt = 0;
                if (slot >= _slots.Length) throw new MalformedMessageException("bad slot");
                seen[slot] = true;
                var p = _slots[slot];
                bool isNew = p == null || p.PeerId != peer;
                if (isNew)
                {
                    if (p != null && !p.IsLocal) PlayerLeft?.Invoke(p, "replaced");
                    p = new Player { Slot = slot, PeerId = peer, LastHeard = LocalTime };
                    _slots[slot] = p;
                    if (slot == 0) { _byPeer[_hostPeer] = p; p.PeerId = _hostPeer; }
                }
                p.IsLocal = slot == LocalSlot;
                if (!p.IsLocal) { p.Name = name; p.Info = info; }
                if (slot != 0 && !p.IsLocal) p.RttMs = rtt;
                if (isNew && !p.IsLocal && slot != 0) PlayerJoined?.Invoke(p);
                if (isNew && slot == 0) PlayerJoined?.Invoke(p);
            }
            for (byte s = 0; s < _slots.Length; s++)
            {
                if (seen[s] || _slots[s] == null || _slots[s].IsLocal) continue;
                var gone = _slots[s]; _slots[s] = null;
                PlayerLeft?.Invoke(gone, "left");
            }
            RosterChanged?.Invoke();
        }

        // Per guest and second: what the host passes on (and handles) at most. Far above what play sends; a flood is
        // dropped instead of being multiplied to every other player.
        const int RelayPacketsPerSecond = 2000, RelayBytesPerSecond = 8 << 20;
        readonly double[] _relayWindow = new double[Protocol.MaxPlayers];
        readonly int[] _relayPackets = new int[Protocol.MaxPlayers], _relayBytes = new int[Protocol.MaxPlayers];
        readonly bool[] _relayWarned = new bool[Protocol.MaxPlayers];

        bool WithinRelayBudget(byte slot, int bytes)
        {
            if (slot >= _relayWindow.Length) return false;
            double now = _now();
            if (now - _relayWindow[slot] >= 1.0) { _relayWindow[slot] = now; _relayPackets[slot] = 0; _relayBytes[slot] = 0; _relayWarned[slot] = false; }
            _relayPackets[slot]++; _relayBytes[slot] += bytes;
            if (_relayPackets[slot] <= RelayPacketsPerSecond && _relayBytes[slot] <= RelayBytesPerSecond) return true;
            if (!_relayWarned[slot]) { _relayWarned[slot] = true; Emit($"slot {slot} sends more than play can make in a second: dropping its messages for now"); }
            return false;
        }

        void OnData(Player from, byte[] pkt, Delivery d)
        {
            if (pkt.Length < 5) throw new MalformedMessageException("short data");
            ushort type = (ushort)(pkt[1] | pkt[2] << 8);
            byte origin = pkt[3], target = pkt[4];
            if (IsHost)
            {
                // Guests may not speak for someone else.
                if (origin != from.Slot) throw new MalformedMessageException($"slot {from.Slot} claimed origin {origin}");
                if (!WithinRelayBudget(origin, pkt.Length)) return;
                if (target == Protocol.TargetAll)
                    foreach (var p in _slots) { if (p != null && !p.IsLocal && p.Slot != origin) SendData(p.PeerId, pkt, d); }
                else if (target != Protocol.HostSlot)
                {
                    if (target < _slots.Length && _slots[target] is Player t && !t.IsLocal) SendData(t.PeerId, pkt, d);
                    return;
                }
            }
            else if (from.Slot != 0) return; // guests only accept from the host
            Dispatch(type, origin, d, pkt);
        }

        void Dispatch(ushort type, byte origin, Delivery d, byte[] pkt)
        {
            if (!_handlers.TryGetValue(type, out var h)) return;
            var body = new NetReader(pkt, 5, pkt.Length - 5);
            try { h(new MsgContext(origin, d), body); }
            catch (MalformedMessageException) { throw; }
            catch (Exception e) { Emit($"handler for message {type} threw: {e}"); }
        }

        /// <summary>Player names: no control characters (CR/LF), no rich-text brackets, at most 32 characters.</summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Gunner";
            s = s.Trim();
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s) if (!char.IsControl(c) && c != '<' && c != '>') sb.Append(c);
            var r = sb.ToString().Trim();
            if (r.Length > 32) r = r.Substring(0, char.IsHighSurrogate(r[31]) ? 31 : 32);
            return r.Length == 0 ? "Gunner" : r;
        }

        /// <summary>An info blob over the limit would make the host refuse the Hello; send none instead.</summary>
        byte[] ClampInfo(byte[] info)
        {
            if (info == null || info.Length <= Protocol.MaxInfoBytes) return info;
            Emit($"player info of {info.Length} bytes is over {Protocol.MaxInfoBytes}; not shared");
            return null;
        }

        void Emit(string m) => Log?.Invoke(m);

        double _logWindow; int _logCount;

        /// <summary>Peer-triggered log lines: at most 20 per 10 s, so a hostile peer can't flood the log.</summary>
        void EmitLimited(string m, double now)
        {
            if (now - _logWindow > 10) { _logWindow = now; _logCount = 0; }
            if (++_logCount <= 20) Emit(m);
            else if (_logCount == 21) Emit("(further packet errors suppressed for 10 s)");
        }
    }
}
