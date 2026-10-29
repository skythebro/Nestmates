using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Dev;
using IronNestCoop.Sync;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>What the dev shortcuts change, as one comparable line (same on both machines when synced).</summary>
        static string DevState()
        {
            var valves = HighPressureSystemManager.GetAllRegisteredValves();
            string v = valves == null ? "-" : string.Join(",", Enumerable.Range(0, valves.Count).Select(i => valves[i] == null ? "x" : valves[i].GetDamage01() > 0.01f ? "broken" : "ok").OrderBy(x => x));
            var eng = Object.FindObjectOfType<DieselEngineController>();
            var t = CounterBatteryTimer.Instance;
            return $"points {Points} | deck {Punchcards().Count} | guns {Gunnery.CurrentDigest} | charges {PowderChargeInventory.Instance?.CurrentCharges} | valves {v} | engine {eng?.EnginesRunning} | timer {(t == null ? "-" : (Mathf.Round(t.TimeRemaining / 5f) * 5).ToString("F0"))}";
        }

        /// <summary>
        /// Dev shortcuts on the host: jump to a mission from the Dev tab, then every shortcut; the guest must end up
        /// with the same state after each one (they work the game's own way, so the normal sync carries them).
        /// </summary>
        static IEnumerable<object> DevToolsHost(Bot bot)
        {
            bool was = Settings.DevTools;
            Settings.DevTools = true;
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            bot.Check(DevTools.Enabled, "dev shortcuts are on for the host");
            bot.Check(DevTools.Missions().Count > 5, $"the Dev tab lists the campaign's missions ({DevTools.Missions().Count})");
            bot.Note("dev: " + DevTools.StartMission(LaunchArgs.Mission ?? "FireOnCall"));
            foreach (var x in bot.Until(() => FireMission.Instance != null && MissionFlow.Phase == MissionManager.GamePhase.MissionActive, 120, "mission started from the Dev tab")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "the guest followed")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Check(DevTools.Used, "the first use switched achievements/leaderboards off for this run");
            int step = 0;
            foreach (var key in new[] { Key.P, Key.C, Key.S, Key.G, Key.V, Key.E, Key.T })
            {
                var a = DevTools.Actions.First(z => z.Key == key);
                DevTools.Run(a);
                bot.Note($"dev {key}: {DevTools.LastMessage}");
                foreach (var x in bot.Wait(3)) yield return x;   // the sync carries it
                bot.Signal("devstate", (++step) + "#" + key + "#" + DevState());
                foreach (var x in bot.Until(() => bot.SignalValue("devchecked") == step.ToString(), 30, null)) yield return x;
            }
            var win = DevTools.Actions.First(z => z.Key == Key.W);
            DevTools.Run(win);
            bot.Check(MissionManager.Instance._CurrentPhase_k__BackingField == MissionManager.GamePhase.MissionActive, "win needs a second press");
            DevTools.Run(win);
            bot.Note("dev W: " + DevTools.LastMessage);
            bot.Signal("won");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            Settings.DevTools = was;
            if (MissionManager.Instance._CurrentPhase_k__BackingField == MissionManager.GamePhase.MissionActive) MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> DevToolsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            bot.Check(!DevTools.Enabled, "dev shortcuts are host-only (off on a guest)");
            int step = 0;
            while (!bot.HasSignal("won"))
            {
                var s = bot.SignalValue("devstate");
                if (s != null && s.StartsWith((step + 1) + "#"))
                {
                    step++;
                    var parts = s.Split('#');
                    string want = parts[2];
                    foreach (var x in bot.Until(() => DevState() == want, 8, null, soft: true)) yield return x;
                    bot.Check(DevState() == want, $"dev {parts[1]} shows the same here (here {DevState()} / host {want})");
                    bot.Signal("devchecked", step.ToString());
                }
                yield return null;
            }
            foreach (var x in bot.Until(() => MissionManager.Instance._CurrentPhase_k__BackingField != MissionManager.GamePhase.MissionActive, 20, null, soft: true)) yield return x;
            bot.Note($"guest after the host's win: phase {MissionManager.Instance._CurrentPhase_k__BackingField}");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
