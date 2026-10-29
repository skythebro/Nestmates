using HarmonyLib;

namespace IronNestCoop
{
    /// <summary>
    /// Test copies run under a real Steam account and the game's online leaderboard profile. Bots finish
    /// missions with made-up points, so in a sandbox nothing may reach those: no achievements or stats, no
    /// leaderboard runs and no operation-state uploads. Inactive in a normal launch.
    /// </summary>
    static class SandboxOnline
    {
        static bool Block(string what)
        {
            if (!Sandbox.Active && !Dev.DevTools.Used) return false;   // true for test copies, or for a game run where dev shortcuts were used
            CoopLog.Debug($"[sandbox] blocked {what}");
            return true;
        }

        [HarmonyPatch(typeof(AchievementsService), nameof(AchievementsService.TryUnlockAchievement))]
        static class Unlock { static bool Prefix() => !Block("achievement"); }

        [HarmonyPatch(typeof(AchievementsService), nameof(AchievementsService.UpdateStat))]
        static class UpdateStat { static bool Prefix() => !Block("stat update"); }

        [HarmonyPatch(typeof(AchievementsService), nameof(AchievementsService.SetStat))]
        static class SetStat { static bool Prefix() => !Block("stat set"); }

        [HarmonyPatch(typeof(AchievementsManager), nameof(AchievementsManager.EventManager_OnAchievementUnlocked))]
        static class UnlockEvent { static bool Prefix() => !Block("achievement event"); }

        [HarmonyPatch(typeof(LeaderboardManager), nameof(LeaderboardManager.Leaderboard_StartRun))]
        static class StartRun { static bool Prefix() => !Block("leaderboard run start"); }

        [HarmonyPatch(typeof(LeaderboardManager), nameof(LeaderboardManager.Leaderboard_CompleteRun))]
        static class CompleteRun
        {
            static bool Prefix(Il2CppSystem.Action onCompleted)
            {
                if (!Block("leaderboard run upload")) return true;
                onCompleted?.Invoke();   // the game carries on as if the upload had finished
                return false;
            }
        }

        [HarmonyPatch(typeof(LeaderboardManager), nameof(LeaderboardManager.PushOperationState))]
        static class PushState { static bool Prefix() => !Block("operation state upload"); }
    }
}
