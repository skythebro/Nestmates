using HarmonyLib;
using IronNestCoop.Core;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Keeps mission checkpoints out of co-op, and keeps them safe. A checkpoint resumes one player's mission
    /// half-way, but the others would start it from scratch, which would leave players in two different missions.
    /// Also, starting a mission deletes its checkpoint (MissionManager.StartOperation), so a guest following the
    /// host would lose their own solo checkpoint of that mission. So while a session is open, no checkpoint is
    /// offered (no icon, no resume prompt), none is deleted and none is written. After leaving the session, every
    /// player's checkpoints are exactly as they were.
    /// </summary>
    public static class Checkpoints
    {
        public static long Hidden, KeptFromDelete, SavesSkipped, SaveExits;

        /// <summary>True when the save comes from this player's Esc menu "Save & Exit" button (the menu is open and that
        /// button was the one pressed), and not from anything automatic.</summary>
        static bool FromSaveButton()
        {
            if (!NoPause.LocalMenuOpen) return false;
            if (TestSaveButton) return true;
            // On the host the menu being open is enough (at worst there is an extra save of their own game). A guest
            // leaves the game on this save, so it only counts when the button itself was pressed (the selected control).
            if (Coop.IsHost) return true;
            var es = UnityEngine.EventSystems.EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            return sel != null && sel.name == "Save Mission";
        }
        internal static bool TestSaveButton;   // for the tests: acts as if the button had been pressed

        [HarmonyPatch(typeof(MissionSaveManager), nameof(MissionSaveManager.HasCheckpoint))]
        static class Has
        {
            static bool Prefix(ref bool __result)
            {
                try
                {
                    if (!Coop.InSession) return true;
                    Hidden++;
                    __result = false;
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Checkpoints.Prefix line 21", e); return true; }
            }
        }

        [HarmonyPatch(typeof(MissionSaveManager), nameof(MissionSaveManager.DeleteCheckpoint))]
        static class Delete
        {
            static bool Prefix(string operationId, string missionId)
            {
                try
                {
                    if (!Coop.InSession) return true;
                    KeptFromDelete++;
                    CoopLog.Info($"[checkpoints] co-op: kept the checkpoint of {operationId}/{missionId} (not deleted)");
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Checkpoints.Prefix line 33", e); return true; }
            }
        }

        [HarmonyPatch(typeof(MissionSaveManager), nameof(MissionSaveManager.SaveCurrentMission))]
        static class Save
        {
            static bool Prefix(ref bool __result)
            {
                try
                {
                    if (!Coop.InSession) return true;
                    // The Esc menu's "Save & Exit": the game only exits after a save that worked, so refusing the save used
                    // to keep the player stuck in the mission (they had to close the lobby first). Now the host saves their
                    // game and everyone goes back to the menu with them. A guest leaves the co-op game and exits without
                    // saving, because the save would be the host's mission in the guest's profile.
                    if (FromSaveButton() && !LaunchArgs.Mutant("nosaveexit"))
                    {
                        SaveExits++;
                        if (Coop.IsHost) { CoopLog.Info("[checkpoints] the host saves and exits: the mission is saved, everyone returns to the menu"); return true; }
                        CoopLog.Info("[checkpoints] a guest pressed Save & Exit: leaving the co-op game (nothing saved: it is the host's mission)");
                        Coop.Leave();
                        UI.Notify.Show("You left the co-op game. Only the host saves a shared mission.");
                        __result = true;   // the game goes on to exit the mission, now for this player alone
                        return false;
                    }
                    SavesSkipped++;
                    __result = false;
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Checkpoints.Prefix line 45", e); return true; }
            }
        }
    }
}
