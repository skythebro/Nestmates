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
        // ------------------------------------------------------------------ the bot's hand: the game's own input path

        /// <summary>Hover the way the cursor manager does: it tells every button which interactable is under the cursor.</summary>
        internal static void RealHover(LookAtTarget b, bool on) => b.HandleHoverChangedFromManager(on ? b.interactable : null);

        /// <summary>Click the way the cursor manager does (press on the button's interactable, release on it).</summary>
        internal static IEnumerable<object> RealPress(Bot bot, LookAtTarget b)
        {
            var it = b.interactable;
            if (it == null) { b.OnClickDown(); foreach (var x in bot.Wait(0.15)) yield return x; b.OnClickUp(); yield break; }
            RealHover(b, true);
            b.HandleClickDownFromManager(it);
            foreach (var x in bot.Wait(0.15)) yield return x;
            b.HandleClickUpFromManager(it);
            RealHover(b, false);
        }

        // buttons a sweep must not press: they leave the mission or the game, or change saved progress
        static readonly string[] SweepUnsafeCalls = { "Quit", "EndOperation", "ReturnTo", "LoadScene", "Restart", "ForceUnlock", "MarkAll", "Exit", "MainMenu", "OpenURL", "Abort", "Surrender", "Retire" };
        static readonly string[] SweepUnsafePaths = { "ProgressDebugButtons", "Campaign 1", "Main Menu", "Quit", "Exit" };

        static bool SweepSafe(LookAtTarget b, string path)
        {
            if (SweepUnsafePaths.Any(path.Contains)) return false;
            foreach (var l in Wiring.ListenersOf(b)) if (SweepUnsafeCalls.Any(l.Contains)) return false;
            return true;
        }

        static List<(string Path, LookAtTarget B)> SweepButtons()
        {
            var list = new List<(string, LookAtTarget)>();
            var seen = new HashSet<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), false))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b == null || !b.gameObject.activeInHierarchy) continue;
                string path = Util.ScenePath.Of(b.transform);
                if (path == null || !seen.Add(path) || Fingerprint.Skipped(path) || Controls.Instance.IsPersonal(b) || !SweepSafe(b, path)) continue;
                if (LaunchArgs.Filter != null && !path.Contains(LaunchArgs.Filter)) continue;
                list.Add((path, b));
            }
            return list.OrderBy(x => x.Item1).ToList();
        }

        /// <summary>
        /// Every button, pressed through the game's own input path, first by the guest and then by the host; after
        /// each press both machines must show the same thing (Fingerprint). The report lists, per button, what the
        /// other player does not see. Writes sweep.txt next to the result file.
        /// </summary>
        static IEnumerable<object> SweepHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Computer != null, 120, "guest mission loaded")) yield return x;
            bot.Signal("ready");
            string lastWant = null, lastPress = null;
            while (!bot.HasSignal("sweepdone"))
            {
                string want = bot.SignalValue("want");
                if (want != null && want != lastWant)
                {
                    lastWant = want;
                    bot.Signal("~fp", want + "\n" + Fingerprint.Encode(Fingerprint.Capture()));
                }
                string press = bot.SignalValue("hostpress");
                if (press != null && press != lastPress)
                {
                    lastPress = press;
                    string path = press.Substring(press.IndexOf('|') + 1);
                    var b = Util.ScenePath.Find(path)?.GetComponent<LookAtTarget>();
                    if (b != null) foreach (var x in RealPress(bot, b)) yield return x;
                    bot.Signal("hostpressed", press);
                }
                yield return null;
            }
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> SweepGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Computer != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(4)) yield return x;

            int n = 0;
            List<string> diff = null;
            // one comparison with the host's current view; result in `diff`
            IEnumerable<object> Compare()
            {
                string tag = (++n).ToString();
                bot.Signal("want", tag);
                foreach (var x in bot.Until(() => (bot.SignalValue("~fp") ?? "").StartsWith(tag + "\n"), 10, "host view " + tag)) yield return x;
                string s = bot.SignalValue("~fp");
                diff = Fingerprint.Diff(Fingerprint.Capture(), Fingerprint.Decode(s.Substring(s.IndexOf('\n') + 1)));
            }
            var ignore = new HashSet<string>();      // differs at rest
            var before = new HashSet<string>();      // differed just before this press (an earlier press's fault)
            IEnumerable<object> Converge(double seconds)
            {
                double end = Time.realtimeSinceStartup + seconds;
                while (true)
                {
                    foreach (var x in Compare()) yield return x;
                    diff = diff.Where(d => { var k = Fingerprint.Keys(new List<string> { d }).First(); return !ignore.Contains(k) && !before.Contains(k); }).ToList();
                    if (diff.Count == 0 || Time.realtimeSinceStartup > end) yield break;
                    foreach (var x in bot.Wait(0.4)) yield return x;
                }
            }

            // at rest: what already differs before anyone touches anything
            foreach (var x in Converge(8)) yield return x;
            var report = new List<string> { $"AT REST: {diff.Count} differences" };
            report.AddRange(diff.Select(d => "  " + d));
            bot.Check(diff.Count == 0, $"both players see the same turret at rest ({diff.Count} differences: {string.Join(" | ", diff.Take(3))})");
            foreach (var k in Fingerprint.Keys(diff)) ignore.Add(k);

            var buttons = SweepButtons();
            bot.Note($"sweeping {buttons.Count} buttons");
            int guestBad = 0, hostBad = 0, i = 0;
            foreach (var (path, b) in buttons)
            {
                i++;
                if (b == null || !b.gameObject.activeInHierarchy) { report.Add($"SKIP {path} (gone)"); continue; }
                // guest presses (what already differs now is not this press's doing)
                before.Clear();
                foreach (var x in Converge(1)) yield return x;
                foreach (var k in Fingerprint.Keys(diff)) before.Add(k);
                foreach (var x in RealPress(bot, b)) yield return x;
                foreach (var x in Converge(4)) yield return x;
                if (diff.Count > 0) { guestBad++; report.Add($"GUEST PRESS {path}: host and guest differ"); report.AddRange(diff.Select(d => "  " + d)); }
                else report.Add($"GUEST PRESS {path}: same");
                // host presses the same button
                before.Clear();
                foreach (var x in Converge(1)) yield return x;
                foreach (var k in Fingerprint.Keys(diff)) before.Add(k);
                string req = i + "|" + path;
                bot.Signal("hostpress", req);
                foreach (var x in bot.Until(() => bot.SignalValue("hostpressed") == req, 15, null, soft: true)) yield return x;
                foreach (var x in Converge(4)) yield return x;
                if (diff.Count > 0) { hostBad++; report.Add($"HOST PRESS {path}: host and guest differ"); report.AddRange(diff.Select(d => "  " + d)); }
                else report.Add($"HOST PRESS {path}: same");
            }
            if (LaunchArgs.ResultPath != null)
                System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(LaunchArgs.ResultPath), "sweep.txt"), report);
            bot.Note($"sweep: {buttons.Count} buttons, {guestBad} guest presses and {hostBad} host presses left the two players seeing different things");
            bot.Check(guestBad == 0, $"every guest press shows the same for both ({guestBad} differ, see sweep.txt)");
            bot.Check(hostBad == 0, $"every host press shows the same for both ({hostBad} differ, see sweep.txt)");
            bot.Signal("sweepdone");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
