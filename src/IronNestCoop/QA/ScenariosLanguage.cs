using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>The guest plays in another language: teleprinter text arrives in the guest's language.</summary>
    public static partial class Scenarios
    {
        static string PrimaryTeleText()
        {
            var tp = Teleprinter.GetTeleprinter(Teleprinter.Teleprinters.Primary);
            return tp?.CaptureMissionState()?.CurrentFullRich ?? "";
        }

        static IEnumerable<object> LanguageHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("lang"), 60, "guest switched language")) yield return x;
            bot.Note($"host language {Localisation.LocalisationManager.Instance?.CurrentLanguage}, guest {bot.SignalValue("lang")}");
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Until(() => PrimaryTeleText().Length > 50, 90, "the teleprinter printed")) yield return x;
            foreach (var x in bot.Until(TeleprintersIdle, 120, "host teleprinters idle")) yield return x;
            string t = PrimaryTeleText();
            bot.Note($"host teleprinter ({t.Length} chars): {t.Substring(0, Mathf.Min(160, t.Length)).Replace('\n', ' ')}");
            bot.Signal("tele", t.Length + "^" + Hash(t));
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 90, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> LanguageGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest && Localisation.LocalisationManager.Instance != null && Localisation.LocalisationManager.Instance.IsReady, 90, "connected, localisation ready")) yield return x;
            var lm = Localisation.LocalisationManager.Instance;
            var langs = lm.LangData?.SupportedLanguages;
            string other = null;
            if (langs != null) foreach (var l in langs) if (l != null && l.Lang != lm.CurrentLanguage && other == null) other = l.Lang;
            bot.Note($"guest language {lm.CurrentLanguage}; supported: {(langs == null ? "-" : string.Join(",", Enumerable.Range(0, langs.Count).Select(i => langs[i].Lang)))}");
            bot.Check(other != null, "another language exists");
            if (other == null) yield break;
            string original = lm.CurrentLanguage;
            lm.SwitchLanguage(other, false);   // not saved: the test profile keeps its setting
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Signal("lang", lm.CurrentLanguage);
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("tele"), 240, "host teleprinter done")) yield return x;
            foreach (var x in bot.Until(TeleprintersIdle, 120, "guest teleprinters idle")) yield return x;
            string t = PrimaryTeleText();
            bot.Note($"guest teleprinter ({t.Length} chars, {lm.CurrentLanguage}): {t.Substring(0, Mathf.Min(160, t.Length)).Replace('\n', ' ')}");
            var h = bot.SignalValue("tele").Split('^');
            bot.Check(WorldEvents.Rerendered > 0, $"teleprinter text was rendered in the guest's language ({WorldEvents.Rerendered} blocks)");
            bot.Check(Hash(t) != h[1], "the guest's teleprinter text differs from the host's (other language)");
            bot.Check(t.Length > 50 && !t.Contains("{") && !t.Contains("}"), "no unresolved placeholders in the guest's text");
            foreach (var x in bot.Shot("guest-teleprinter-language")) yield return x;
            lm.SwitchLanguage(original, false);
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
