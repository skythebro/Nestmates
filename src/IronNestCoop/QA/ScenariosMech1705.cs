using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>The counter-battery timer, even while its object is switched off.</summary>
        static CounterBatteryTimer CbTimer() => CounterBatteryTimer.Instance;

        static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

        /// <summary>
        /// Checks for the mechanics the game's 2026-10-14 update added, and the things another co-op mod's changelog
        /// listed as drifting between players. The guest's mission clock matches the host's. The counter-battery timer
        /// shows the host's time after time is added (and never counts the added time twice). The mission statistics
        /// behind the end-of-mission screen match. An area effect the host creates on the map exists on the guest too.
        /// </summary>
        static IEnumerable<object> Mech1705Host(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "IronRoad")) yield return x;   // a campaign mission with a counter-battery timer
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(5)) yield return x;

            // 1. the mission clock
            bot.Signal("clock", F(MissionClock.TimeAsDouble));

            // 2. the counter-battery timer: start it, then add time
            // the mission graph makes the timer from a prefab when it starts it; do the same if it isn't running yet
            if (CbTimer() == null)
            {
                var prefab = WorldEvents.TimerPrefab();
                if (prefab != null) Object.Instantiate(prefab.gameObject);
                foreach (var x in bot.Wait(0.5)) yield return x;
            }
            var cb = CbTimer();
            bot.Check(cb != null, "the counter-battery timer exists on the host");
            if (cb == null) bot.Signal("cb", "-1");
            else
            {
                cb.Init(300f); cb.StartTimer();
                foreach (var x in bot.Wait(2)) yield return x;
                cb.AddTime(200f);
                foreach (var x in bot.Wait(0.2)) yield return x;
                bot.Signal("cb", F(cb._remainingSeconds) + "^" + F(Time.unscaledTime));
            }

            // 3. mission statistics: the host counts a shot, a hit and a destroyed target
            var st = MissionStatsTracker.Instance;
            bot.Check(st != null && st.mission != null, "the mission statistics tracker is running");
            if (st != null && st.mission != null)
            {
                st.mission.shotsFired += 3; st.mission.hitsOnTargets += 2; st.mission.targetsDestroyed += 1;
                bot.Signal("stats", $"{st.mission.shotsFired}^{st.mission.hitsOnTargets}^{st.mission.targetsDestroyed}");
            }

            // 4. area effects the mission itself makes (they can't be made by hand): wait a while for one
            var fm = FireMission.Instance;
            foreach (var x in bot.Until(() => fm != null && fm.Effectors != null && fm.Effectors.Count > 0, 45, null, soft: true)) yield return x;
            if (fm != null && fm.Effectors != null && fm.Effectors.Count > 0)
            {
                var ids = new List<string>();
                foreach (var kv in fm.Effectors) ids.Add(kv.Key);
                bot.Note($"the mission made {ids.Count} area effect(s)");
                bot.Signal("effect", string.Join("^", ids));
            }
            else { bot.Note("this mission made no area effect within 45 s: area effects not checked"); bot.Signal("effect", ""); }

            foreach (var x in bot.Until(() => bot.HasSignal("guestdone"), 90, "guest checked")) yield return x;
            bot.Signal("done");
        }

        static IEnumerable<object> Mech1705Guest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "in mission")) yield return x;

            foreach (var x in bot.Until(() => bot.HasSignal("clock"), 120, "host sent its mission clock")) yield return x;
            double hostClock = D(bot.SignalValue("clock")), mine = MissionClock.TimeAsDouble;
            bot.Check(System.Math.Abs(mine - hostClock) < 0.5, $"the guest's mission clock matches the host's (host {hostClock:F2}, guest {mine:F2})");

            var cb = CbTimer();
            foreach (var x in bot.Until(() => bot.HasSignal("cb"), 30, "host added counter-battery time")) yield return x;
            var cbv = bot.SignalValue("cb").Split('^');
            double hostCb = D(cbv[0]);
            foreach (var x in bot.Until(() => CbTimer() != null, 5, null, soft: true)) yield return x;
            cb = CbTimer();
            bot.Check(hostCb < 0 || cb != null, "the guest has the counter-battery timer too");
            if (cb != null && hostCb >= 0)
            {
                // watch it for a second and a half: it must never show the added time twice
                float worst = 0f, t0 = Time.unscaledTime;
                while (Time.unscaledTime - t0 < 1.5f) { worst = Mathf.Max(worst, cb._remainingSeconds); yield return null; }
                bot.Check(worst < hostCb + 5, $"the guest's counter-battery timer never counts added time twice (worst {worst:F1}, host {hostCb:F1})");
                // the host sends the timer every 2 s; by now about 1.5 s have passed since the host's reading
                foreach (var x in bot.Wait(2)) yield return x;
                double expect = hostCb - 3.5;
                bot.Check(System.Math.Abs(cb._remainingSeconds - expect) < 2.5, $"the guest's counter-battery timer shows the host's time (guest {cb._remainingSeconds:F1}, host about {expect:F1})");
            }

            foreach (var x in bot.Until(() => bot.HasSignal("stats"), 30, "host counted shots")) yield return x;
            var want = bot.SignalValue("stats").Split('^').Select(int.Parse).ToArray();
            var st = MissionStatsTracker.Instance;
            foreach (var x in bot.Until(() => st != null && st.mission != null && st.mission.shotsFired == want[0] && st.mission.hitsOnTargets == want[1] && st.mission.targetsDestroyed == want[2], 5,
                $"the guest's mission statistics follow the host's (shots/hits/destroyed {want[0]}/{want[1]}/{want[2]})", soft: true)) yield return x;
            bot.Check(st != null && st.mission != null && st.mission.shotsFired == want[0] && st.mission.hitsOnTargets == want[1] && st.mission.targetsDestroyed == want[2],
                $"the end-of-mission statistics match the host's (guest {st?.mission?.shotsFired}/{st?.mission?.hitsOnTargets}/{st?.mission?.targetsDestroyed}, host {want[0]}/{want[1]}/{want[2]})");

            foreach (var x in bot.Wait(1)) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("effect"), 90, "host looked for area effects")) yield return x;
            string effs = bot.SignalValue("effect") ?? "";
            if (effs.Length > 0)
            {
                var fm = FireMission.Instance;
                string first = effs.Split('^')[0];
                foreach (var x in bot.Until(() => fm != null && fm.Effectors != null && fm.Effectors.ContainsKey(first), 5, null, soft: true)) yield return x;
                bot.Check(fm != null && fm.Effectors != null && fm.Effectors.ContainsKey(first), $"the host's area effect {first} exists on the guest too (guest has {fm?.Effectors?.Count ?? 0})");
            }
            else bot.Note("no area effects to compare");
            bot.Signal("guestdone");
            foreach (var x in bot.Wait(2)) yield return x;
        }
    }
}
