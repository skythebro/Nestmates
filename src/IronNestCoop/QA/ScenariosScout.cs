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
        /// <summary>Every recon photo strip on the map: where it lies and which way round.</summary>
        static string StripsLine()
        {
            var parts = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<MapEntityRevealAlerter>(), true))
            {
                var a = o.TryCast<MapEntityRevealAlerter>();
                if (a == null || a.gameObject.scene.name == null || a.gameObject.scene.name == "") continue;
                var t = a.transform;
                var sup = a.GetComponentInParent<MapRevealEntityFilterSuppressor>(true);
                parts.Add($"{t.position.x:F2};{t.position.y:F2};{t.position.z:F2} rot {t.eulerAngles.x:F0}/{t.eulerAngles.y:F0}/{t.eulerAngles.z:F0} {(a.gameObject.activeInHierarchy ? "shown" : "hidden")} parent {t.parent?.name}");
            }
            parts.Sort();
            return parts.Count == 0 ? "none" : string.Join(" | ", parts);
        }

        /// <summary>The guest buys a scout plane; the photo strip it drops on the map lies the same way for both.</summary>
        static IEnumerable<object> ScoutHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PunchcardDefinitionV2>()))
            { var d = o.TryCast<PunchcardDefinitionV2>(); if (d != null && d.ID == "ScoutPlane" && !d.name.Contains("(Clone)") && defs.Count < 1) defs.Add(d); }
            bot.Check(defs.Count == 1, "the scout plane card exists");
            if (defs.Count > 0) ProgressionManager.Instance.UnlockPunchcards(defs.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PunchcardDefinitionV2>>());
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Console != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Until(() => Punchcards().Count > 0, 60, "punchcards dealt")) yield return x;
            if (MissionStatsTracker.Instance != null && MissionStatsTracker.Instance.RequisitionPoints < 500) MissionStatsTracker.Instance.AddRequisitionPoints(500, "qa");   // fixture: enough to buy it
            foreach (var x in bot.Wait(2)) yield return x;
            string before = StripsLine();
            bot.Signal("ready", before);
            foreach (var x in bot.Until(() => StripsLine() != before, 90, "a photo strip appeared here")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;   // the strip settles onto the map (end of frame), the reveal runs
            bot.Signal("strips", StripsLine());
            // rejoin: the guest leaves and joins again; the photos (and the units on them) must come back
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 1, 60, "guest left")) yield return x;
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 2 && WorldReady.Slots.Contains((byte)1), 120, "guest back in my mission")) yield return x;
            foreach (var x in bot.Wait(4)) yield return x;
            bot.Signal("strips2", StripsLine());
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> ScoutGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("ready"), 200, "in mission")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var slot = Console?.RequisitionSlot;
            var card = Punchcards().FirstOrDefault(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null && c.CurrentDefinition.ID == "ScoutPlane");
            bot.Check(slot != null && card != null, "a slot and the scout plane card");
            if (slot == null || card == null) { bot.Signal("verdict"); yield break; }
            Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);
            foreach (var x in bot.Wait(3)) yield return x;
            string before = StripsLine();
            for (int attempt = 0; attempt < 3 && StripsLine() == before; attempt++)
            {
                foreach (var x in RealPress(bot, slot.lever)) yield return x;
                foreach (var x in bot.Until(() => StripsLine() != before, 15, null, soft: true)) yield return x;
            }
            foreach (var x in bot.Until(() => bot.HasSignal("strips"), 90, "host's strip")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(StripsLine() != "none", "a photo strip lies on my map");
            bot.Check(StripsLine() == bot.SignalValue("strips"), $"the photo strip lies the same way for both (here {StripsLine()} / host {bot.SignalValue("strips")})");
            Coop.Leave();
            bot.Note("left; the runner joins again");
            foreach (var x in bot.Until(() => Coop.IsGuest && WorldReady.LocalReady && MissionFlow.Phase == MissionManager.GamePhase.MissionActive, 120, "back in the host's mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("strips2"), 60, "host's strips after the rejoin")) yield return x;
            foreach (var x in bot.Until(() => StripsLine() == bot.SignalValue("strips2"), 8, null, soft: true)) yield return x;
            bot.Check(StripsLine() == bot.SignalValue("strips2"), $"after joining again the photo strips are back (here {StripsLine()} / host {bot.SignalValue("strips2")})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
