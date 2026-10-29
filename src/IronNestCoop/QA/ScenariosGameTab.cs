using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using Kamgam.UGUIComponentsForSettings;
using UnityEngine;
using UnityEngine.UI;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>
        /// One copy: the Multiplayer tab in the game's Settings (Esc menu). Opens Esc -> Settings -> Multiplayer,
        /// takes screenshots, flips a toggle and steps an option the way a player would, and checks the mod's settings
        /// followed; then puts them back.
        /// </summary>
        static string R(RectTransform rt)
        {
            if (rt == null) return "-";
            var c = new Vector3[4]; rt.GetWorldCorners(c);
            return $"w{rt.rect.width:F0} h{rt.rect.height:F0} x[{c[0].x:F0}..{c[2].x:F0}] y[{c[0].y:F0}..{c[2].y:F0}]";
        }

        /// <summary>Sizes of the tab row, the button row and the page's scroll setup (UI layout bugs).</summary>
        static string LayoutDump(Transform settings, Transform page)
        {
            var sb = new System.Text.StringBuilder();
            var tabs = settings.Find("TabsCtn");
            var hlg = tabs?.GetComponent<HorizontalLayoutGroup>();
            sb.Append($"| TABROW {R(tabs?.GetComponent<RectTransform>())} spacing {hlg?.spacing} pad {hlg?.padding.left}/{hlg?.padding.right} childCtrlW {hlg?.childControlWidth} forceExpW {hlg?.childForceExpandWidth}");
            for (int i = 0; tabs != null && i < tabs.childCount; i++)
            {
                var t = tabs.GetChild(i);
                sb.Append($" | TAB {t.name} {R(t.GetComponent<RectTransform>())} active {t.gameObject.activeSelf}");
                foreach (var tx in t.GetComponentsInChildren<TMPro.TMP_Text>(true))
                    sb.Append($" [text '{tx.text}' {R(tx.rectTransform)} pref {tx.preferredWidth:F0} size {tx.fontSize:F1} auto {tx.enableAutoSizing} {tx.fontSizeMin:F1}-{tx.fontSizeMax:F1} overflow {tx.overflowMode} align {tx.alignment} wrap {tx.enableWordWrapping} on {tx.gameObject.name}]");
                foreach (var le in t.GetComponentsInChildren<LayoutElement>(true)) sb.Append($" [LE {le.gameObject.name} pw {le.preferredWidth} mw {le.minWidth} fw {le.flexibleWidth}]");
            }
            var row = page.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "ButtonRow (co-op)");
            if (row != null)
            {
                sb.Append($" | BUTTONROW {R(row.GetComponent<RectTransform>())}");
                foreach (var t in row.GetComponentsInChildren<RectTransform>(true)) sb.Append($" [{t.name} {R(t)} active {t.gameObject.activeSelf}]");
                foreach (var tx in row.GetComponentsInChildren<TMPro.TMP_Text>(true)) sb.Append($" [text pref {tx.preferredWidth:F0} size {tx.fontSize:F1} auto {tx.enableAutoSizing} overflow {tx.overflowMode}]");
            }
            foreach (var sr in settings.GetComponentsInChildren<ScrollRect>(true))
            {
                var content = sr.content;
                sb.Append($" | SCROLL {Util.ScenePath.Of(sr.transform).Split('/').Last()} active {sr.gameObject.activeInHierarchy} movement {sr.movementType} vert {sr.vertical} viewport {R(sr.viewport)} content {content?.name} {R(content)}");
                if (content != null)
                {
                    sb.Append($" prefH {LayoutUtility.GetPreferredHeight(content):F0} fitter {content.GetComponent<ContentSizeFitter>()?.verticalFit.ToString() ?? "none"} vlg {(content.GetComponent<VerticalLayoutGroup>() != null)}");
                    for (int i = 0; i < content.childCount; i++) { var c = content.GetChild(i); sb.Append($" [{c.name} {R(c.GetComponent<RectTransform>())} active {c.gameObject.activeSelf} fitter {c.GetComponent<ContentSizeFitter>()?.verticalFit.ToString() ?? "none"} prefH {LayoutUtility.GetPreferredHeight(c.GetComponent<RectTransform>()):F0}]"); }
                }
            }
            return sb.ToString();
        }

        static IEnumerable<object> GameTab(Bot bot)
        {
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => Computer != null, 120, "mission ready")) yield return x;
            foreach (var x in bot.Wait(6)) yield return x;
            foreach (var x in bot.Until(() => UI.GameSettingsTab.Built > 0, 20, "a Multiplayer tab was built")) yield return x;
            bot.Note($"Multiplayer tabs built: {UI.GameSettingsTab.Built}");

            var menu = EscMenu();
            menu?.ForceOpen(true);
            foreach (var x in bot.Wait(1.5)) yield return x;
            Transform settings = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TabButtonUGUI>(), true))
            {
                var t = o.TryCast<TabButtonUGUI>();
                if (t != null && t.name == "TabButtonUGUI (Multiplayer)" && Util.ScenePath.Of(t.transform).Contains("Options Menu Parent")) settings = t.transform.parent.parent;
            }
            bot.Check(settings != null, "the Esc menu's Settings has a Multiplayer tab");
            if (settings == null) yield break;
            // what the Settings button does: hide the Esc buttons, show Settings
            Transform escButtons = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Transform>(), true))
            { var t = o.TryCast<Transform>(); if (t != null && t.name == "ESC Menu Buttons" && Util.ScenePath.Of(t).Contains("Options Menu Parent")) escButtons = t; }
            escButtons?.gameObject.SetActive(false);
            settings.gameObject.SetActive(true);
            foreach (var x in bot.Wait(1)) yield return x;
            foreach (var x in bot.Shot("settings-game")) yield return x;
            var tab = settings.Find("TabsCtn/TabButtonUGUI (Multiplayer)");
            tab.GetComponent<Button>().onClick.Invoke();
            foreach (var x in bot.Wait(1)) yield return x;
            var page = settings.Find("ContentCtn/Content (Multiplayer)");
            bot.Check(page != null && page.gameObject.activeInHierarchy, "clicking the tab shows the Multiplayer page");
            bot.Check(!settings.Find("ContentCtn/Content (Game)").gameObject.activeInHierarchy && !settings.Find("ContentCtn/Content (Audio)").gameObject.activeInHierarchy, "the other pages are hidden");
            bot.Check(UI.GameSettingsTab.PageScroll() > 0.99f, $"the page opens at its first row (scroll {UI.GameSettingsTab.PageScroll():F2}, 1 = top)");
            float margin = UI.GameSettingsTab.FirstRowMargin();
            bot.Check(margin >= -1f && margin < 80f, $"the first row (the Multiplayer headline) shows at the top of the page, not cut off above it ({margin:F0} units below the top)");
            foreach (var h in new[] { "Your player", "On screen", "Privacy & status", "Host rules" })
                bot.Check(page.GetComponentsInChildren<TMPro.TMP_Text>(true).Any(t => t.text == h), $"the page has a \"{h}\" header");
            foreach (var x in bot.Shot("settings-multiplayer")) yield return x;

            // a toggle, the way a click does
            var skip = page.GetComponentsInChildren<ToggleUGUI>(true).FirstOrDefault(t => t.name.Contains("Skip the game's intro"));
            bot.Check(skip != null, "the skip intro row is there");
            bool was = Settings.SkipIntro;
            if (skip != null)
            {
                bot.Check(skip.Value == was, $"the row shows the setting ({skip.Value} / {was})");
                skip.Toggle.isOn = !skip.Toggle.isOn;
                foreach (var x in bot.Wait(0.5)) yield return x;
                bot.Check(Settings.SkipIntro == !was, $"clicking it changed the setting ({Settings.SkipIntro})");
                Settings.SkipIntro = was;
                foreach (var x in bot.Wait(0.5)) yield return x;
                bot.Check(skip.Value == was, "and the row follows a setting changed elsewhere");
            }
            bot.Note("LAYOUT " + LayoutDump(settings, page.transform));
            // players saw: the tab label running into "Controls", the menu button's text spilling out of its
            // frame, and the page springing back to the top when scrolled
            var tabRow = settings.Find("TabsCtn");
            float lastRight = float.MinValue;
            for (int i = 0; tabRow != null && i < tabRow.childCount; i++)
            {
                var tabRt = tabRow.GetChild(i).GetComponent<RectTransform>();
                var label = tabRt.GetComponentsInChildren<TMPro.TMP_Text>(false).FirstOrDefault();
                // siblings in the row: compared in the row's own coordinates (GetWorldCorners comes back unfilled)
                float left = tabRt.localPosition.x + tabRt.rect.xMin, right = tabRt.localPosition.x + tabRt.rect.xMax;
                float scale = tabRt.lossyScale.x;
                float glyphs = label == null ? 0 : label.textBounds.size.x * label.rectTransform.lossyScale.x / scale;
                bot.Check(glyphs <= tabRt.rect.width + 1, $"tab '{label?.text}' text fits its tab ({glyphs:F0} of {tabRt.rect.width:F0})");
                bot.Check(left >= lastRight - 1, $"tab '{label?.text}' does not overlap the one before it ({left:F0} vs {lastRight:F0})");
                lastRight = right;
            }
            var menuRow = page.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == "ButtonRow (co-op)");
            var frame = menuRow?.GetComponentInChildren<Button>(true)?.GetComponent<RectTransform>();
            var menuText = menuRow?.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (frame != null && menuText != null)
            {
                float glyphs = menuText.textBounds.size.x * menuText.rectTransform.lossyScale.x / frame.lossyScale.x;
                bot.Check(glyphs <= frame.rect.width - 10, $"the menu button's text '{menuText.text}' fits its frame ({glyphs:F0} of {frame.rect.width:F0})");
            }
            var scroll = page.GetComponentInChildren<ScrollRect>(true);
            if (scroll != null)
            {
                // the content must reach past its last row, or that row can't be scrolled to (the window's rect also
                // covers the area behind the Reset/Close/Apply buttons, so content vs window says nothing)
                var rowsRt = page.GetComponentsInChildren<VerticalLayoutGroup>(true).FirstOrDefault()?.GetComponent<RectTransform>();
                if (rowsRt != null)
                {
                    float rowsBottom = rowsRt.localPosition.y + rowsRt.rect.yMin;
                    // scrolled to the bottom, the content's bottom is the window's: the last row must sit clear of the
                    // Reset/Close/Apply strip that covers the window's lowest ~110 px
                    bot.Check(rowsBottom - scroll.content.rect.yMin >= 120, $"scrolled down, the last row clears the bottom buttons (rows end {rowsBottom - scroll.content.rect.yMin:F0} px above the content's end)");
                    bot.Check(scroll.content.rect.height > scroll.viewport.rect.height + 40, $"the page is taller than its scroll window, so it scrolls (content {scroll.content.rect.height:F0}, window {scroll.viewport.rect.height:F0})");
                }
                scroll.verticalNormalizedPosition = 0f;   // to the bottom, as a long wheel scroll does
                var trace = new System.Text.StringBuilder();
                float traceUntil = Time.unscaledTime + 1.5f;
                while (Time.unscaledTime < traceUntil)
                {
                    var sel = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject;
                    trace.Append($"{scroll.verticalNormalizedPosition:F2}:{(sel == null ? "-" : sel.name)} ");
                    yield return null;
                }
                bot.Note("scroll trace " + trace);
                bot.Check(!trace.ToString().Contains("1,00") && !trace.ToString().Contains("1.00"), "scrolled to the bottom, it never springs back to the top (trace above)");
                bot.Check(scroll.verticalNormalizedPosition < 0.05f, $"scrolled to the bottom it stays there (position {scroll.verticalNormalizedPosition:F2}, 0 = bottom)");
                foreach (var x in bot.Shot("settings-multiplayer-bottom")) yield return x;
                // another tab and back: the page starts at the top again
                var gameTab = settings.Find("TabsCtn/TabButtonUGUI (Game)");
                if (gameTab != null)
                {
                    gameTab.GetComponent<Button>().onClick.Invoke();
                    foreach (var x in bot.Wait(0.5)) yield return x;
                    tab.GetComponent<Button>().onClick.Invoke();
                    foreach (var x in bot.Wait(1)) yield return x;
                    bot.Check(UI.GameSettingsTab.PageScroll() > 0.99f && UI.GameSettingsTab.FirstRowMargin() >= -1f, $"opened again, the page is back at its first row (scroll {UI.GameSettingsTab.PageScroll():F2}, first row {UI.GameSettingsTab.FirstRowMargin():F0} below the top)");
                }
                else bot.Note("no Game tab found to switch away");
                scroll.verticalNormalizedPosition = 1f;
                foreach (var x in bot.Wait(0.3)) yield return x;
            }
            // an options button, the way its arrow does
            var timer = page.GetComponentsInChildren<OptionsButtonUGUI>(true).FirstOrDefault(o => o.name.Contains("Counter-battery timer"));
            bot.Check(timer != null, "the counter-battery timer row is there");
            int pct = Settings.CounterBatteryTimerPercent;
            if (timer != null)
            {
                bot.Note("timer row shows " + timer.TextTf?.text);
                timer.Next();
                foreach (var x in bot.Wait(0.5)) yield return x;
                // host rules are the host's: a guest's arrow must leave them, and the row keeps showing the host's
                if (Coop.IsGuest) bot.Check(Settings.CounterBatteryTimerPercent == pct && timer.TextTf?.text == IronNestCoop.UI.CoopUI.TimerName(HostRules.TimerPercent),
                    $"as a guest its arrow leaves the host's rule (setting {pct} -> {Settings.CounterBatteryTimerPercent}, row {timer.TextTf?.text}, host {IronNestCoop.UI.CoopUI.TimerName(HostRules.TimerPercent)})");
                else bot.Check(Settings.CounterBatteryTimerPercent != pct, $"its arrow changed the timer setting ({pct} -> {Settings.CounterBatteryTimerPercent})");
                foreach (var x in bot.Shot("settings-multiplayer-timer")) yield return x;
                Settings.CounterBatteryTimerPercent = pct;
            }
            var colour = page.GetComponentsInChildren<OptionsButtonUGUI>(true).FirstOrDefault(o => o.name.Contains("Colour"));
            bot.Note("colour row shows " + colour?.TextTf?.text);
            settings.gameObject.SetActive(false);
            escButtons?.gameObject.SetActive(true);
            menu?.ForceClose(true);
            MissionManager.Instance.EndOperationAndReturnToMenu();
        }
    }
}
