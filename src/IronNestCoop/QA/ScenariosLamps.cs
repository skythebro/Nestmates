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
        /// <summary>The click button of a hanging lamp (it calls SwingLightFlicker.ToggleManualOverride).</summary>
        static List<(LookAtTarget B, SwingLightFlicker L, string Path)> LampButtons()
        {
            var list = new List<(LookAtTarget, SwingLightFlicker, string)>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), false))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b == null) continue;
                foreach (var h in Wiring.ListenersOf(b))
                    if (h.Contains("SwingLightFlicker.ToggleManualOverride"))
                    {
                        var lamp = b.GetComponentInParent<SwingLightFlicker>() ?? b.transform.root.GetComponentInChildren<SwingLightFlicker>();
                        // the lamp the button names: search the button's ancestors' children for the flicker it targets
                        var t = b.transform;
                        while (lamp == null && t != null) { lamp = t.GetComponentInChildren<SwingLightFlicker>(); t = t.parent; }
                        if (lamp != null) list.Add((b, lamp, Util.ScenePath.Of(b.transform)));
                        break;
                    }
            }
            return list.OrderBy(x => x.Item3).ToList();
        }

        /// <summary>
        /// The guest clicks the same lamps again and again (real input path): every click must switch the lamp, the
        /// same on both machines. (Reported: after one click the client could not switch the light any more.)
        /// </summary>
        /// <summary>A lamp's switch state: hand switch, emergency override; and whether its light object shines now.</summary>
        static string LampFlags(SwingLightFlicker l) => $"{(l._manualOverrideOff ? 1 : 0)}{(l._emergencyPlayerOverride ? 1 : 0)}";
        static string LampLit(SwingLightFlicker l) => l.lightObjectToToggle == null ? "?" : l.lightObjectToToggle.activeSelf ? "lit" : "dark";

        static IEnumerable<object> LampsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Computer != null, 120, "guest mission loaded")) yield return x;
            bot.Signal("ready");
            string last = null;
            while (!bot.HasSignal("lampsdone"))
            {
                string want = bot.SignalValue("lampwant");
                if (want != null && want != last)
                {
                    last = want;
                    bot.Signal("lampstate", want + "|" + string.Join(",", LampButtons().Select(l => LampFlags(l.L))));
                }
                yield return null;
            }
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> LampsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Computer != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            var lamps = LampButtons();
            bot.Note($"{lamps.Count} lamps: {string.Join(" | ", lamps.Take(4).Select(l => l.Path))}");
            bot.Check(lamps.Count > 0, "lamps with click buttons exist");
            int n = 0;
            string Mine() => string.Join(",", lamps.Select(l => LampFlags(l.L)));
            string host = null;
            IEnumerable<object> HostView()
            {
                string tag = (++n).ToString();
                bot.Signal("lampwant", tag);
                foreach (var x in bot.Until(() => (bot.SignalValue("lampstate") ?? "").StartsWith(tag + "|"), 10, null)) yield return x;
                host = bot.SignalValue("lampstate").Substring(tag.Length + 1);
            }
            foreach (var (b, l, path) in lamps.Where(x => !x.Path.Contains("Emergency")).Take(2).Concat(lamps.Where(x => x.Path.Contains("Emergency")).Take(3)))
            {
                for (int k = 1; k <= 3; k++)
                {
                    string before = LampFlags(l);
                    foreach (var x in RealPress(bot, b)) yield return x;
                    foreach (var x in bot.Until(() => LampFlags(l) != before, 4, null, soft: true)) yield return x;
                    foreach (var x in bot.Wait(0.6)) yield return x;
                    foreach (var x in HostView()) yield return x;
                    bot.Check(LampFlags(l) != before, $"click {k} on {path} switched the lamp here (flags {before} -> {LampFlags(l)}, light {LampLit(l)})");
                    bot.Check(host == Mine(), $"click {k} on {path}: the host shows the same lamps (host {host}, here {Mine()})");
                }
            }
            bot.Signal("lampsdone");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
