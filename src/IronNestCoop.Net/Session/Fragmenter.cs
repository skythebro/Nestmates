using System;
using System.Collections.Generic;
using IronNestCoop.Net.Wire;

namespace IronNestCoop.Net.Session
{
    /// <summary>
    /// Splits packets larger than the transport limit into reliable pieces and reassembles them.
    /// Piece: u8 Kind.Fragment | u32 messageId | u16 index | u16 count | bytes.
    /// Relies on reliable, in-order delivery, but tolerates interleaving of different messages.
    /// </summary>
    public sealed class Fragmenter
    {
        public const int HeaderSize = 9;
        const int MaxPieces = 4096;
        const int MaxPendingPerPeer = 8;
        /// <summary>Largest reassembled message (a peer must not make us allocate more).</summary>
        public const int MaxMessageBytes = 16 << 20;
        /// <summary>A partial message not completed within this many seconds is dropped.</summary>
        public const double PendingTimeoutSec = 60;

        sealed class Pending
        {
            public byte[][] Pieces;
            public int Received;
            public long Bytes;
            public double Started;
        }

        readonly Dictionary<(ulong, uint), Pending> _pending = new();
        readonly Dictionary<ulong, int> _pendingCount = new();
        uint _nextId = 1;

        public IEnumerable<byte[]> Split(byte[] packet, int maxPayload)
        {
            int chunk = maxPayload - HeaderSize;
            if (chunk <= 0) throw new ArgumentException("payload limit too small");
            int count = (packet.Length + chunk - 1) / chunk;
            if (count > MaxPieces) throw new ArgumentException($"message of {packet.Length} bytes is too large");
            uint id = _nextId++;
            for (int i = 0; i < count; i++)
            {
                int off = i * chunk, len = Math.Min(chunk, packet.Length - off);
                var piece = new byte[HeaderSize + len];
                piece[0] = (byte)Protocol.Kind.Fragment;
                BitConverter.TryWriteBytes(piece.AsSpan(1), id);
                BitConverter.TryWriteBytes(piece.AsSpan(5), (ushort)i);
                BitConverter.TryWriteBytes(piece.AsSpan(7), (ushort)count);
                Buffer.BlockCopy(packet, off, piece, HeaderSize, len);
                yield return piece;
            }
        }

        /// <summary>
        /// Feed a fragment (reader positioned after the kind byte). Returns the whole packet when complete.
        /// <paramref name="maxPiece"/>: the largest piece the sender could legitimately send (transport limit).
        /// </summary>
        public byte[] Accept(ulong from, NetReader r, int maxPiece = int.MaxValue, double now = 0)
        {
            uint id = r.U32();
            int index = r.U16(), count = r.U16();
            if (count == 0 || count > MaxPieces || index >= count) throw new MalformedMessageException($"bad fragment {index}/{count}");
            if (r.Remaining > maxPiece - HeaderSize) throw new MalformedMessageException($"fragment of {r.Remaining} bytes exceeds the transport limit");
            var data = r.Raw(r.Remaining);

            var key = (from, id);
            if (!_pending.TryGetValue(key, out var p))
            {
                _pendingCount.TryGetValue(from, out int n);
                if (n >= MaxPendingPerPeer) throw new MalformedMessageException("too many partial messages");
                p = new Pending { Pieces = new byte[count][], Started = now };
                _pending[key] = p;
                _pendingCount[from] = n + 1;
            }
            if (p.Pieces.Length != count) throw new MalformedMessageException("fragment count changed");
            if (p.Pieces[index] != null) return null; // duplicate
            p.Pieces[index] = data; p.Received++; p.Bytes += data.Length;
            if (p.Bytes > MaxMessageBytes) { Drop(key); throw new MalformedMessageException($"fragmented message over {MaxMessageBytes} bytes"); }
            if (p.Received < count) return null;

            Drop(key);
            var whole = new byte[p.Bytes];
            int off = 0;
            foreach (var piece in p.Pieces) { Buffer.BlockCopy(piece, 0, whole, off, piece.Length); off += piece.Length; }
            return whole;
        }

        void Drop((ulong, uint) key)
        {
            if (!_pending.Remove(key)) return;
            if (_pendingCount.TryGetValue(key.Item1, out int n)) _pendingCount[key.Item1] = Math.Max(0, n - 1);
        }

        /// <summary>Drop partial messages older than <see cref="PendingTimeoutSec"/>. Returns how many.</summary>
        public int Expire(double now)
        {
            List<(ulong, uint)> old = null;
            foreach (var kv in _pending) if (now - kv.Value.Started > PendingTimeoutSec) (old ??= new()).Add(kv.Key);
            if (old == null) return 0;
            foreach (var k in old) Drop(k);
            return old.Count;
        }

        public int PendingCount => _pending.Count;

        public void Forget(ulong peer)
        {
            var dead = new List<(ulong, uint)>();
            foreach (var k in _pending.Keys) if (k.Item1 == peer) dead.Add(k);
            foreach (var k in dead) _pending.Remove(k);
            _pendingCount.Remove(peer);
        }
    }
}
