using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Dev;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        const int CampaignMissions = 5;        // spread over the campaign
        const float CampaignSeconds = 120f;    // each mission runs this long
        const float CampaignSample = 15f;      // the host's world is compared this often

        /// <summary>
        /// A soak test: the host plays several campaign missions in a row with every guest (one or two), lets each
        /// run for two minutes, and every 15 s each guest compares its world with the host's. It reports anything
        /// that differs in two samples in a row (not at the mission's start): scene objects and their state, unit
        /// count, area effects, mission statistics, the mission clock and the counter-battery timer.
        /// </summary>
        static IEnumerable<object> CampaignHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount >= 2, 90, "a guest joined")) yield return x;
            foreach (var x in bot.Wait(8)) yield return x;   // a second guest joins a few seconds later
            int guests = Coop.Session.PlayerCount - 1;
            bot.Note($"playing with {guests} guest(s)");
            var all = DevTools.Missions();
            var pick = new List<string>();
            for (int i = 0; i < CampaignMissions && all.Count > 0; i++) pick.Add(all[Mathf.Min(all.Count - 1, i * all.Count / CampaignMissions)].Id);
            pick = pick.Distinct().ToList();
            bot.Note("missions: " + string.Join(", ", pick));
            bot.Signal("plan", pick.Count.ToString());

            for (int m = 0; m < pick.Count; m++)
            {
                foreach (var x in StartMission(bot, pick[m])) yield return x;
                foreach (var x in bot.Until(() => WorldReady.Slots.Count() >= guests, 150, $"all guests loaded {pick[m]}")) yield return x;
                foreach (var x in bot.Wait(5)) yield return x;
                float t0 = Time.unscaledTime; int seq = 0;
                while (Time.unscaledTime - t0 < CampaignSeconds)
                {
                    bot.Signal("~fp", $"{m}#{seq++}#{WorldMeta()}\n" + Fingerprint.Encode(Fingerprint.Capture()));
                    foreach (var x in bot.Wait(CampaignSample)) yield return x;
                }
                var fm = FireMission.Instance;
                bot.Note($"{pick[m]}: host had {fm?.Entities?.Count ?? 0} units, {fm?.Effectors?.Count ?? 0} area effects, stats {StatsKey()}");
                bot.Signal("end", m.ToString());
                foreach (var slot in WorldReady.Slots.ToList())
                    foreach (var x in bot.Until(() => bot.HasSignal($"m{m}done{slot}"), 60, $"guest {slot} finished comparing {pick[m]}", soft: true)) yield return x;
                MissionManager.Instance.EndOperationAndReturnToMenu();
                foreach (var x in bot.Until(() => MissionFlow.Phase != MissionManager.GamePhase.MissionActive, 60, "back at the menu")) yield return x;
                foreach (var x in bot.Wait(5)) yield return x;
            }
            bot.Signal("done");
            foreach (var x in bot.Wait(3)) yield return x;
        }

        static string StatsKey()
        {
            var s = MissionStatsTracker.Instance?.mission;
            return s == null ? "-" : $"{s.shotsFired}/{s.hitsOnTargets}/{s.missedShots}/{s.targetsDestroyed}";
        }

        /// <summary>Units, area effects, statistics, clock and counter-battery time, as one line.</summary>
        static string WorldMeta()
        {
            var fm = FireMission.Instance;
            var cb = CounterBatteryTimer.Instance;
            return string.Join("|", fm?.Entities?.Count ?? -1, fm?.Effectors?.Count ?? -1, StatsKey(),
                MissionClock.TimeAsDouble.ToString("F2", CultureInfo.InvariantCulture),
                cb != null && cb._running ? cb._remainingSeconds.ToString("F1", CultureInfo.InvariantCulture) : "-");
        }

        static IEnumerable<object> CampaignGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("plan"), 120, "host picked the missions")) yield return x;
            int missions = int.Parse(bot.SignalValue("plan"));
            byte me = Coop.Session.LocalSlot;
            for (int m = 0; m < missions; m++)
            {
                foreach (var x in bot.Until(() => WorldReady.LocalReady && MissionFlow.Phase == MissionManager.GamePhase.MissionActive, 180, $"in mission {m + 1}")) yield return x;
                HashSet<string> atRest = null, last = new HashSet<string>(), reported = new HashSet<string>();
                var problems = new List<string>();
                string seen = null;
                while (!(bot.HasSignal("end") && bot.SignalValue("end") == m.ToString()))
                {
                    string v = bot.SignalValue("~fp");
                    if (v != null && v != seen && v.StartsWith(m + "#"))
                    {
                        seen = v;
                        int nl = v.IndexOf('\n');
                        var head = v.Substring(0, nl).Split('#');
                        var diff = Fingerprint.Diff(Fingerprint.Capture(), Fingerprint.Decode(v.Substring(nl + 1)));
                        var keys = new HashSet<string>(Fingerprint.Keys(diff));
                        if (atRest == null) { atRest = keys; bot.Note($"mission {m + 1}: {keys.Count} differences at the start (ignored)"); }
                        else
                        {
                            // only what differs in two samples in a row and wasn't different at the start
                            foreach (var d in diff)
                            {
                                string k = Fingerprint.Keys(new List<string> { d }).First();
                                if (!atRest.Contains(k) && last.Contains(k) && reported.Add(k)) problems.Add(d);
                            }
                            last = keys;
                        }
                        // the meta line: units, area effects, statistics, clock, counter-battery time
                        var host = head[2].Split('|'); var mine = WorldMeta().Split('|');
                        if (host[0] != mine[0] && reported.Add("units")) problems.Add($"units: here {mine[0]}, host {host[0]}");
                        if (host[1] != mine[1] && reported.Add("effects")) problems.Add($"area effects: here {mine[1]}, host {host[1]}");
                        if (host[2] != mine[2] && reported.Add("stats" + host[2])) problems.Add($"statistics: here {mine[2]}, host {host[2]}");
                        double hc = double.Parse(host[3], CultureInfo.InvariantCulture), mc = double.Parse(mine[3], CultureInfo.InvariantCulture);
                        if (System.Math.Abs(hc - mc) > 1.0 && reported.Add("clock")) problems.Add($"mission clock: here {mc:F2}, host {hc:F2}");
                        if (host[4] != "-" && mine[4] != "-" && System.Math.Abs(double.Parse(host[4], CultureInfo.InvariantCulture) - double.Parse(mine[4], CultureInfo.InvariantCulture)) > 4 && reported.Add("cb"))
                            problems.Add($"counter-battery: here {mine[4]}, host {host[4]}");
                        if ((host[4] == "-") != (mine[4] == "-") && reported.Add("cbrun")) problems.Add($"counter-battery running: here {mine[4]}, host {host[4]}");
                    }
                    yield return null;
                }
                foreach (var p in problems) bot.Note($"mission {m + 1} differs: {p}");
                bot.Check(problems.Count == 0, $"mission {m + 1}: the guest's world stayed like the host's ({problems.Count} lasting differences)");
                bot.Signal($"m{m}done{me}");
                foreach (var x in bot.Until(() => MissionFlow.Phase != MissionManager.GamePhase.MissionActive, 60, "back at the menu")) yield return x;
            }
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host finished")) yield return x;
        }
    }
}
