using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace IronNestCoop.Core
{
    /// <summary>
    /// Where the mod spends frame time. Every module tick, the network message handling and the window are timed;
    /// anything over <see cref="SlowMs"/> in one frame is logged (at most once per 10 s per name, with the worst
    /// value seen), so a hitch in play shows up in the log by name.
    /// </summary>
    public static class Perf
    {
        public const double SlowMs = 4.0;
        static readonly Stopwatch Clock = Stopwatch.StartNew();
        static readonly Dictionary<string, (double Worst, int Count, float NextLog)> Slow = new();

        public static long Start() => Clock.ElapsedTicks;

        static int _heavyFrame = -1;

        /// <summary>
        /// Searching the whole scene (FindObjectsOfType) costs a few ms on a slower PC. Each module used to search on
        /// its own clock, so several searches sometimes landed in one frame and caused regular hitches. Now a module
        /// asks first, and if another module already searched this frame it waits for the next one.
        /// </summary>
        public static bool HeavyFrame()
        {
            int f = UnityEngine.Time.frameCount;
            if (f == _heavyFrame) return false;
            _heavyFrame = f;
            return true;
        }

        public static void Stop(string name, long started)
        {
            double ms = (Clock.ElapsedTicks - started) * 1000.0 / Stopwatch.Frequency;
            if (ms < SlowMs) return;
            Slow.TryGetValue(name, out var s);
            s.Worst = System.Math.Max(s.Worst, ms); s.Count++;
            if (Time.unscaledTime >= s.NextLog)
            {
                CoopLog.Warn($"[perf] {name} took {ms:F1} ms (worst {s.Worst:F1} ms, {s.Count} slow frames since last report)");
                s = (0, 0, Time.unscaledTime + 10f);
            }
            Slow[name] = s;
        }
    }
}
