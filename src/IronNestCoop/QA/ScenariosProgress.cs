using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// Campaign progress: the guest owns a punchcard the host also owns, with a different number of uses left. The
    /// host redeems it once and finishes the mission. The guest's own profile must then record the mission as
    /// completed and keep the guest's own uses minus the one spent (not the host's count).
    /// </summary>
    public static partial class Scenarios
    {
        const string ProgressOp = "Base_Campaign";

        static PunchcardDefinitionV2 FixtureCard()
        {
            PunchcardDefinitionV2 best = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<PunchcardDefinitionV2>()))
            {
                var d = o.TryCast<PunchcardDefinitionV2>();
                if (d == null || string.IsNullOrEmpty(d.ID) || d.Cost <= 0 || d.Cost >= 2000 || d.ID.Contains("Demo") || d.name.Contains("(Clone)") || d.MaxUses <= 0) continue;
                if (best == null || string.CompareOrdinal(d.ID, best.ID) < 0) best = d;   // same pick on both machines
            }
            return best;
        }

        static UserProgression.UserCardState CardState(string id)
        {
            var states = ProgressionManager.Instance?.UserProgression?.CardStates;
            return states != null && states.TryGetValue(id, out var s) ? s : null;
        }

        static void OwnCard(PunchcardDefinitionV2 def, int uses)
        {
            var pm = ProgressionManager.Instance;
            var one = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            one.Add(def);
            pm.UnlockPunchcards(one.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PunchcardDefinitionV2>>());
            var st = CardState(def.ID);
            if (st == null) { st = new UserProgression.UserCardState { CardID = def.ID }; pm.UserProgression.CardStates[def.ID] = st; }
            st.RemainingUses = uses;
        }

        static bool MissionCompleted(string missionId)
        {
            var op = ProgressionManager.Instance?.GetOperation(ProgressOp);
            return op?.MissionStates != null && op.MissionStates.TryGetValue(missionId, out var m) && m != null && m.Completed;
        }

        /// <summary>Uses left of a card in the deck; a used-up card leaves the deck (0).</summary>
        static int DeckUses(string id) =>
            Punchcards().Where(c => c.CurrentDefinition?.ID == id).Select(c => c.CurrentDefinition.RemainingUses).DefaultIfEmpty(0).First();

        static IEnumerable<object> ProgressHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var def = FixtureCard();
            bot.Check(def != null, "a limited-use punchcard exists");
            if (def == null) yield break;
            OwnCard(def, def.MaxUses);
            bot.Note($"host owns {def.ID} with {def.MaxUses} uses (max {def.MaxUses}, cost {def.Cost})");
            foreach (var x in bot.Until(() => bot.HasSignal("owned"), 60, "guest owns the card")) yield return x;

            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            string missionId = MissionManager.Instance.CurrentMission?.MissionID;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Console != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Until(() => Punchcards().Any(c => c.CurrentDefinition?.ID == def.ID), 60, "the card is in the deck")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            int usesBefore = DeckUses(def.ID);

            var card = Punchcards().First(c => c.CurrentDefinition?.ID == def.ID);
            var slot = Console.RequisitionSlot;
            if (Points < def.Cost + 100) MissionStatsTracker.Instance.AddRequisitionPoints(def.Cost + 100 - Points, "qa");
            Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);
            foreach (var x in bot.Wait(2)) yield return x;
            int points = Points;
            for (int attempt = 0; attempt < 3 && Points >= points; attempt++)
            {
                foreach (var x in Press(bot, slot.lever)) yield return x;
                foreach (var x in bot.Until(() => Points < points, 6, null, soft: true)) yield return x;
            }
            bot.Check(Points < points, $"the host redeemed {def.ID} ({points} -> {Points} points)");
            foreach (var x in bot.Wait(6)) yield return x;   // the card's effects run and the card is consumed
            int usesAfter = DeckUses(def.ID);
            bot.Note($"host deck uses of {def.ID}: {usesBefore} -> {usesAfter}");
            foreach (var x in bot.Wait(2)) yield return x;   // the deck change reaches the guest (1 s poll)

            bot.Signal("finishing", $"{missionId}^{usesBefore - usesAfter}");
            MissionManager.Instance.MarkMissionComplete(true);
            foreach (var x in bot.Wait(1)) yield return x;
            MissionManager.Instance.FinishMission();
            foreach (var x in bot.Until(() => MissionFlow.Phase == MissionManager.GamePhase.BrowsingMap, 60, "host back on the map")) yield return x;
            bot.Check(MissionCompleted(missionId), $"host saved {missionId} as completed");
            bot.Note($"host's own uses of {def.ID} now {CardState(def.ID)?.RemainingUses}");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 90, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> ProgressGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var def = FixtureCard();
            bot.Check(def != null, "a limited-use punchcard exists");
            if (def == null) yield break;
            int mine = def.MaxUses + 5;   // a count the host cannot have
            OwnCard(def, mine);
            bot.Note($"guest owns {def.ID} with {mine} uses");
            bot.Signal("owned");

            foreach (var x in bot.Until(() => bot.HasSignal("finishing"), 400, "host finishing the mission")) yield return x;
            var f = bot.SignalValue("finishing").Split('^');
            string missionId = f[0];
            int spent = int.Parse(f[1]);
            bot.Check(!MissionCompleted(missionId), $"{missionId} not yet completed in the guest's profile");
            foreach (var x in bot.Until(() => MissionFlow.Phase == MissionManager.GamePhase.BrowsingMap, 60, "guest followed the host to the map")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(MissionCompleted(missionId), $"the guest's own profile saved {missionId} as completed");
            int now = CardState(def.ID)?.RemainingUses ?? -99;
            bot.Note($"guest uses of {def.ID}: {mine} -> {now} (spent in the mission {spent}; tracked {(Requisition.Spent.TryGetValue(def.ID, out var t) ? t : -1)})");
            bot.Check(spent > 0, $"the mission spent a use ({spent})");
            bot.Check(now == mine - spent, $"the guest keeps its own uses minus the spent ones ({now}, expected {mine - spent})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
