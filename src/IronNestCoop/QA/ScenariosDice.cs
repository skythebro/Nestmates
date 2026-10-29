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
        /// <summary>Where every delivery a scene spawner dropped lies, relative to its spawner (the random offset).</summary>
        static string DeliveriesLine()
        {
            var parts = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PrefabSpawner>(), true))
            {
                var sp = o.TryCast<PrefabSpawner>();
                if (sp == null || string.IsNullOrEmpty(sp.gameObject.scene.name)) continue;
                var t = sp.transform;
                for (int i = 0; i < t.childCount; i++)
                {
                    var c = t.GetChild(i);
                    if (!c.name.Contains("(Clone)")) continue;
                    var p = c.localPosition; var e = c.localEulerAngles;
                    parts.Add($"{sp.name}/{c.name} at {p.x:F3};{p.y:F3};{p.z:F3} turned {e.y:F1}");
                }
            }
            parts.Sort();
            return parts.Count == 0 ? "none" : string.Join(" | ", parts);
        }

        /// <summary>The names both machines give the items deliveries brought (a record in a delivery): they must
        /// match, or each player has their own copy of it.</summary>
        static string DeliveredIdsLine()
        {
            var ids = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PrefabSpawner>(), true))
            {
                var sp = o.TryCast<PrefabSpawner>();
                if (sp == null || string.IsNullOrEmpty(sp.gameObject.scene.name)) continue;
                foreach (var d in sp.GetComponentsInChildren<DraggableItem>(true)) if (d != null) ids.Add(Util.ItemId.Of(d));
            }
            ids.Sort();
            return ids.Count == 0 ? "none" : string.Join(" | ", ids);
        }

        /// <summary>Where the dealt punchcards lie (each slid onto the deck with a random spread).</summary>
        static string DealtLine()
        {
            var parts = Punchcards().Where(c => c != null && c.gameObject.activeInHierarchy && c.CurrentDefinition != null)
                .Select(c => { var p = c.transform.position; return $"{c.CurrentDefinition.ID} {p.x:F2};{p.y:F2};{p.z:F2}"; }).ToList();
            parts.Sort();
            return parts.Count == 0 ? "none" : string.Join(" | ", parts);
        }

        static PrefabSpawner TestSpawner() => Object.FindObjectsOfType(Il2CppType.Of<PrefabSpawner>(), true)
            .Select(o => o.TryCast<PrefabSpawner>())
            .Where(s => s != null && !string.IsNullOrEmpty(s.gameObject.scene.name))
            .OrderBy(s => Util.ScenePath.Of(s.transform)).FirstOrDefault();

        /// <summary>
        /// Shared dice: the host fires a delivery spawner twice and deals every punchcard; the guest must see the
        /// deliveries and the dealt cards at the same spots (each was placed with a random roll).
        /// </summary>
        static IEnumerable<object> DiceHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;

            var sp = TestSpawner();
            bot.Check(sp != null, "a delivery spawner in the turret");
            if (sp != null)
            {
                bot.Note("spawner: " + Util.ScenePath.Of(sp.transform));
                sp.Spawn();
                foreach (var x in bot.Wait(0.5)) yield return x;
                sp.Spawn();
            }
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("deliveries", DeliveriesLine());
            bot.Signal("deliveredids", DeliveredIdsLine());

            var con = RequisitionConsoleManager.Instance;
            if (con != null)
            {
                var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PunchcardDefinitionV2>()))
                {
                    var d = o.TryCast<PunchcardDefinitionV2>();
                    if (d != null && !string.IsNullOrEmpty(d.ID) && !d.ID.Contains("Demo") && !d.name.Contains("(Clone)") && defs.Count < 6) defs.Add(d);
                }
                con.AddSetCardsToDeck(defs);
            }
            foreach (var x in bot.Wait(5)) yield return x;   // cards slide out, the guest rebuilds the deck
            bot.Signal("deck", DealtLine());
            bot.Note($"shared rolls here: {SharedDice.Seeded}");
            foreach (var x in bot.Until(() => bot.HasSignal("guestorder"), 90, "guest moved a card in the fan")) yield return x;
            foreach (var x in bot.Until(() => DealtLine() == bot.SignalValue("guestorder"), 10, null, soft: true)) yield return x;
            bot.Check(DealtLine() == bot.SignalValue("guestorder"), $"the guest's move inside the fan reached the host (here {DealtLine()} / guest {bot.SignalValue("guestorder")})");
            bot.Signal("hostorder");
            foreach (var x in bot.Until(() => bot.HasSignal("movechecked"), 30, "guest checked its move")) yield return x;
            // the host takes a card out of the middle of the fan (into the requisition slot): the fan closes up
            var hdeck = DraggableItemDeckArea.AllDecks?.ToArray().FirstOrDefault(d => d != null && d.isActiveAndEnabled && d.items != null && d.items.Count > 2);
            var hslot = Console?.RequisitionSlot;
            if (hdeck != null && hslot != null)
            {
                var mid = hdeck.items[hdeck.items.Count / 2];
                bot.Note("host takes out " + Util.ItemId.Of(mid));
                Music.PlaceInto(mid, hslot.itemSlot);
                foreach (var x in bot.Wait(3)) yield return x;
                bot.Signal("compact", DealtLine());
            }
            else { bot.Note("no deck/slot for the close-up check"); bot.Signal("compact", "skip"); }
            // regression: after the host pressed the console's button (it spreads the cards out, and gathers
            // them back into the fan), the guest's cards stayed where they were
            foreach (var x in bot.Until(() => bot.HasSignal("compactchecked"), 30, "guest checked the close-up")) yield return x;
            // 1: the cards spread over the console (taken out of the fan one by one, as by hand); 2: the game's
            // "everything back into the deck" (DragSurface.MoveItemsToDeck: all cards at once, then they slide)
            var rdeck = DraggableItemDeckArea.AllDecks?.ToArray().FirstOrDefault(d => d != null && d.isActiveAndEnabled && d.items != null && d.items.Count > 1);
            var rsurf = rdeck?.GetComponentInParent<DragSurface>() ?? Object.FindObjectsOfType(Il2CppType.Of<DragSurface>(), true).Select(o => o.TryCast<DragSurface>())
                .FirstOrDefault(sf => sf != null && Util.ScenePath.Of(sf.transform).EndsWith("Requisition Console/Drag Surface"));
            bot.Check(rdeck != null && rsurf != null, "a fanned deck on the requisition console's surface");
            if (rdeck != null && rsurf != null)
            {
                var cards = new List<DraggableItem>(); foreach (var c in rdeck.items) if (c != null) cards.Add(c);
                var surfaces = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DragSurface>(1); surfaces[0] = rsurf;
                for (int i = 0; i < cards.Count; i++)
                {
                    var st = MissionSaveManager.CaptureDraggableSurfaceState(cards[i]);
                    rdeck.RemoveItem(cards[i]);
                    st.Location = (int)DraggableItem.ItemLocation.Surface;
                    var lp = st.SurfaceLocalPosition; lp.X += (i - cards.Count / 2f) * 0.13f; lp.Z += (i % 2) * 0.1f; st.SurfaceLocalPosition = lp;
                    MissionSaveManager.RestoreDraggableSurfaceState(cards[i], st, surfaces);
                    foreach (var x in bot.Wait(0.3)) yield return x;
                }
                foreach (var x in bot.Wait(3)) yield return x;
                bot.Note("spread: " + DealtLine());
                bot.Signal("reset1", DealtLine());
                foreach (var x in bot.Until(() => bot.HasSignal("resetchecked1"), 30, "guest checked the spread cards")) yield return x;
                rsurf.MoveItemsToDeck();
                foreach (var x in bot.Wait(5)) yield return x;   // they slide into the fan
                bot.Note("gathered: " + DealtLine());
                bot.Signal("reset2", DealtLine());
                foreach (var x in bot.Until(() => bot.HasSignal("resetchecked2"), 30, "guest checked the gathered deck")) yield return x;
            }
            else { bot.Signal("reset1", "skip"); bot.Signal("reset2", "skip"); }
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> DiceGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("deliveries"), 240, "host spawned deliveries")) yield return x;
            foreach (var x in bot.Until(() => DeliveriesLine() == bot.SignalValue("deliveries"), 10, null, soft: true)) yield return x;
            bot.Check(DeliveriesLine() != "none", "deliveries arrived here");
            bot.Check(DeliveriesLine() == bot.SignalValue("deliveries"), $"deliveries landed on the same spot (here {DeliveriesLine()} / host {bot.SignalValue("deliveries")})");
            // like a record seen in play: this player had it in hand (moved elsewhere) before anything named it
            DraggableItem moved = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PrefabSpawner>(), true))
            {
                var sp = o.TryCast<PrefabSpawner>();
                if (sp == null || string.IsNullOrEmpty(sp.gameObject.scene.name) || moved != null) continue;
                foreach (var d in sp.GetComponentsInChildren<DraggableItem>(true))
                    if (moved == null && d != null && d.transform.parent != null && d.transform.parent.parent != null) moved = d;
            }
            if (moved != null) { moved.transform.SetParent(moved.transform.parent.parent, true); bot.Note("moved " + moved.name + " before naming it"); }
            foreach (var x in bot.Until(() => bot.HasSignal("deliveredids"), 20, "host's delivered item names")) yield return x;
            bot.Check(DeliveredIdsLine() == bot.SignalValue("deliveredids"), $"delivered items have the same names (here {DeliveredIdsLine()} / host {bot.SignalValue("deliveredids")})");

            foreach (var x in bot.Until(() => bot.HasSignal("deck"), 60, "host dealt cards")) yield return x;
            foreach (var x in bot.Until(() => DealtLine() == bot.SignalValue("deck"), 10, null, soft: true)) yield return x;
            bot.Check(DealtLine() == bot.SignalValue("deck"), $"dealt cards lie in the same spots (here {DealtLine()} / host {bot.SignalValue("deck")})");
            // this player drops the first card of the fan back in at the end (what a drag inside the fan does)
            var deck = DraggableItemDeckArea.AllDecks?.ToArray().FirstOrDefault(d => d != null && d.isActiveAndEnabled && d.items != null && d.items.Count > 1);
            bot.Check(deck != null, "a fanned deck with cards");
            if (deck != null)
            {
                // the game's own drop: the card is let go past the far end of the fan and goes back in there
                var first = deck.items[0];
                var last = deck.items[deck.items.Count - 1].transform.position;
                var prev = deck.items[deck.items.Count - 2].transform.position;
                deck.RemoveItem(first);
                first.transform.position = last + (last - prev);
                deck.AddBack(first);
                bot.Note($"after the drop the first card is {(deck.items.IndexOf(first))} of {deck.items.Count}");
                foreach (var x in bot.Wait(2)) yield return x;
                string mine = DealtLine();
                bot.Signal("guestorder", mine);
                foreach (var x in bot.Until(() => bot.HasSignal("hostorder"), 40, "host checked")) yield return x;
                foreach (var x in bot.Wait(1)) yield return x;
                bot.Check(DealtLine() == mine, $"my move stayed (here {DealtLine()} / my move {mine})");
                bot.Signal("movechecked");
            }
            if (deck == null) bot.Signal("movechecked");
            foreach (var x in bot.Until(() => bot.HasSignal("compact"), 60, "host took a card out")) yield return x;
            if (bot.SignalValue("compact") != "skip")
            {
                foreach (var x in bot.Until(() => DealtLine() == bot.SignalValue("compact"), 8, null, soft: true)) yield return x;
                bot.Check(DealtLine() == bot.SignalValue("compact"), $"the fan closed up here too (here {DealtLine()} / host {bot.SignalValue("compact")})");
            }
            bot.Signal("compactchecked");
            for (int press = 1; press <= 2; press++)
            {
                int p = press;
                foreach (var x in bot.Until(() => bot.HasSignal("reset" + p), 60, press == 1 ? "host spread the cards" : "host put the cards back")) yield return x;
                if (bot.SignalValue("reset" + p) != "skip")
                {
                    foreach (var x in bot.Until(() => DealtLine() == bot.SignalValue("reset" + p), 8, null, soft: true)) yield return x;
                    if (DealtLine() != bot.SignalValue("reset" + p))
                    {
                        var dk = DraggableItemDeckArea.AllDecks?.ToArray().FirstOrDefault(d => d != null && d.isActiveAndEnabled);
                        var inFan = new HashSet<System.IntPtr>(); if (dk?.items != null) foreach (var it in dk.items) if (it != null) inFan.Add(it.Pointer);
                        bot.Note("cards here: " + string.Join(" | ", Punchcards().Where(c => c != null && c.gameObject.activeInHierarchy).Select(c =>
                        {
                            var di = c.DraggableItem ?? c.GetComponent<DraggableItem>();
                            return $"{c.CurrentDefinition?.ID} loc {di?.CurrentLocation} parent {c.transform.parent?.name} inFan {(di != null && inFan.Contains(di.Pointer))} drag {di?.IsDragging} local {c.transform.localPosition:F2}";
                        })) + $" | deck {dk?.name} items {dk?.items?.Count}");
                    }
                    bot.Check(DealtLine() == bot.SignalValue("reset" + p), $"after the host {(press == 1 ? "spread the cards" : "put every card back into the deck")} they lie as on the host (here {DealtLine()} / host {bot.SignalValue("reset" + p)})");
                }
                bot.Signal("resetchecked" + press);
            }
            bot.Note($"shared rolls here: {SharedDice.Seeded}");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
