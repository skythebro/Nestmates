using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static Transform ReqConsole() => Resources.FindObjectsOfTypeAll<RequisitionConsoleManager>()
            .FirstOrDefault(m => m != null && !string.IsNullOrEmpty(m.gameObject.scene.name))?.transform;

        /// <summary>Probe: what the requisition console is made of (component types and names), to find the grid selector.</summary>
        static IEnumerable<string> ReqDump()
        {
            var root = ReqConsole();
            if (root == null) { yield return "no requisition console"; yield break; }
            while (root.parent != null && !root.parent.name.StartsWith("Master Turret")) root = root.parent;
            yield return "console root: " + Util.ScenePath.Of(root);
            var types = new Dictionary<string, int>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string t = c.GetIl2CppType().Name;
                types[t] = types.TryGetValue(t, out int n) ? n + 1 : 1;
            }
            yield return "types: " + string.Join(", ", types.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} x{kv.Value}"));
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string t = c.GetIl2CppType().Name, path = Util.ScenePath.Of(c.transform);
                bool gridish = path.IndexOf("grid", System.StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("grid", System.StringComparison.OrdinalIgnoreCase) >= 0
                               || t.Contains("Dial") || t.Contains("Slider") || t.Contains("Odometer") || t.Contains("Cycler") || t.Contains("Selector") || t.Contains("Wheel");
                if (!gridish) continue;
                string v = c.TryCast<DialInteractable>() is DialInteractable d ? $" value {d.AccumulatedValue:F2}" : "";
                yield return $"  {t} at {path}{v}";
            }
        }

        static IEnumerable<object> ReqGridHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            // every card into this mission's deck (as the dev tools do), so the scout plane card is there
            var con0 = RequisitionConsoleManager.Instance;
            if (con0 != null)
            {
                var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
                foreach (var d in Resources.FindObjectsOfTypeAll<PunchcardDefinitionV2>())
                    if (d != null && !string.IsNullOrEmpty(d.ID) && !d.ID.Contains("Demo") && !d.name.Contains("(Clone)")) defs.Add(d);
                con0.AddSetCardsToDeck(defs);
                MissionStatsTracker.Instance?.AddRequisitionPoints(500, "qa");
            }
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Signal("deck", DeckLine());
            var slot = Console?.RequisitionSlot;
            for (int t = 0; t < 60 && !bot.HasSignal("turned"); t++) { if (slot?.CurrentCard != null) bot.Note($"host t{t}: {GridState(slot)}"); foreach (var x in bot.Wait(1)) yield return x; }
            foreach (var x in bot.Until(() => slot?.CurrentCardConsole != null, 10, null, soft: true)) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            string hostGrid = GridState(slot);
            bot.Note($"host slot card {slot?.CurrentCard?.CurrentDefinition?.ID}; host grid: {hostGrid}");
            bot.Signal("hostgrid", hostGrid);
            foreach (var x in bot.Until(() => bot.HasSignal("guestdone"), 60, "guest done")) yield return x;
        }

        /// <summary>
        /// The scout plane card's grid square, set by a guest on the requisition console's split-flap wheels: the guest's
        /// wheels and the host's must show the same square, and the card on the host must get it.
        /// </summary>
        static IEnumerable<object> ReqGridGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("deck"), 180, "in mission, cards dealt")) yield return x;
            string want = bot.SignalValue("deck");
            foreach (var x in bot.Until(() => DeckLine() == want, 20, null, soft: true)) yield return x;
            bot.Check(DeckLine() == want, $"the guest has the host's deck ({DeckLine()} vs {want})");
            var slot = Console?.RequisitionSlot;
            var card = Punchcards().FirstOrDefault(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null && c.CurrentDefinition.ID == "ScoutPlane");
            bot.Check(slot != null && card != null, "the guest has a slot and a scout plane card");
            if (slot == null || card == null) { bot.Signal("turned"); yield break; }
            Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);   // a drop into the slot
            foreach (var x in bot.Until(() => slot.CurrentCardConsole != null, 15, null, soft: true)) yield return x;
            bot.Check(slot.CurrentCardConsole != null, "the guest's slot shows the card's grid panel");
            for (int t = 0; t < 6; t++) { bot.Note($"guest +{t}s: {GridState(slot)}"); foreach (var x in bot.Wait(1)) yield return x; }   // the wheels get registered for sync
            if (slot.CurrentCardConsole != null)
            {
                var binders = slot.CurrentCardConsole.GetComponentsInChildren<DialToSplitFlipDisplayBinder>(true);
                foreach (var x in bot.Until(() => binders.All(b => b?.dial != null && Controls.Instance.DialIds.Any(id => id.StartsWith(Util.ScenePath.Of(b.dial.transform) + "|"))), 10, null, soft: true)) yield return x;
                bot.Note($"grid wheels registered for sync: {binders.Count(b => b?.dial != null && Controls.Instance.DialIds.Any(id => id.StartsWith(Util.ScenePath.Of(b.dial.transform) + "|")))} of {binders.Length}");
                float step = 3f;
                foreach (var b in binders)
                    if (b?.dial != null) { foreach (var x in DragDial(bot, b.dial, b.dial.AccumulatedValue + step)) yield return x; step = 2f; }
            }
            for (int t = 0; t < 4; t++) { bot.Note($"guest after turning +{t}s: {GridState(slot)}"); foreach (var x in bot.Wait(1)) yield return x; }
            bot.Signal("turned");
            foreach (var x in bot.Until(() => bot.HasSignal("hostgrid"), 40, "host reported its grid")) yield return x;
            string mine = GridState(slot), host = bot.SignalValue("hostgrid");
            bot.Note("guest grid: " + mine);
            bot.Check(mine == host, $"the guest's wheels show the square the host's card got (guest {mine} | host {host})");
            bot.Signal("guestdone");
        }

        /// <summary>Each grid wheel: dial value, the symbol the binder picked, the flap display, then the card's variables.</summary>
        static string GridState(RequisitionSlot slot)
        {
            var cc = slot?.CurrentCardConsole;
            if (cc == null) return "no panel";
            var parts = new List<string>();
            foreach (var b in cc.GetComponentsInChildren<DialToSplitFlipDisplayBinder>(true))
            {
                var sym = b.orderedSymbols;
                string picked = b._lastIndex >= 0 && sym != null && b._lastIndex < sym.Length ? sym[b._lastIndex].ToString() : "?";
                parts.Add($"wheel {b.dial?.AccumulatedValue:F1} -> {picked} shows '{b.splitFlipDisplay?.desiredValue}'");
            }
            foreach (var v in cc.GetComponentsInChildren<PunchcardVariable>(true))
                parts.Add($"card {v.VariableID}={v.VariableType}:{v.VariableText}/{(v.VariableCoordinate == null ? "-" : v.VariableCoordinate.X + "," + v.VariableCoordinate.Y)}");
            return string.Join(" ; ", parts);
        }

        static IEnumerable<string> CardConsoleDump(RequisitionSlot slot)
        {
            var cc = slot?.CurrentCardConsole;
            if (cc == null) { yield return "no card console"; yield break; }
            var root = cc.transform;
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                string t = c.GetIl2CppType().Name;
                if (t == "Transform" || t == "MeshRenderer" || t == "MeshFilter" || t.Contains("Particle") || t == "StudioEventEmitter") continue;
                string v = "";
                if (c.TryCast<DialInteractable>() is DialInteractable d) v = $" value {d.AccumulatedValue:F2}";
                else if (c.TryCast<TMPro.TMP_Text>() is TMPro.TMP_Text tx) v = $" text '{tx.text}'";
                else if (c.TryCast<LookAtTarget>() is LookAtTarget b) v = $" isActive {b.isActive}";
                yield return $"  {t} at {Util.ScenePath.Of(c.transform)}{v}";
            }
        }
    }
}
