using HarmonyLib;
using IronNestCoop.Core;
using SleepyNodes;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Makes sure guests never run mission logic. Every graph (mission, passive, impact, punchcard, objective,
    /// per-enemy behaviour) and every event/notification entry point is skipped on a guest, so no guest ever
    /// rolls its own random spawn position, target or branch. Rolling locally is what makes enemies show up in
    /// different grid squares for different players. The host replicates the results.
    /// </summary>
    public static class GraphGate
    {
        /// <summary>True when this machine must not execute game logic itself.</summary>
        public static bool Suppress => Coop.IsGuest && (!Coop.Applying || Coop.VisualOnly) && !Mutant;

        /// <summary>QA mutation switch (-coop-mutant=nogate): guests run their own logic again, which the entity test must catch.</summary>
        static readonly bool Mutant = System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coop-mutant=nogate") >= 0;

        public static long Skipped;

        static bool Gate()
        {
            if (!Suppress) return true;
            Skipped++;
            return false;
        }

        [HarmonyPatch(typeof(StateGraph), nameof(StateGraph.Run))] static class P1 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(StateGraph), nameof(StateGraph.Update))] static class P2 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionGraph), nameof(MissionGraph.Run))] static class P3 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionGraph), nameof(MissionGraph.Update))] static class P4 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionGraph), nameof(MissionGraph.CheckEvents))] static class P5 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionGraph), nameof(MissionGraph.OnNotification))] static class P6 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionPassiveGraph), nameof(MissionPassiveGraph.Run))] static class P7 { static bool Prefix() => Gate(); }
        // MissionPassiveGraph.Update is a 7-byte forwarder to StateGraph.Update (gated above), which is too short to patch.
        [HarmonyPatch(typeof(MissionPassiveGraph), nameof(MissionPassiveGraph.CheckEvents))] static class P9 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionPassiveGraph), nameof(MissionPassiveGraph.SendNotification))] static class P10 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(MissionPassiveGraph), nameof(MissionPassiveGraph.OnMissionStart))] static class P11 { static bool Prefix() => Gate(); }
        // ImpactGraph.Run is a 3-byte empty method that the compiler folded together with 37 others, so never patch it
        // (see tools/foldcheck.py).
        [HarmonyPatch(typeof(ImpactGraph), nameof(ImpactGraph.CheckEvents))] static class P13 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(ImpactGraph), nameof(ImpactGraph.OnNotification))] static class P14 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(EntityBehaviourGraph), nameof(EntityBehaviourGraph.Run))] static class P15 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(EntityBehaviourGraph), nameof(EntityBehaviourGraph.RunLifecycle))] static class P16 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(FireMission), nameof(FireMission.ProcessEvent))] static class P17 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(FireMission), nameof(FireMission.ProcessNotification))] static class P18 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(EntityLocation), nameof(EntityLocation.EvaluateBehaviourTriggers))] static class P19 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(EntityLocation), nameof(EntityLocation.UpdateActiveBehaviour))] static class P20 { static bool Prefix() => Gate(); }
        [HarmonyPatch(typeof(EntityLocation), nameof(EntityLocation.TryActivateBehaviour))] static class P21 { static bool Prefix() => Gate(); }
        // Checkpoint saving during a session is handled in Checkpoints (nobody writes one while co-op is on).
    }
}
