using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// Requisition: the guest puts a punchcard into the requisition slot and pulls the lever. The host must spend
    /// the points and run the card once; everyone ends with the same points, the same deck and the same gun state.
    /// </summary>
    public static partial class Scenarios
    {
        static RequisitionConsoleManager Console => RequisitionConsoleManager.Instance;

        static List<PunchcardRuntime> Punchcards() =>
            Console?.GetAllCards()?.Where(c => c != null).ToList() ?? new List<PunchcardRuntime>();

        static string DeckLine() =>
            string.Join(",", Punchcards().Select(c => $"{c.CurrentDefinition?.ID}:{(c.gameObject.activeInHierarchy ? "on" : "off")}").OrderBy(x => x));

        static int Points => MissionStatsTracker.Instance?.RequisitionPoints ?? -1;

        static IEnumerable<object> RequisitionHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            // fixture: the host has two punchcards unlocked (its own sandboxed profile); the guest has none
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<PunchcardDefinitionV2>()))
            { var d = o.TryCast<PunchcardDefinitionV2>(); if (d != null && !string.IsNullOrEmpty(d.ID) && d.Cost > 0 && d.Cost < 2000 && !d.ID.Contains("Demo") && !d.name.Contains("(Clone)") && defs.Count < 2) defs.Add(d); }
            bot.Note($"punchcard definitions loaded: unlocking {defs.Count} on the host");
            bot.Check(defs.Count > 0, "punchcard definitions are loaded");
            if (defs.Count > 0) ProgressionManager.Instance.UnlockPunchcards(defs.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PunchcardDefinitionV2>>());
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Console != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Until(() => Punchcards().Count > 0, 60, "punchcards dealt")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            var cards = Punchcards();
            bot.Note($"host deck: {DeckLine()}; ids {string.Join(", ", cards.Select(c => ItemId.Of(c.DraggableItem ?? c.GetComponent<DraggableItem>())))}; points {Points}; slot {Console.RequisitionSlot?.name}");
            int maxCost = cards.Max(c => c.CurrentDefinition?.Cost ?? 0);
            bot.Note($"card costs: {string.Join(", ", cards.Select(c => c.CurrentDefinition?.ID + "=" + c.CurrentDefinition?.Cost))}");
            if (Points < maxCost + 100) MissionStatsTracker.Instance.AddRequisitionPoints(maxCost + 100 - Points, "qa");   // enough to afford any card
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Signal("ready", $"{Points}^{DeckLine()}^{Gunnery.CurrentDigest}");

            foreach (var x in bot.Until(() => bot.HasSignal("pulled"), 90, "guest inserted a card and pulled the lever")) yield return x;
            int before = int.Parse(bot.SignalValue("pulled"));
            var hs = Console.RequisitionSlot;
            bot.Note($"host slot: item {hs?.itemSlot?.CurrentItem?.name ?? "empty"}, card {hs?.CurrentCard?.CurrentDefinition?.ID ?? "none"}, lever active {hs?.lever?.isActive}, clicks executed {Controls.Instance.ClicksExecuted}");
            foreach (var x in bot.Until(() => Points < before, 20, "the host spent the points")) yield return x;
            foreach (var x in bot.Wait(6)) yield return x;   // the card's graph runs (shells, charges, ...) and the card is consumed
            bot.Note($"host after requisition: points {Points}, deck {DeckLine()}, guns {Gunnery.CurrentDigest}, charges {PowderChargeInventory.Instance?.CurrentCharges}");
            bot.Signal("after", $"{Points}^{DeckLine()}^{Gunnery.CurrentDigest}^{PowderChargeInventory.Instance?.CurrentCharges}");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> RequisitionGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("ready"), 200, "in mission, host ready")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            var r = bot.SignalValue("ready").Split('^');
            bot.Check(Points.ToString() == r[0], $"same points before ({Points} vs {r[0]})");
            foreach (var x in bot.Until(() => DeckLine() == r[1], 10, null, soft: true)) yield return x;
            bot.Check(DeckLine() == r[1], $"the guest has the host's punchcard deck ({DeckLine()} vs {r[1]}), rebuilt {Requisition.Instance.Rebuilds}x");
            var slot = Console?.RequisitionSlot;
            var card = Punchcards().FirstOrDefault(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null && c.CurrentDefinition.Cost <= Points);
            bot.Check(slot != null && card != null, $"a slot and an affordable card ({slot?.name}, {card?.CurrentDefinition?.ID})");
            if (slot == null || card == null) yield break;
            bot.Note($"guest inserts {card.CurrentDefinition.ID} (cost {card.CurrentDefinition.Cost})");
            Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);   // a drop into the slot
            foreach (var x in bot.Wait(3)) yield return x;   // the card reaches the host's slot; the console scans it
            int before = Points;
            for (int attempt = 0; attempt < 3 && Points >= before; attempt++)
            {
                foreach (var x in Press(bot, slot.lever)) yield return x;
                bot.Signal("pulled", before.ToString());
                foreach (var x in bot.Until(() => Points < before, 6, null, soft: true)) yield return x;
            }
            foreach (var x in bot.Until(() => bot.HasSignal("after"), 60, "host requisitioned")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            var a = bot.SignalValue("after").Split('^');
            bot.Note($"guest after requisition: points {Points}, deck {DeckLine()}, guns {Gunnery.CurrentDigest}, charges {PowderChargeInventory.Instance?.CurrentCharges}");
            bot.Check(Points.ToString() == a[0], $"same points after ({Points} vs {a[0]})");
            bot.Check(DeckLine() == a[1], $"same deck after ({DeckLine()} vs {a[1]})");
            bot.Check(Gunnery.CurrentDigest == a[2], $"same guns after ({Gunnery.CurrentDigest} vs {a[2]})");
            bot.Check((PowderChargeInventory.Instance?.CurrentCharges).ToString() == a[3], $"same powder charges after ({PowderChargeInventory.Instance?.CurrentCharges} vs {a[3]})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
