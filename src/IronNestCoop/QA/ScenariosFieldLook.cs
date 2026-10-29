using System.Collections.Generic;
using IronNestCoop.Core;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>One copy, not in a game: the Play tab with the lobby-code field focused (players saw the focused
        /// field stretched and blurry). A picture of it, typed into, for review.</summary>
        static IEnumerable<object> FieldLook(Bot bot)
        {
            foreach (var x in bot.Until(() => Sync.MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            UI.CoopUI.Show(0);
            foreach (var x in bot.Wait(0.5)) yield return x;
            foreach (var x in bot.Shot("field-idle")) yield return x;
            UI.L.TestFocus = "code";
            foreach (var x in bot.Wait(0.6)) yield return x;
            bot.Check(UI.L.AnyFieldFocused, "the code field took the focus");
            foreach (var x in bot.Shot("field-focused")) yield return x;
        }
    }
}
