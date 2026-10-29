using System;
using System.Collections.Generic;

namespace IronNestCoop.Net.Transport
{
    /// <summary>
    /// In-process network for unit tests and the fuzzer. Simulates latency, jitter and loss
    /// (unreliable only; reliable is delayed but never lost or reordered, like Steam).
    /// Time is driven by the test via <see cref="Now"/>, so runs are deterministic for a seed.
    /// </summary>
    public sealed class MemoryHub
    {
        readonly Dictionary<ulong, MemoryTransport> _nodes = new();
        readonly Random _rng;
        readonly List<InFlight> _flight = new();
        long _order;

        public double Now;
        public double LatencyMs;
        public double JitterMs;
        public double LossRate;
        public double DuplicateRate;

        public MemoryHub(int seed = 1) { _rng = new Random(seed); }

        struct InFlight
        {
            public ulong From, To;
            public byte[] Data;
            public Delivery Delivery;
            public double DueAt;
            public long Order;
        }

        public MemoryTransport Create(ulong id)
        {
            var t = new MemoryTransport(this, id);
            _nodes[id] = t;
            return t;
        }

        internal void Remove(ulong id) => _nodes.Remove(id);

        /// <summary>Simulates a cable pull: the node stops receiving and its sends vanish.</summary>
        public void Partition(ulong id, bool cut)
        {
            if (_nodes.TryGetValue(id, out var n)) n.Cut = cut;
        }

        internal bool Enqueue(ulong from, ulong to, byte[] data, Delivery d)
        {
            if (!_nodes.TryGetValue(to, out var dst)) return false;
            if (_nodes.TryGetValue(from, out var src) && src.Cut) return true;
            if (dst.Cut) return true;
            double delay = LatencyMs + (JitterMs > 0 ? _rng.NextDouble() * JitterMs : 0);
            if (d == Delivery.Unreliable)
            {
                if (_rng.NextDouble() < LossRate) return true;
                Add(from, to, data, d, Now + delay / 1000.0);
                if (_rng.NextDouble() < DuplicateRate) Add(from, to, data, d, Now + (delay + JitterMs) / 1000.0);
                return true;
            }
            // Reliable: keep per-pair order by never scheduling before the last reliable to the same pair.
            double due = Now + delay / 1000.0;
            for (int i = _flight.Count - 1; i >= 0; i--)
            {
                var f = _flight[i];
                if (f.From == from && f.To == to && f.Delivery == Delivery.Reliable) { due = Math.Max(due, f.DueAt); break; }
            }
            Add(from, to, data, d, due);
            return true;
        }

        void Add(ulong from, ulong to, byte[] data, Delivery d, double due)
        {
            _flight.Add(new InFlight { From = from, To = to, Data = data, Delivery = d, DueAt = due, Order = _order++ });
        }

        internal void Deliver(ulong to, List<Packet> into)
        {
            // Collect due packets for this node in (due, order) order.
            List<InFlight> due = null;
            for (int i = 0; i < _flight.Count; i++)
            {
                var f = _flight[i];
                if (f.To != to || f.DueAt > Now) continue;
                (due ??= new List<InFlight>()).Add(f);
                _flight.RemoveAt(i--);
            }
            if (due == null) return;
            due.Sort((a, b) => a.DueAt != b.DueAt ? a.DueAt.CompareTo(b.DueAt) : a.Order.CompareTo(b.Order));
            foreach (var f in due) into.Add(new Packet { From = f.From, Data = f.Data, Delivery = f.Delivery });
        }
    }

    public sealed class MemoryTransport : ITransport
    {
        readonly MemoryHub _hub;
        internal bool Cut;

        internal MemoryTransport(MemoryHub hub, ulong id) { _hub = hub; LocalId = id; }

        public ulong LocalId { get; }

        public int MaxPayload(Delivery d) => d == Delivery.Reliable ? 512 * 1024 : 1200;

        public bool Send(ulong peer, byte[] data, int offset, int count, Delivery d)
        {
            if (count > MaxPayload(d)) return false;
            var copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            return _hub.Enqueue(LocalId, peer, copy, d);
        }

        public void Poll(List<Packet> received)
        {
            if (!Cut) _hub.Deliver(LocalId, received);
        }

        public void Close(ulong peer) { }

        public string Describe() => $"memory node {LocalId}";

        public void Dispose() => _hub.Remove(LocalId);
    }
}
