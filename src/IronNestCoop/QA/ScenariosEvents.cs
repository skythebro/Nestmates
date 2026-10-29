using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>Which teleprinter alert lights are on (not caught by the fingerprint: they glow by curve).</summary>
        static string AlertsLine() => string.Join(" | ", Object.FindObjectsOfType(Il2CppType.Of<PrinterAlertSystem>())
            .Select(o => o.TryCast<PrinterAlertSystem>()).Where(a => a != null)
            .Select(a => Util.ScenePath.Of(a.transform) + " " + PrinterAlerts.KeyOf(a)).OrderBy(x => x));

        /// <summary>The mission graph's named timers (what the wrist watch shows).</summary>
        static string GenericTimersLine()
        {
            var timers = FireMission.Instance?.RunningTimers;
            if (timers == null) return "none";
            var parts = new List<string>();
            foreach (var kv in timers) if (kv.Value != null) parts.Add($"{kv.Key} {kv.Value.InitialSeconds:F0}/{kv.Value.CurrentSeconds:F1}");
            parts.Sort();
            return parts.Count == 0 ? "none" : string.Join(" | ", parts);
        }

        /// <summary>The same timers, each reading within <paramref name="slack"/> seconds.</summary>
        static bool SameTimers(string a, string b, float slack)
        {
            if (a == null || b == null) return false;
            var pa = a.Split(" | "); var pb = b.Split(" | ");
            if (pa.Length != pb.Length) return false;
            for (int i = 0; i < pa.Length; i++)
            {
                var x = pa[i].Split(' ', '/'); var y = pb[i].Split(' ', '/');
                if (x.Length != 3 || y.Length != 3) { if (pa[i] != pb[i]) return false; continue; }
                if (x[0] != y[0] || x[1] != y[1]) return false;
                if (!float.TryParse(x[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var cx)
                    || !float.TryParse(y[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var cy)
                    || Mathf.Abs(cx - cy) > slack) return false;
            }
            return true;
        }

        static EscapeMenuToggleUnityEvent EscMenu() => Object.FindObjectsOfType(Il2CppType.Of<EscapeMenuToggleUnityEvent>())
            .Select(o => o.TryCast<EscapeMenuToggleUnityEvent>()).FirstOrDefault(m => m != null && m.isActiveAndEnabled);

        /// <summary>
        /// Things the mission graph does on the host that guests must see: the teleprinter alarm lamps and the
        /// counter-battery timer starting (its display). After each, nothing may look different on the guest that
        /// did not already differ at rest. Then the shared pause: both menus open stops time for both, one closing
        /// starts it again.
        /// </summary>
        static IEnumerable<object> EventsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(4)) yield return x;

            var steps = new (string Name, System.Action Do)[]
            {
                ("rest", () => { }),
                ("field reports alarm", () => Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Primary)?.SignalAlarm(Teleprinter.TeleprinterAlarmState.High)),
                ("high command alarm", () => Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Secondary)?.SignalAlarm(Teleprinter.TeleprinterAlarmState.Low)),
                ("alarms cleared", () => { Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Primary)?.ClearAlarm(); Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Secondary)?.ClearAlarm(); }),
                // as the mission graph's State_StartTimer does: instantiate its prefab, Init, start
                ("timer started", () =>
                {
                    var t = CounterBatteryTimer.Instance;
                    if (t == null) { var prefab = WorldEvents.TimerPrefab(); if (prefab != null) t = Object.Instantiate(prefab); }
                    if (t != null) { t.Init(300); t.StartTimer(); }
                }),
            };
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i].Do();
                bot.Note("host: " + steps[i].Name);
                foreach (var x in bot.Wait(3)) yield return x;
                bot.Signal("alerts", i + "#" + AlertsLine());
                bot.Signal("~fp", i + "#" + steps[i].Name + "\n" + Fingerprint.Encode(Fingerprint.Capture()));
                int step = i;
                foreach (var x in bot.Until(() => bot.SignalValue("fpchecked") == step.ToString(), 40, "guest compared " + steps[i].Name)) yield return x;
            }
            var t2 = CounterBatteryTimer.Instance;
            bot.Check(t2 != null, "the host has a counter-battery timer to compare");
            bot.Signal("timer", t2 == null ? "none" : $"{t2._running} {Mathf.Round(t2._remainingSeconds / 10f) * 10}");

            // the wrist watch: a named mission timer (State_GenericTimer's, host only) must show on the guest too
            var watch = FireMission.Instance?.RunningTimers;
            bot.Check(watch != null, "the fire mission has its named timers");
            if (watch != null)
            {
                var tv = new FireMission.TimerValue(); tv.InitialSeconds = 780; tv.CurrentSeconds = 780; tv.StartedAt = MissionClock.TimeAsDouble;   // as State_GenericTimer starts one
                watch["coop-qa-watch"] = tv;
            }
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("gtimers", GenericTimersLine());
            foreach (var x in bot.Until(() => bot.HasSignal("gtimerschecked"), 30, "guest compared the watch timers")) yield return x;

            // the timer rule while it runs: 2x makes what is left twice as long, off pauses it, 1x runs on from there
            if (t2 != null && t2._running)
            {
                int keep = Settings.CounterBatteryTimerPercent;
                float before = t2.TimeRemaining;
                Settings.CounterBatteryTimerPercent = 200;
                foreach (var x in bot.Wait(1.5)) yield return x;
                bot.Check(Mathf.Abs(t2.TimeRemaining - 2 * before) < 6, $"2x: what was left doubled ({before:F0} s -> {t2.TimeRemaining:F0} s)");
                Settings.CounterBatteryTimerPercent = 0;
                foreach (var x in bot.Wait(1.5)) yield return x;
                float paused = t2.TimeRemaining;
                foreach (var x in bot.Wait(1.5)) yield return x;
                bot.Check(!t2._running && Mathf.Abs(t2.TimeRemaining - paused) < 0.3f, $"off: the timer stands still ({paused:F1} s -> {t2.TimeRemaining:F1} s, running {t2._running})");
                bot.Signal("timeroff", $"{t2._running} {Mathf.Round(t2.TimeRemaining / 10f) * 10}");
                foreach (var x in bot.Until(() => bot.HasSignal("timeroffchecked"), 30, "guest checked the paused timer")) yield return x;
                Settings.CounterBatteryTimerPercent = 100;
                foreach (var x in bot.Wait(1.5)) yield return x;
                bot.Check(t2._running && Mathf.Abs(t2.TimeRemaining - paused / 2) < 6, $"1x again: it runs on, at the game's own length ({paused:F0} s at 2x -> {t2.TimeRemaining:F0} s)");
                Settings.CounterBatteryTimerPercent = keep;
            }
            else { bot.Note("no running timer for the rule checks"); bot.Signal("timeroff", "skip"); }
            watch?.Remove("coop-qa-watch");

            // shared pause
            var menu = EscMenu();
            bot.Check(menu != null, "the pause menu exists here");
            menu?.ForceOpen(true);
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(Time.timeScale > 0f, $"only my menu is open: time still runs (time scale {Time.timeScale})");
            bot.Signal("hostmenu");
            foreach (var x in bot.Until(() => bot.HasSignal("guestmenu"), 30, "guest opened the menu")) yield return x;
            foreach (var x in bot.Until(() => Time.timeScale == 0f, 5, null, soft: true)) yield return x;
            bot.Check(Time.timeScale == 0f && NoPause.AllPaused, $"everyone's menu is open: paused (time scale {Time.timeScale})");
            bot.Signal("pausedhere");
            // Escape pressed quickly (players did this): the world must not stop and start with every press (that
            // froze the menu's animations halfway), only once the menu stays open
            int flips = NoPause.PauseFlips;
            for (int i = 0; i < 6; i++)
            {
                menu?.ForceClose(true);
                foreach (var x in bot.Wait(0.2)) yield return x;
                menu?.ForceOpen(true);
                foreach (var x in bot.Wait(0.2)) yield return x;
            }
            int during = NoPause.PauseFlips - flips;
            bot.Note($"shared pause turned on/off {during} times during 12 quick Escape presses");
            bot.Check(during <= 1, $"quick Escape presses: the world ran on and was not paused again with each press ({during} changes, 1 expected)");
            // and the menu itself must still be there: a close timer from the spam hid the open menu's clipboard
            var board = menu == null ? null : menu.transform.Find("Clipboard Main Menu");
            bool hiddenWhileOpen = false;
            foreach (var x in bot.Until(() => { if (board != null && menu.isOpen && !board.gameObject.activeInHierarchy) hiddenWhileOpen = true; return Time.timeScale == 0f; }, 5, null, soft: true)) yield return x;
            bot.Check(Time.timeScale == 0f && NoPause.AllPaused, $"the menu stayed open: paused again (time scale {Time.timeScale})");
            foreach (var x in bot.Wait(0.5)) yield return x;
            if (board != null && menu.isOpen && !board.gameObject.activeInHierarchy) hiddenWhileOpen = true;
            bot.Note($"menu clipboard {(board == null ? "not found" : board.gameObject.activeInHierarchy ? "shown" : "hidden")}; close timers stopped {NoPause.CloseTimersStopped}");
            bot.Check(board != null && !hiddenWhileOpen, $"after the quick presses the open menu is still shown (clipboard {(board == null ? "not found" : hiddenWhileOpen ? "was hidden while open" : "shown")})");
            bot.Signal("spamdone");
            foreach (var x in bot.Until(() => bot.HasSignal("guestclosed"), 30, "guest closed the menu")) yield return x;
            foreach (var x in bot.Until(() => Time.timeScale > 0f, 5, null, soft: true)) yield return x;
            bot.Check(Time.timeScale > 0f && !NoPause.AllPaused, $"one menu closed: time runs again (time scale {Time.timeScale})");
            menu?.ForceClose(true);

            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> EventsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            var atRest = new HashSet<string>();
            int expect = 0;
            while (!bot.HasSignal("timer"))
            {
                var s = bot.SignalValue("~fp");
                if (s != null && s.StartsWith(expect + "#"))
                {
                    string name = s.Substring(s.IndexOf('#') + 1, s.IndexOf('\n') - s.IndexOf('#') - 1);
                    foreach (var x in bot.Wait(1)) yield return x;
                    var host = Fingerprint.Decode(s.Substring(s.IndexOf('\n') + 1));
                    var diff = Fingerprint.Diff(Fingerprint.Capture(), host);
                    var keys = Fingerprint.Keys(diff).ToList();
                    if (expect == 0) { foreach (var k in keys) atRest.Add(k); bot.Note($"at rest {keys.Count} things differ already (ignored after)"); }
                    else
                    {
                        var fresh = diff.Where(d => !atRest.Contains(Fingerprint.Keys(new List<string> { d }).First())).ToList();
                        bot.Check(fresh.Count == 0, $"after '{name}' the guest sees the same ({fresh.Count} new differences: {string.Join(" || ", fresh.Take(6))})");
                    }
                    string hostAlerts = bot.SignalValue("alerts") ?? "";
                    if (hostAlerts.StartsWith(expect + "#"))
                    {
                        string want = hostAlerts.Substring(hostAlerts.IndexOf('#') + 1);
                        foreach (var x in bot.Until(() => AlertsLine() == want, 3, null, soft: true)) yield return x;
                        bot.Check(AlertsLine() == want, $"after '{name}' the teleprinter alert lights match (here {AlertsLine()} / host {want})");
                    }
                    bot.Signal("fpchecked", expect.ToString());
                    expect++;
                }
                yield return null;
            }
            var t = CounterBatteryTimer.Instance;
            string mine = t == null ? "none" : $"{t._running} {Mathf.Round(t._remainingSeconds / 10f) * 10}";
            bot.Check(mine == bot.SignalValue("timer"), $"the counter-battery timer runs here too (here {mine} / host {bot.SignalValue("timer")})");
            bot.Check(t != null && t.gameObject.activeInHierarchy, "the counter-battery display exists here (its flap opens on the timer's events)");

            foreach (var x in bot.Until(() => bot.HasSignal("gtimers"), 60, "host's watch timers")) yield return x;
            // the host's line is ~1 s old by the time it is compared: the readings may differ by that much
            foreach (var x in bot.Until(() => SameTimers(GenericTimersLine(), bot.SignalValue("gtimers"), 2.5f), 4, null, soft: true)) yield return x;
            bot.Check(SameTimers(GenericTimersLine(), bot.SignalValue("gtimers"), 2.5f), $"the wrist watch's timers match (here {GenericTimersLine()} / host {bot.SignalValue("gtimers")})");
            bot.Signal("gtimerschecked");
            foreach (var x in bot.Until(() => bot.HasSignal("timeroff"), 60, "host turned the timer rule off")) yield return x;
            if (bot.SignalValue("timeroff") != "skip")
            {
                string Off() { var tt = CounterBatteryTimer.Instance; return tt == null ? "none" : $"{tt._running} {Mathf.Round(tt.TimeRemaining / 10f) * 10}"; }
                foreach (var x in bot.Until(() => Off() == bot.SignalValue("timeroff"), 5, null, soft: true)) yield return x;
                bot.Check(Off() == bot.SignalValue("timeroff"), $"the timer stands still here too (here {Off()} / host {bot.SignalValue("timeroff")})");
            }
            bot.Signal("timeroffchecked");

            foreach (var x in bot.Until(() => bot.HasSignal("hostmenu"), 30, "host opened the menu")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(Time.timeScale > 0f, "only the host's menu is open: time still runs here");
            var menu = EscMenu();
            bot.Check(menu != null, "the pause menu exists here");
            menu?.ForceOpen(true);
            bot.Signal("guestmenu");
            foreach (var x in bot.Until(() => bot.HasSignal("pausedhere"), 30, "host paused")) yield return x;
            foreach (var x in bot.Until(() => Time.timeScale == 0f, 5, null, soft: true)) yield return x;
            bot.Check(Time.timeScale == 0f && NoPause.AllPaused, $"everyone's menu is open: paused here too (time scale {Time.timeScale})");
            foreach (var x in bot.Until(() => bot.HasSignal("spamdone"), 30, "host pressed Escape quickly")) yield return x;
            menu?.ForceClose(true);
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Signal("guestclosed");
            foreach (var x in bot.Until(() => Time.timeScale > 0f, 5, null, soft: true)) yield return x;
            bot.Check(Time.timeScale > 0f, $"I closed my menu: time runs (time scale {Time.timeScale})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
