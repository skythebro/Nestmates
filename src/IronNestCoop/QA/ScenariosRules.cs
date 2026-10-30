using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static DialInteractable VolumeDial => Object.FindObjectOfType<RecordPlayerVolumeDialBridge>()?.volumeDial;

        /// <summary>A point along the dial's output range.</summary>
        static float At(DialInteractable d, float t)
        {
            float lo = d.minOutputValue, hi = d.maxOutputValue;
            if (Mathf.Approximately(lo, hi)) { lo = 0; hi = 1; }
            return Mathf.Lerp(lo, hi, t);
        }

        static LookAtTarget MapClearSwitch()
        {
            LookAtTarget clear = null;
            foreach (var h in Wiring.Callers("DeleteAllChildren")) clear ??= h.Owner.TryCast<LookAtTarget>() ?? h.Owner.GetComponent<LookAtTarget>();
            return clear;
        }

        static LookAtTarget HoverButton()
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LookAtTarget>()))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b != null && b.gameObject.activeInHierarchy && Wiring.ListenersOf(b).Any(l => l.StartsWith("onLookAt:")) && !(Util.ScenePath.Of(b.transform) ?? "").Contains("Main Camera/")) return b;
            }
            return null;
        }

        /// <summary>
        /// Host rules: guests get the host's rules, and each rule really switches between shared and own. Volume via
        /// a real dial drag, hover via the button's own look-at calls, drawings via the game's drawing handlers and
        /// the map's delete-all switch.
        /// </summary>
        static IEnumerable<object> RulesHost(Bot bot)
        {
            bool vol0 = Settings.SharedRecordVolume, hov0 = Settings.SharedHover, draw0 = Settings.SharedMapDrawings;
            Settings.SharedRecordVolume = Settings.SharedHover = Settings.SharedMapDrawings = true;
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && VolumeDial != null && Placer != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            var dial = VolumeDial; var p = Placer;
            bot.Note($"volume dial {Util.ScenePath.Of(dial.transform)}: mode {dial.dialMode}, output {dial.minOutputValue}..{dial.maxOutputValue}, now {dial.AccumulatedValue}");
            bot.Signal("ready", HostRules.Mask.ToString());

            // 1. shared volume: the host turns it, the guest hears the same
            float v1 = At(dial, 0.3f);
            foreach (var x in DragDial(bot, dial, v1)) yield return x;
            bot.Signal("vol1", dial.AccumulatedValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            // 2. own volume: neither side's dial moves the other's
            foreach (var x in bot.Until(() => bot.HasSignal("checked1"), 30, "guest checked the shared volume")) yield return x;
            Settings.SharedRecordVolume = false;
            bot.Signal("volown", HostRules.Mask.ToString());
            foreach (var x in bot.Until(() => bot.HasSignal("guestvol"), 30, "guest turned its own volume")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(Mathf.Abs(dial.AccumulatedValue - v1) < 0.01f, $"the guest's own volume did not move the host's dial ({dial.AccumulatedValue:F3}, was {v1:F3})");
            foreach (var x in DragDial(bot, dial, At(dial, 0.8f))) yield return x;
            bot.Signal("hostvol");

            // 3. hover for the looker only
            foreach (var x in bot.Until(() => bot.HasSignal("checked2"), 30, "guest checked its own volume")) yield return x;
            Settings.SharedHover = false;
            bot.Signal("hoverown", HostRules.Mask.ToString());
            foreach (var x in bot.Until(() => bot.HasSignal("hoverready"), 30, "guest has the hover rule")) yield return x;
            var hb = HoverButton();
            bot.Note($"host hovers {Util.ScenePath.Of(hb?.transform)}");
            if (hb != null) { RealHover(hb, true); foreach (var x in bot.Wait(1.5)) yield return x; RealHover(hb, false); }
            bot.Signal("hovered", hb == null ? "none" : "1");

            // 4. own map drawings
            foreach (var x in bot.Until(() => bot.HasSignal("checked3"), 30, "guest checked hover")) yield return x;
            bot.Check(Draw(bot, p, new Vector2(-40, 30), new Vector2(60, -20)), "host drew a line while drawings are shared");
            bot.Signal("hostdrew");
            foreach (var x in bot.Until(() => bot.HasSignal("sawline"), 30, "guest saw the shared line")) yield return x;
            Settings.SharedMapDrawings = false;
            int hostLines = LiveMarkers(p);
            bot.Signal("drawown", HostRules.Mask.ToString());
            foreach (var x in bot.Until(() => bot.HasSignal("guestdrew"), 40, "guest drew and cleared its own map")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            bot.Check(LiveMarkers(p) == hostLines, $"the guest's own drawing and delete-all left the host's map alone ({LiveMarkers(p)} lines, had {hostLines})");
            // back to shared: the host's lines show on the guest's map again
            Settings.SharedMapDrawings = true;
            bot.Signal("drawshared", LiveMarkers(p).ToString());

            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            Settings.SharedRecordVolume = vol0; Settings.SharedHover = hov0; Settings.SharedMapDrawings = draw0;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> RulesGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && VolumeDial != null && Placer != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            var dial = VolumeDial; var p = Placer;
            uint Want(string sig) => uint.Parse(bot.SignalValue(sig));
            bot.Check(HostRules.Known && HostRules.Mask == Want("ready"), $"the host's rules arrived ({HostRules.Describe(HostRules.Mask)})");

            foreach (var x in bot.Until(() => bot.HasSignal("vol1"), 30, "host turned the volume")) yield return x;
            float v1 = float.Parse(bot.SignalValue("vol1"), System.Globalization.CultureInfo.InvariantCulture);
            foreach (var x in bot.Until(() => Mathf.Abs(dial.AccumulatedValue - v1) < 0.01f, 5, null, soft: true)) yield return x;
            bot.Check(Mathf.Abs(dial.AccumulatedValue - v1) < 0.01f, $"shared volume: the host's dial turned here too ({dial.AccumulatedValue:F3} vs {v1:F3})");
            bot.Signal("checked1");

            foreach (var x in bot.Until(() => bot.HasSignal("volown") && HostRules.Mask == Want("volown"), 15, "the volume is each player's own")) yield return x;
            float mine = At(dial, 0.55f);
            dial.BeginDialDrag(); bool drags = dial.IsDragging; dial.EndDialDrag();
            bot.Note($"guest volume dial: drag starts {drags}, enabled {dial.isActiveAndEnabled}, value {dial.AccumulatedValue}");
            foreach (var x in DragDial(bot, dial, mine)) yield return x;
            mine = dial.AccumulatedValue;   // where the drag landed (a bot drag lands near the target, not exactly on it)
            bot.Signal("guestvol");
            foreach (var x in bot.Until(() => bot.HasSignal("hostvol"), 30, "host turned its own volume")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(Mathf.Abs(dial.AccumulatedValue - mine) < 0.01f, $"own volume: the host's dial did not move mine ({dial.AccumulatedValue:F3}, set {mine:F3})");
            bot.Signal("checked2");

            foreach (var x in bot.Until(() => bot.HasSignal("hoverown") && HostRules.Mask == Want("hoverown"), 15, "hover is for the looker only")) yield return x;
            int hovers = Controls.Instance.HoversApplied;
            bot.Signal("hoverready");
            foreach (var x in bot.Until(() => bot.HasSignal("hovered"), 30, "host hovered")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            if (bot.SignalValue("hovered") != "none")
                bot.Check(Controls.Instance.HoversApplied == hovers, $"own hover: the host's hover did not play here ({Controls.Instance.HoversApplied - hovers} applied)");
            bot.Signal("checked3");

            foreach (var x in bot.Until(() => bot.HasSignal("hostdrew"), 30, "host drew")) yield return x;
            foreach (var x in bot.Until(() => LiveMarkers(p) >= 1, 8, "the host's shared line is here")) yield return x;
            int sharedLines = LiveMarkers(p);
            bot.Signal("sawline");
            foreach (var x in bot.Until(() => bot.HasSignal("drawown") && HostRules.Mask == Want("drawown"), 15, "drawings are each player's own")) yield return x;
            bot.Check(Draw(bot, p, new Vector2(-70, -60), new Vector2(30, 45)), "guest drew on its own map");
            var clear = MapClearSwitch();
            bot.Check(clear != null && Controls.Instance.IsPersonal(clear), "the delete-all switch is this player's own");
            if (clear != null)
            {
                foreach (var x in Press(bot, clear)) yield return x;
                foreach (var x in bot.Until(() => LiveMarkers(p) == 0, 8, null, soft: true)) yield return x;
                bot.Check(LiveMarkers(p) == 0, $"the delete-all switch cleared my map ({LiveMarkers(p)} left, had {sharedLines + 1})");
            }
            bot.Signal("guestdrew");
            foreach (var x in bot.Until(() => bot.HasSignal("drawshared"), 30, "drawings shared again")) yield return x;
            int hostLines = int.Parse(bot.SignalValue("drawshared"));
            foreach (var x in bot.Until(() => LiveMarkers(p) == hostLines, 8, null, soft: true)) yield return x;
            bot.Check(LiveMarkers(p) == hostLines, $"shared again: the host's lines show here ({LiveMarkers(p)} vs {hostLines})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
