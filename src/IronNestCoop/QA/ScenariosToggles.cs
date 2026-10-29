using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>"count~on-id~on-id..." (bot signals are size-limited; only the toggles that are on).</summary>
        static string ToggleStates()
        {
            var ids = Toggles.Instance.Ids.ToList();
            return ids.Count + "~" + string.Join("~", ids.Where(i => Toggles.Instance.Value(i) == true).OrderBy(i => i));
        }

        /// <summary>Empty when both sides have the same toggles on; otherwise what differs.</summary>
        static string ToggleDiff(string a, string b)
        {
            var x = a.Split('~'); var y = b.Split('~');
            var on1 = new HashSet<string>(x.Skip(1).Where(v => v.Length > 0)); var on2 = new HashSet<string>(y.Skip(1).Where(v => v.Length > 0));
            var only1 = on1.Except(on2).ToList(); var only2 = on2.Except(on1).ToList();
            if (only1.Count == 0 && only2.Count == 0) return "";
            return $"(toggles {x[0]} vs {y[0]}; on only here: {string.Join(" | ", only1.Take(4))}; on only there: {string.Join(" | ", only2.Take(4))})";
        }

        /// <summary>A button wired (in the scene) to one of the toggle methods.</summary>
        static (LookAtTarget btn, string what) ToggleButton()
        {
            foreach (var m in new[] { "TogglePowerAll", "ToggleState", "ToggleBool", "PowerOffAll", "PowerOnAll" })
                foreach (var h in Wiring.Callers(m))
                {
                    var b = h.Owner.TryCast<LookAtTarget>() ?? h.Owner.GetComponent<LookAtTarget>();
                    if (b != null && b.gameObject.activeInHierarchy) return (b, $"{b.name} -> {m} on {h.Target}");
                }
            return (null, null);
        }

        static IEnumerable<object> TogglesHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            var ids = Toggles.Instance.Ids.ToList();
            bot.Note($"{ids.Count} toggles: {string.Join(" | ", ids.Take(12))}");
            bot.Signal("ready", ToggleStates());

            foreach (var x in bot.Until(() => bot.HasSignal("pressed"), 60, "guest pressed a switch")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            bot.Signal("after1", ToggleStates());

            // the host flips a few toggles itself (as its own presses or game events would)
            int flipped = 0;
            foreach (var o in Object.FindObjectsOfType<AnimatorOnOffStateChanger>()) { if (flipped++ >= 2) break; o.ToggleState(); }
            foreach (var o in Object.FindObjectsOfType<AnimatedObjectToggle>()) { if (flipped++ >= 4) break; o.ToggleOn = !o.ToggleOn; }
            bot.Note($"host flipped {flipped} toggles");
            foreach (var x in bot.Wait(1.5)) yield return x;
            var alerts = Object.FindObjectOfType<PrinterAlertSystem>();
            alerts?.TriggerLowPriorityAlert();   // a lit alert lamp for the guest to dismiss
            bot.Signal("after2", ToggleStates());

            // the guest switches a lamp off by hand and walks up to a printer: both must show here too
            foreach (var x in bot.Until(() => bot.HasSignal("guestlamp"), 60, "guest switched a lamp")) yield return x;
            string want = bot.SignalValue("guestlamp");
            foreach (var x in bot.Until(() => ToggleDiff(ToggleStates(), want) == "", 6, null, soft: true)) yield return x;
            bot.Check(ToggleDiff(ToggleStates(), want) == "", $"the guest's lamp switch shows on the host {ToggleDiff(ToggleStates(), want)}");
            if (alerts != null)
            {
                foreach (var x in bot.Until(() => alerts.ResidentTier == PrinterAlertSystem.AlertTier.None, 6, null, soft: true)) yield return x;
                bot.Check(alerts.ResidentTier == PrinterAlertSystem.AlertTier.None, $"the guest's alert dismissal reached the host (tier {alerts.ResidentTier}, dismissals {Toggles.Instance.Dismissed})");
            }
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> TogglesGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Until(() => ToggleDiff(ToggleStates(), bot.SignalValue("ready")) == "", 5, null, soft: true)) yield return x;
            bot.Check(ToggleDiff(ToggleStates(), bot.SignalValue("ready")) == "", $"same toggles at the start {ToggleDiff(ToggleStates(), bot.SignalValue("ready"))}");
            string before = ToggleStates();
            var (btn, what) = ToggleButton();
            bot.Note("guest presses " + (what ?? "nothing (no wired switch found)"));
            if (btn != null) foreach (var x in Press(bot, btn)) yield return x;
            bot.Signal("pressed");
            foreach (var x in bot.Until(() => bot.HasSignal("after1"), 30, "host state after the press")) yield return x;
            foreach (var x in bot.Until(() => ToggleDiff(ToggleStates(), bot.SignalValue("after1")) == "", 5, null, soft: true)) yield return x;
            bot.Check(ToggleDiff(ToggleStates(), bot.SignalValue("after1")) == "", $"the guest's switch press shows the same everywhere {ToggleDiff(ToggleStates(), bot.SignalValue("after1"))}");
            bot.Check(btn == null || ToggleDiff(bot.SignalValue("after1"), before) != "", "the press changed something on the host: " + ToggleDiff(bot.SignalValue("after1"), before));
            foreach (var x in bot.Until(() => bot.HasSignal("after2"), 30, "host flipped toggles")) yield return x;
            foreach (var x in bot.Until(() => ToggleDiff(ToggleStates(), bot.SignalValue("after2")) == "", 5, null, soft: true)) yield return x;
            bot.Check(ToggleDiff(ToggleStates(), bot.SignalValue("after2")) == "", $"the host's toggles show here {ToggleDiff(ToggleStates(), bot.SignalValue("after2"))}, applied {Toggles.Instance.Applied}");

            var lamp = Object.FindObjectsOfType<SwingLightFlicker>().FirstOrDefault(l => l != null && !(Util.ScenePath.Of(l.transform) ?? "").Contains("Main Camera/"));
            bot.Check(lamp != null, "a hanging lamp exists");
            lamp?.ToggleManualOverride();   // what clicking the lamp does
            Object.FindObjectOfType<PrinterAlertSystem>()?.DismissAllAlerts();   // what walking up to the printer does
            foreach (var x in bot.Wait(1.5)) yield return x;
            bot.Note($"guest switched {Util.ScenePath.Of(lamp?.transform)} (manually off {lamp?._manualOverrideOff})");
            bot.Signal("guestlamp", ToggleStates());
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
