using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>
        /// Rejoin test: the guest leaves in the middle of a mission (its copy of the mission keeps running,
        /// offline) and joins again in the same game launch. It must end up back in the host's mission, loaded and
        /// ready, not left at the main menu. (The QA runner joins again by itself once the guest has left.)
        /// </summary>
        static IEnumerable<object> RejoinHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            bot.Signal("inmission", MissionFlow.Instance.Epoch.ToString());
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 1, 60, "guest left")) yield return x;
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 2, 60, "guest joined again")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "the guest loaded my mission again")) yield return x;
            bot.Check(WorldReady.Slots.Contains((byte)1), "the guest is back in the mission and ready");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> RejoinGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("inmission") && WorldReady.LocalReady, 240, "in the host's mission")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            Coop.Leave();
            bot.Note($"left in the middle of the mission (phase {MissionFlow.Phase})");
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Check(MissionFlow.Phase == MissionManager.GamePhase.MissionActive, $"after leaving, my own copy of the mission runs on (phase {MissionFlow.Phase})");
            foreach (var x in bot.Until(() => Coop.IsGuest, 60, "joined again")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && MissionFlow.Phase == MissionManager.GamePhase.MissionActive, 120, "back in the host's mission")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Check(WorldReady.LocalReady && MissionFlow.Phase == MissionManager.GamePhase.MissionActive,
                $"after joining again I am in the host's mission, loaded (phase {MissionFlow.Phase}, ready {WorldReady.LocalReady}, epoch {MissionFlow.Instance.Epoch})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
