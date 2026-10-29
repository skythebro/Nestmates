using System.Collections.Generic;
using IronNestCoop.Sync;
using IronNestCoop.UI;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// The co-op window needs the system cursor: open it in the main menu and in a mission, the cursor must be
    /// visible and unlocked (the game hides it for its own virtual cursor); closed in a mission, the game's own
    /// cursor state must come back.
    /// </summary>
    public static partial class Scenarios
    {
        static string CursorState() => $"visible {Cursor.visible}, lock {Cursor.lockState}";

        static IEnumerable<object> CursorCheck(Bot bot, string where)
        {
            if (!CoopUI.IsOpen) CoopUI.Toggle();
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(Cursor.visible && Cursor.lockState == CursorLockMode.None, $"{where}: window open -> system cursor free ({CursorState()})");
        }

        /// <summary>A press where a tab button is drawn must switch to that tab (the window's own click path).</summary>
        static IEnumerable<object> ClickTab(Bot bot, int tab, string where)
        {
            var r = CoopUI.TabScreenRects[tab];
            Pointer.TestPress = r.center;
            foreach (var x in bot.Wait(0.5)) yield return x;
            bot.Check(CoopUI.TabIndex == tab, $"{where}: a click on tab {tab} at {r.center} opens it (open tab {CoopUI.TabIndex})");
        }

        static IEnumerable<object> CursorScenario(Bot bot)
        {
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            foreach (var x in CursorCheck(bot, "main menu")) yield return x;
            foreach (var x in ClickTab(bot, 2, "main menu")) yield return x;
            foreach (var x in ClickTab(bot, 0, "main menu")) yield return x;
            if (bot.IsHostRole)
            {
                CoopUI.Toggle();
                foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            }
            else
            {
                foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "followed host into mission")) yield return x;
            }
            foreach (var x in bot.Wait(3)) yield return x;
            if (CoopUI.IsOpen) CoopUI.Toggle();
            foreach (var x in bot.Wait(2)) yield return x;
            string closed = CursorState();
            bot.Note("mission, window closed: " + closed);
            foreach (var x in CursorCheck(bot, "mission")) yield return x;
            foreach (var x in ClickTab(bot, 2, "mission")) yield return x;
            foreach (var x in ClickTab(bot, 0, "mission")) yield return x;
            CoopUI.Toggle();
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(CursorState() == closed, $"mission: window closed again -> the game's cursor state is back ({CursorState()} vs {closed})");
            bot.Signal(bot.IsHostRole ? "hostdone" : "guestdone");
            foreach (var x in bot.Until(() => bot.HasSignal(bot.IsHostRole ? "guestdone" : "hostdone"), 120, "other side done")) yield return x;
            if (bot.IsHostRole) MissionManager.Instance.EndOperationAndReturnToMenu();
            foreach (var x in bot.Wait(1)) yield return x;
        }
    }
}
