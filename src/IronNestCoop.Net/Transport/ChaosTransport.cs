using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace IronNestCoop.Net.Transport
{
    /// <summary>
    /// Decorator that adds latency, jitter and loss to any transport (receive side).
    /// Reliable packets are delayed but stay in order and are never dropped, matching Steam.
    /// Enabled in test runs from the command line (-coopchaos=latencyMs,jitterMs,lossPct).
    /// </summary>
    public sealed class ChaosTransport : ITransport
    {
        readonly ITransport _inner;
        readonly Random _rng;
        readonly List<Packet> _tmp = new();
        readonly List<(double due, long order, Packet p)> _held = new();
        readonly Dictionary<ulong, double> _lastReliableDue = new();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        long _order;

        public double LatencyMs, JitterMs, LossRate;

        public ChaosTransport(ITransport inner, double latencyMs, double jitterMs, double lossRate, int seed = 7)
        {
            _inner = inner; LatencyMs = latencyMs; JitterMs = jitterMs; LossRate = lossRate;
            _rng = new Random(seed);
        }

        public ulong LocalId => _inner.LocalId;
        public int MaxPayload(Delivery d) => _inner.MaxPayload(d);
        public bool Send(ulong peer, byte[] data, int offset, int count, Delivery d) => _inner.Send(peer, data, offset, count, d);
        public void Close(ulong peer) => _inner.Close(peer);
        public string Describe() => $"{_inner.Describe()} +chaos({LatencyMs}ms±{JitterMs}, loss {LossRate:P0})";
        public void Dispose() => _inner.Dispose();

        public void Poll(List<Packet> received)
        {
            double now = _clock.Elapsed.TotalMilliseconds;
            _tmp.Clear();
            _inner.Poll(_tmp);
            foreach (var p in _tmp)
            {
                double due = now + LatencyMs + _rng.NextDouble() * JitterMs;
                if (p.Delivery == Delivery.Unreliable)
                {
                    if (_rng.NextDouble() < LossRate) continue;
                }
                else
                {
                    if (_lastReliableDue.TryGetValue(p.From, out var last)) due = Math.Max(due, last);
                    _lastReliableDue[p.From] = due;
                }
                _held.Add((due, _order++, p));
            }
            _held.Sort((a, b) => a.due != b.due ? a.due.CompareTo(b.due) : a.order.CompareTo(b.order));
            int n = 0;
            while (n < _held.Count && _held[n].due <= now) received.Add(_held[n++].p);
            _held.RemoveRange(0, n);
        }
    }
}
