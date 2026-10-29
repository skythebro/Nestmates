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
        static string ConsoleDump(GameObject console)
        {
            if (console == null) return "no card console";
            var parts = new List<string>();
            foreach (var mb in console.GetComponentsInChildren<MonoBehaviour>(true))
            {
                string tn = mb.GetIl2CppType().Name;
                if (tn == "StudioEventEmitter" || tn.Contains("Text") || tn.Contains("Localised")) continue;
                parts.Add($"{mb.transform.name}:{tn}{(mb.isActiveAndEnabled ? "" : "(off)")}");
            }
            return $"{Util.ScenePath.Of(console.transform)} -> {string.Join(", ", parts)}";
        }

        /// <summary>The card console's look: animators and buttons under it (what the player sees of the switch).</summary>
        static string ConsoleLook(GameObject console)
        {
            if (console == null) return "none";
            var fp = Fingerprint.Capture();
            string root = Util.ScenePath.Of(console.transform);
            string dials = string.Join(",", console.GetComponentsInChildren<DialInteractable>(false).Select(d => d.AccumulatedValue.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)));
            return "dials " + dials + " | " + string.Join(" | ", fp.Where(kv => kv.Key.Contains(root)).OrderBy(kv => kv.Key).Select(kv => kv.Key.Substring(kv.Key.IndexOf(root) + root.Length) + "=" + kv.Value));
        }

        /// <summary>
        /// A punchcard in the requisition slot opens its own small console (the shell cards: left/right magazine
        /// switch). The guest flips every button on it the way a player does; both machines must show the same
        /// console afterwards. (Reported: the client set "right", the host stayed "left", the shells went left.)
        /// </summary>
        static IEnumerable<object> MagazineHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            // fixture: the host's sandboxed profile owns the shell cards (the deck comes from the host's unlocks)
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PunchcardDefinitionV2>()))
            {
                var d = o.TryCast<PunchcardDefinitionV2>();
                if (d != null && !string.IsNullOrEmpty(d.ID) && d.ID.Contains("Shell") && !d.ID.Contains("Demo") && !d.name.Contains("(Clone)") && defs.Count < 3) defs.Add(d);
            }
            bot.Note($"unlocking {defs.Count} shell cards on the host: {string.Join(", ", System.Linq.Enumerable.Select(defs.ToArray(), d => d.ID))}");
            if (defs.Count > 0) ProgressionManager.Instance.UnlockPunchcards(defs.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PunchcardDefinitionV2>>());
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Console != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("ready");
            string last = null;
            while (!bot.HasSignal("magdone"))
            {
                if (bot.HasSignal("slotstate") && last == null)
                {
                    var hs = Console?.RequisitionSlot;
                    bot.Note($"host slot: item {hs?.itemSlot?.CurrentItem?.name ?? "empty"}, card {hs?.CurrentCard?.CurrentDefinition?.ID ?? "none"}, console {hs?.CurrentCardConsole?.name ?? "none"}");
                    last = "";
                }
                string want = bot.SignalValue("magwant");
                if (want != null && want != last)
                {
                    last = want;
                    var c = Console?.RequisitionSlot?.CurrentCardConsole;
                    bot.Signal("maglook", want + "#" + ConsoleLook(c));
                    if (want == "1") bot.Note("host console: " + ConsoleDump(c));
                }
                yield return null;
            }
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> MagazineGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("ready"), 200, "in mission")) yield return x;
            foreach (var x in bot.Until(() => Punchcards().Any(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null), 90, null, soft: true)) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var slot = Console?.RequisitionSlot;
            var card = Punchcards().FirstOrDefault(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null && c.CurrentDefinition.ID.Contains("Shell"))
                       ?? Punchcards().FirstOrDefault(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null);
            bot.Check(slot != null && card != null, $"a slot and a shell card ({card?.CurrentDefinition?.ID})");
            if (slot == null || card == null) { bot.Signal("magdone"); yield break; }
            Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);
            foreach (var x in bot.Until(() => slot.CurrentCardConsole != null, 20, null, soft: true)) yield return x;
            bot.Note($"guest slot: item {slot.itemSlot?.CurrentItem?.name ?? "empty"}, card {slot.CurrentCard?.CurrentDefinition?.ID ?? "none"}, console {slot.CurrentCardConsole?.name ?? "none"}");
            bot.Signal("slotstate");
            bot.Check(slot.CurrentCardConsole != null, $"the card console opened here ({card.CurrentDefinition.ID})");
            if (slot.CurrentCardConsole == null) { bot.Signal("magdone"); yield break; }
            foreach (var x in bot.Wait(2)) yield return x;
            var console = slot.CurrentCardConsole;
            bot.Note("guest console: " + ConsoleDump(console));
            int n = 0;
            string host = null;
            IEnumerable<object> HostLook()
            {
                string tag = (++n).ToString();
                bot.Signal("magwant", tag);
                foreach (var x in bot.Until(() => (bot.SignalValue("maglook") ?? "").StartsWith(tag + "#"), 10, null)) yield return x;
                host = bot.SignalValue("maglook").Substring(tag.Length + 1);
            }
            foreach (var x in HostLook()) yield return x;
            bot.Check(host != "none", "the host has the card console open too");
            bot.Check(host == ConsoleLook(console), $"the card console looks the same before anything is flipped (here {ConsoleLook(console)} / host {host})");
            // the magazine selector is a dial: turn it to each end, as a hand would
            foreach (var d in console.GetComponentsInChildren<DialInteractable>(false))
            {
                float lo = d.minOutputValue, hi = d.maxOutputValue;
                if (Mathf.Approximately(lo, hi)) { lo = 0; hi = 1; }
                foreach (var target in new[] { hi, lo, hi })
                {
                    foreach (var x in DragDial(bot, d, target)) yield return x;
                    foreach (var x in bot.Wait(1.2)) yield return x;
                    foreach (var x in HostLook()) yield return x;
                    bot.Check(host == ConsoleLook(console), $"selector turned to {target:F2}: the host's console shows the same (here {ConsoleLook(console)} / host {host})");
                }
            }
            var buttons = console.GetComponentsInChildren<LookAtTarget>(false).ToList();
            bot.Note($"{buttons.Count} buttons on the card console: {string.Join(", ", buttons.Select(b => b.name))}");
            foreach (var b in buttons)
            {
                for (int k = 1; k <= 2; k++)
                {
                    foreach (var x in RealPress(bot, b)) yield return x;
                    foreach (var x in bot.Wait(1.5)) yield return x;
                    foreach (var x in HostLook()) yield return x;
                    bot.Check(host == ConsoleLook(console), $"flip {k} of {b.name}: the host's console shows the same (here {ConsoleLook(console)} / host {host})");
                }
            }
            bot.Signal("magdone");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
