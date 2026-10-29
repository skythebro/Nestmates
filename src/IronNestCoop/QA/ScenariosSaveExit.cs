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
        /// <summary>
        /// The Esc menu's "Save &amp; Exit" in co-op (it did nothing while in a lobby; the game exits only
        /// after a save that worked, and co-op refused every save). A guest leaves the co-op game and exits without
        /// saving; the host saves and everyone goes back to the menu.
        /// </summary>
        static IEnumerable<object> SaveExitHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Signal("guestsave");
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 1, 30, "the guest left with Save & Exit")) yield return x;
            bot.Check(MissionFlow.Phase == MissionManager.GamePhase.MissionActive, "the host's mission goes on when a guest saves and exits");
            // now the host: the save is made, and the game leaves the mission
            var menu = EscMenu();
            menu?.ForceOpen(true);
            foreach (var x in bot.Wait(1.2)) yield return x;
            Checkpoints.TestSaveButton = true;
            bool saved = false;
            try { saved = MissionSaveManager.Instance.SaveCurrentMission(); }
            finally { Checkpoints.TestSaveButton = false; }
            bot.Check(saved, "the host's Save & Exit saves the mission in co-op (it used to be refused, so the game never exited)");
            bot.Check(Coop.InSession, "the host stays the host (the lobby stays open)");
            menu?.ForceClose(true);
            MissionManager.Instance.EndOperationAndReturnToMenu();
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("done");
        }

        static IEnumerable<object> SaveExitGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "in mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("guestsave"), 120, "host asks to save and exit")) yield return x;
            var menu = EscMenu();
            menu?.ForceOpen(true);
            foreach (var x in bot.Wait(1.2)) yield return x;
            bot.Check(NoPause.LocalMenuOpen, "the Esc menu is open");
            // an automatic save while the menu is open (no button pressed) must not throw a guest out of the game
            bool auto = MissionSaveManager.Instance.SaveCurrentMission();
            bot.Check(!auto && Coop.InSession, "a save the guest did not ask for is refused and the guest stays in the game");
            Checkpoints.TestSaveButton = true;
            bool ok;
            try { ok = MissionSaveManager.Instance.SaveCurrentMission(); }
            finally { Checkpoints.TestSaveButton = false; }
            bot.Check(ok && !Coop.InSession, $"Save & Exit on a guest: it leaves the co-op game and the game may exit (result {ok}, in session {Coop.InSession})");
            menu?.ForceClose(true);
            MissionManager.Instance.EndOperationAndReturnToMenu();   // what the game does next after a save that worked
            foreach (var x in bot.Until(() => MissionFlow.Phase != MissionManager.GamePhase.MissionActive, 20, "the guest left the mission (not blocked: it is alone now)")) yield return x;
            bot.Signal("guestdone");
            foreach (var x in bot.Wait(2)) yield return x;
        }
    }
}
