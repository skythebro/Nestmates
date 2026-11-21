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
            foreach (var x in bot.Until(() => bot.HasSignal("g1"), 120, "guest inserted the card and turned the wheels")) yield return x;
            foreach (var x in bot.Until(() => slot?.CurrentCardConsole != null, 10, null, soft: true)) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Note("host after the guest turned: " + GridState(slot));
            bot.Signal("h1", GridShown(slot));
            // phase 2: the host turns the wheels back, like a quick hand
            foreach (var x in bot.Until(() => bot.HasSignal("g1checked"), 30, "guest compared")) yield return x;
            if (slot?.CurrentCardConsole != null)
            {
                float d = -2.4f;
                foreach (var b in slot.CurrentCardConsole.GetComponentsInChildren<DialToSplitFlipDisplayBinder>(true))
                    if (b?.dial != null) { foreach (var x in FastTurn(b.dial, b.dial.AccumulatedValue + d)) yield return x; d = 1.6f; }
            }
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Note("host after its own turn: " + GridState(slot));
            bot.Signal("h2", GridShown(slot));
            foreach (var x in bot.Until(() => bot.HasSignal("guestdone"), 60, "guest done")) yield return x;
        }

        /// <summary>What each wheel shows (dial value rounded to the step, symbol, flap) - what a player sees.</summary>
        static string GridShown(RequisitionSlot slot)
        {
            var cc = slot?.CurrentCardConsole;
            if (cc == null) return "no panel";
            return string.Join(" ; ", cc.GetComponentsInChildren<DialToSplitFlipDisplayBinder>(true).Select(b => $"{b.dial?.AccumulatedValue:F1}:{b.splitFlipDisplay?.desiredValue}"));
        }

        /// <summary>A quick hand: a small step every frame, then let go.</summary>
        static IEnumerable<object> FastTurn(DialInteractable d, float value)
        {
            bool unlimited = d.dialMode == DialInteractable.DialMode.Unlimited;
            void Set(float v) { if (unlimited) d.SetAccumulatedValueUnlimited(v, true, true); else d.SetDialValue(v); }
            d.BeginDialDrag();
            float from = d.AccumulatedValue;
            int frames = Mathf.Max(4, Mathf.CeilToInt(Mathf.Abs(value - from) / 0.2f));
            for (int i = 1; i <= frames; i++) { Set(Mathf.Lerp(from, value, (float)i / frames)); yield return null; }
            d.EndDialDrag();
            yield return null;
        }

        /// <summary>
        /// The scout plane card's grid square on the requisition console's split-flap wheels: turned quickly by the guest,
        /// then by the host. After each turn both players' wheels must show the same square.
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
            if (slot == null || card == null) { bot.Signal("g1"); yield break; }
            Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);   // a drop into the slot
            foreach (var x in bot.Until(() => slot.CurrentCardConsole != null, 15, null, soft: true)) yield return x;
            bot.Check(slot.CurrentCardConsole != null, "the guest's slot shows the card's grid panel");
            if (slot.CurrentCardConsole == null) { bot.Signal("g1"); yield break; }
            var binders = slot.CurrentCardConsole.GetComponentsInChildren<DialToSplitFlipDisplayBinder>(true);
            foreach (var x in bot.Until(() => binders.All(b => b?.dial != null && Controls.Instance.DialIds.Any(id => id.StartsWith(Util.ScenePath.Of(b.dial.transform) + "|"))), 15, null, soft: true)) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            foreach (var b in binders)
                bot.Note($"binder {b.name}: min interval {b.minUpdateIntervalSeconds}, only on index change {b.onlySendOnIndexChange}, rounding {b.indexRounding}, symbols '{b.orderedSymbols}', dial mode {b.dial?.dialMode}");
            // phase 1: the guest turns both wheels like a quick hand
            float d = 6.4f;
            foreach (var b in binders)
                if (b?.dial != null) { foreach (var x in FastTurn(b.dial, b.dial.AccumulatedValue + d)) yield return x; d = 4.4f; }
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Note("guest after its own turn: " + GridState(slot));
            bot.Signal("g1", GridShown(slot));
            foreach (var x in bot.Until(() => bot.HasSignal("h1"), 30, "host reported")) yield return x;
            bot.Check(GridShown(slot) == bot.SignalValue("h1"), $"after the guest's quick turn both show the same square (guest {GridShown(slot)} | host {bot.SignalValue("h1")})");
            bot.Signal("g1checked");
            foreach (var x in bot.Until(() => bot.HasSignal("h2"), 30, "host turned")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Note("guest after the host turned: " + GridState(slot));
            bot.Check(GridShown(slot) == bot.SignalValue("h2"), $"after the host's quick turn both show the same square (guest {GridShown(slot)} | host {bot.SignalValue("h2")})");
            // phase 3: the guest's panel is made again on the guest only (as a deck rebuild used to do): the fresh
            // wheels start at their defaults and must pick up the host's square
            var cur = slot.CurrentCard;
            if (cur != null) Coop.Apply("qa: panel again", () => { slot.RemoveCard(cur, false); slot.PlaceCard(cur); });
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Note("guest's fresh panel: " + GridState(slot));
            foreach (var x in bot.Until(() => GridShown(slot) == bot.SignalValue("h2"), 12, null, soft: true)) yield return x;
            bot.Note("guest's fresh panel later: " + GridState(slot));
            bot.Check(GridShown(slot) == bot.SignalValue("h2"), $"a fresh grid panel on the guest shows the host's square (guest {GridShown(slot)} | host {bot.SignalValue("h2")})");
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
