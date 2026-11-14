using System.Collections.Generic;
using UnityEngine;

namespace IronNestCoop.Util
{
    /// <summary>
    /// Per-player rate limit (a token bucket): each player may do something <c>perSecond</c> times a second on
    /// average, with short bursts up to <c>burst</c>. Real play stays far below it; it stops a broken or hostile game
    /// from making the host do expensive work (parsing, scene searches) for every message it can send.
    /// </summary>
    public sealed class SlotLimiter
    {
        readonly float _perSecond, _burst;
        readonly Dictionary<byte, (float Tokens, float At)> _buckets = new();
        public int Refused { get; private set; }

        public SlotLimiter(float perSecond, float burst) { _perSecond = perSecond; _burst = burst; }

        public bool Allow(byte slot)
        {
            float now = Time.unscaledTime;
            (float Tokens, float At) b = _buckets.TryGetValue(slot, out var have) ? have : (_burst, now);
            float tokens = Mathf.Min(_burst, b.Tokens + (now - b.At) * _perSecond);
            if (tokens < 1f) { _buckets[slot] = (tokens, now); Refused++; return false; }
            _buckets[slot] = (tokens - 1f, now);
            return true;
        }

        public void Forget(byte slot) => _buckets.Remove(slot);
    }
}
