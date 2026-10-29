using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using SleepyNodes;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static MissionManager.GamePhase Phase => MissionFlow.Phase;

        static (OperationGraph op, MissionGraph mission) FirstCampaignMission()
        {
            OperationGraph best = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<OperationGraph>()))
            {
                var g = o.TryCast<OperationGraph>();
                if (g == null) continue;
                CoopLog.Info($"[bot] operation '{g.OperationID}' missions={g.Missions?.Count}");
                if (best == null || g.OperationID == "Base_Campaign") best = g;
            }
            if (best == null || best.Missions == null) return (null, null);
            foreach (var n in best.Missions)
                if (n != null && n.Mission != null) { CoopLog.Info($"[bot]   mission '{n.Mission.MissionID}' scene '{n.Mission.SceneReference?.sceneName}'"); }
            foreach (var n in best.Missions)
                if (n != null && n.Mission != null) return (best, n.Mission);
            return (best, null);
        }

        /// <summary>Host starts a mission; guest follows, clocks agree, guest cannot start one itself; host returns to menu and the guest follows.</summary>
        static IEnumerable<object> MissionHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("guest-tried-start"), 60, "guest tried a local start")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var (op, mission) = FirstCampaignMission();
            bot.Check(op != null && mission != null, $"found a campaign mission ({op?.OperationID}/{mission?.MissionID})");
            if (mission == null) yield break;
            MissionManager.Instance.StartOperation(op, mission);
            foreach (var x in bot.Until(() => Phase == MissionManager.GamePhase.MissionActive && MissionClock.IsRunning, 90, "host mission running")) yield return x;
            bot.Check(Sync.Checkpoints.KeptFromDelete > 0 && !MissionSaveManager.Instance.HasCheckpoint(op.OperationID, mission.MissionID) && !MissionSaveManager.Instance.SaveCurrentMission(),
                      $"co-op leaves checkpoints alone: start kept it ({Sync.Checkpoints.KeptFromDelete}), none offered, none written");
            foreach (var x in bot.Until(() => bot.SignalValue("in-mission") == mission.MissionID, 120, "guest reports same mission")) yield return x;
            foreach (var x in bot.Wait(8)) yield return x;
            foreach (var x in bot.Shot("host-in-mission")) yield return x;
            bot.Signal("check-clock");
            foreach (var x in bot.Until(() => bot.HasSignal("clock-ok"), 30, "guest clock verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            foreach (var x in bot.Until(() => bot.HasSignal("back-in-menu"), 90, "guest back in menu")) yield return x;
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> MissionGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => MissionManager.Instance != null && Phase == MissionManager.GamePhase.MainMenu, 60, "main menu ready")) yield return x;
            // Guard check: a guest's own mission start must be refused.
            var (op, mission) = FirstCampaignMission();
            if (mission != null) MissionManager.Instance.StartOperation(op, mission);
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Check(Phase == MissionManager.GamePhase.MainMenu, $"guest's own mission start was blocked (phase {Phase})");
            bot.Signal("guest-tried-start");
            foreach (var x in bot.Until(() => Phase == MissionManager.GamePhase.MissionActive && MissionClock.IsRunning, 120, "followed host into mission")) yield return x;
            bot.Check(Sync.Checkpoints.KeptFromDelete > 0, $"following the host kept this player's own checkpoint ({Sync.Checkpoints.KeptFromDelete} kept)");
            bot.Check(MissionFlow.Instance.MissionId == MissionManager.Instance.CurrentMission?.MissionID,
                $"running the host's mission ({MissionManager.Instance.CurrentMission?.MissionID} vs {MissionFlow.Instance.MissionId})");
            bot.Signal("in-mission", MissionManager.Instance.CurrentMission?.MissionID);
            foreach (var x in bot.Until(() => bot.HasSignal("check-clock"), 120, "clock check request")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            double err = MissionFlow.Instance.LastClockError;
            bot.Check(Math.Abs(err) < 0.06, $"mission clock within 60 ms of host (last error {err * 1000:F0} ms)");
            foreach (var x in bot.Shot("guest-in-mission")) yield return x;
            bot.Signal("clock-ok");
            foreach (var x in bot.Until(() => Phase == MissionManager.GamePhase.MainMenu, 90, "followed host back to menu")) yield return x;
            bot.Signal("back-in-menu");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 30, "host done")) yield return x;
        }
    }
}
