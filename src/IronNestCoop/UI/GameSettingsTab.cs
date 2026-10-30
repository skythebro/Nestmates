using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using Kamgam.UGUIComponentsForSettings;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace IronNestCoop.UI
{
    /// <summary>
    /// A "Multiplayer" tab in the game's own Settings clipboard (Esc menu and main menu), next to Game, Graphics,
    /// Audio and Controls. Built the way the game builds its tabs (Kamgam Settings Generator UGUI): the Audio tab
    /// button and page are copied, and the page is filled with copies of the game's own rows (headline, toggle,
    /// options button, big button) with their setting bindings (resolvers) and localisation removed, then driven by
    /// the mod's settings. Every widget looks and behaves like the game's.
    /// </summary>
    public static class GameSettingsTab
    {
        const string TabName = "TabButtonUGUI (Multiplayer)";
        static readonly bool Off = LaunchArgs.Mutant("nogametab");

        sealed class Binding
        {
            public ToggleUGUI Toggle;
            public OptionsButtonUGUI Options;
            public TMP_Text Label;
            public Func<int> Get;
            public Action<int> Set;
            public Func<bool> Editable;
            public Func<string> Text;
            public RectTransform Frame, RowRect;   // a button row: its frame grows with the label
            public int Shown = int.MinValue;
        }

        static readonly List<Binding> Bindings = new();
        static readonly HashSet<IntPtr> Done = new();
        static float _nextScan;

        /// <summary>Tabs built so far (tests).</summary>
        public static int Built { get; private set; }

        static int _sceneSig = int.MinValue, _scansLeft;
        static float _nextSync;

        /// <summary>The settings menus come with their scenes: they are searched for a few times after a scene
        /// change (a menu may finish building late), not every few seconds for the whole game.</summary>
        public static void Update()
        {
            if (Off) return;
            int sig = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) sig = sig * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            if (sig != _sceneSig) { _sceneSig = sig; _scansLeft = 4; _nextScan = Time.unscaledTime + 0.5f; }
            if (_scansLeft > 0 && Time.unscaledTime >= _nextScan)
            {
                _scansLeft--;
                _nextScan = Time.unscaledTime + 2.5f;
                try { InjectAll(); }
                catch (Exception e) { CoopLog.ErrorOnce("gametab", "[gametab] building the Multiplayer tab failed: " + e); }
            }
            if (Time.unscaledTime < _nextSync) return;
            _nextSync = Time.unscaledTime + 0.1f;
            Sync();
            try { FitAll(); }
            catch (Exception e) { CoopLog.ErrorOnce("gametab-fit", "[gametab] fitting the Multiplayer tab failed: " + e); }
        }

        // ------------------------------------------------------------------ building

        static void InjectAll()
        {
            Bindings.RemoveAll(b => (b.Toggle == null && b.Options == null && b.Label == null));
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TabButtonUGUI>(), true))
            {
                var tab = o.TryCast<TabButtonUGUI>();
                var row = tab?.transform.parent;
                if (row == null || row.name != "TabsCtn" || Done.Contains(row.Pointer)) continue;
                if (row.Find("TabButtonUGUI (Audio)") == null) continue;              // only the real settings menus
                Done.Add(row.Pointer);
                if (row.Find(TabName) != null) continue;
                Inject(row);
            }
        }

        static Transform FindDeep(Transform root, string name)
        {
            if (root == null) return null;
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++) { var f = FindDeep(root.GetChild(i), name); if (f != null) return f; }
            return null;
        }

        /// <summary>Remove what ties a copied row to a game setting or overwrites its text.</summary>
        static void Strip(GameObject go)
        {
            foreach (var c in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n.Contains("Resolver") || n.Contains("Localised") || n.Contains("Localisation") || n.Contains("Helper") || n.Contains("Leaderboard")
                    || n.Contains("Delayed") || n.Contains("Antialias") || n == "SystemDependantClick" || n == "UIDiscordLinker")
                    Object.DestroyImmediate(c);
            }
        }

        static void Inject(Transform tabs)
        {
            var settings = tabs.parent;
            var contentCtn = settings?.Find("ContentCtn");
            var audioTab = tabs.Find("TabButtonUGUI (Audio)");
            var audioPage = contentCtn?.Find("Content (Audio)");
            var gamePage = contentCtn?.Find("Content (Game)");
            var gfxPage = contentCtn?.Find("Content (Graphics)");
            var tHeadline = FindDeep(gamePage, "HeadlineUGUI (Game)");
            var tToggle = FindDeep(gamePage, "ToggleConsoleUGUI (Tooltips)");
            var tOptions = FindDeep(gfxPage, "OptionsButtonConsoleUGUI (Window)");
            var tButton = FindDeep(gamePage, "CatCustomization");
            if (audioTab == null || audioPage == null || tHeadline == null || tToggle == null || tOptions == null || tButton == null)
            {
                CoopLog.Warn($"[gametab] {ScenePath(settings)}: the settings menu is not built as expected; no Multiplayer tab here");
                return;
            }

            // the page: a copy of Audio's, emptied
            var page = Object.Instantiate(audioPage.gameObject, contentCtn);
            page.name = "Content (Multiplayer)";
            page.SetActive(false);
            var layout = FindDeep(page.transform, "Layout (Display)");
            var oldHead = FindDeep(page.transform, "HeadlineUGUI (Display)");
            if (oldHead != null) Object.DestroyImmediate(oldHead.gameObject);
            for (int i = layout.childCount - 1; i >= 0; i--) Object.DestroyImmediate(layout.GetChild(i).gameObject);

            // the tab button: a copy of Audio's, last in the row
            var tabGo = Object.Instantiate(audioTab.gameObject, tabs);
            tabGo.name = TabName;
            Strip(tabGo);
            foreach (var t in tabGo.GetComponentsInChildren<TMP_Text>(true)) t.text = "Multiplayer";
            var tb = tabGo.GetComponent<TabButtonUGUI>();
            tb.Content = page;
            tabGo.transform.SetAsLastSibling();
            Rows.Add(new TabRow { Row = tabs });
            FitTabs(Rows[Rows.Count - 1]);

            Build(layout, tHeadline.gameObject, tToggle.gameObject, tOptions.gameObject, tButton.gameObject);
            // the copied Audio layout had a fixed height for Audio's few rows: it must grow with ours, or the page
            // can't scroll past it (it sprang back to the top)
            var fitter = layout.GetComponent<ContentSizeFitter>() ?? layout.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            var content = layout.parent?.GetComponent<RectTransform>();
            // the game's own fitter on this copied content measures once, from the Audio rows, and sets that height
            // again whenever it updates (the page sprang back to the top): this page sizes its content itself
            if (content != null)
                foreach (var mb in content.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetIl2CppType().Name == "ContentSizeFitterForChildren") mb.enabled = false;
            Pages.Add(new Page { Layout = layout.GetComponent<RectTransform>(), Content = content });

            // every tab must know the new sibling (they cached their group when they started)
            for (int i = 0; i < tabs.childCount; i++) tabs.GetChild(i).GetComponent<TabButtonUGUI>()?.FindSiblings();
            Built++;
            CoopLog.Info($"[gametab] Multiplayer tab added to {ScenePath(settings)}");
        }

        static string ScenePath(Transform t) => t == null ? "?" : Util.ScenePath.Of(t);

        // ------------------------------------------------------------------ fitting

        sealed class TabRow { public Transform Row; public string Key; public readonly Dictionary<IntPtr, (float Size, float Max, float Min)> Base = new(); }
        sealed class Page { public RectTransform Layout, Content; public int TopFrames; public bool TopPivot; }
        const float TopPad = 8f;
        static readonly List<TabRow> Rows = new();
        static readonly List<Page> Pages = new();

        static TMP_Text TabLabel(Transform tab)
        {
            TMP_Text first = null;
            foreach (var t in tab.GetComponentsInChildren<TMP_Text>(true)) { if (t.gameObject.activeInHierarchy) return t; first ??= t; }
            return first;
        }

        /// <summary>
        /// Five tabs in a row made for four. The game's font is monospaced, so "Multiplayer" needs 1.4x the room of
        /// "Controls": each tab is sized to its own label (plus a margin) and the room left over is shared out. Only
        /// if the labels can't fit at all does every label shrink, by the same amount, so they still match.
        /// Re-run when a label changes (language).
        /// </summary>
        static void FitTabs(TabRow r)
        {
            var row = r.Row;
            var hlg = row.GetComponent<HorizontalLayoutGroup>();
            var rowRect = row.GetComponent<RectTransform>();
            if (hlg == null || rowRect == null) return;
            int n = row.childCount;
            var labels = new List<TMP_Text>[n];
            var pref = new float[n];
            var key = new System.Text.StringBuilder();
            float sum = 0;
            for (int i = 0; i < n; i++)
            {
                labels[i] = new List<TMP_Text>(row.GetChild(i).GetComponentsInChildren<TMP_Text>(true));
                foreach (var t in labels[i])
                    if (!r.Base.ContainsKey(t.Pointer)) r.Base[t.Pointer] = (t.enableAutoSizing ? t.fontSizeMax : t.fontSize, t.fontSizeMax, t.fontSizeMin);
                var shown = TabLabel(row.GetChild(i));
                if (shown == null) continue;
                var b = r.Base[shown.Pointer];
                pref[i] = shown.GetPreferredValues(shown.text, 10000, 1000).x * (b.Size / Mathf.Max(1f, shown.fontSize));
                sum += pref[i];
                key.Append(shown.text).Append('|');
            }
            r.Key = key.ToString();
            const float Margin = 24f;   // inside each tab, both sides together
            float avail = rowRect.rect.width - hlg.padding.left - hlg.padding.right;
            float scale = Mathf.Min(1f, (avail - n * Margin) / Mathf.Max(1f, sum));
            float extra = Mathf.Max(0f, (avail - sum * scale - n * Margin) / n);
            hlg.spacing = 0;
            for (int i = 0; i < n; i++)
            {
                var tab = row.GetChild(i);
                foreach (var t in labels[i])
                {
                    var b = r.Base[t.Pointer];
                    t.enableWordWrapping = false;
                    if (t.enableAutoSizing) { t.fontSizeMax = b.Max * scale; t.fontSizeMin = Mathf.Min(b.Min, b.Max * scale); }
                    else t.fontSize = b.Size * scale;
                }
                var rt = tab.GetComponent<RectTransform>();
                if (rt != null) rt.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, pref[i] * scale + Margin + extra);
                var le = tab.GetComponent<LayoutElement>();
                if (le != null) le.preferredWidth = pref[i] * scale + Margin + extra;
            }
            LayoutRebuilder.MarkLayoutForRebuild(rowRect);
            CoopLog.Debug($"[gametab] tab row {rowRect.rect.width:F0} wide: labels {sum:F0} px, scale {scale:F2}, {extra:F0} px extra per tab");
        }

        /// <summary>The page's scroll content must be as tall as its rows (the game's own fitter only follows the
        /// sizes the rows' boxes say they have).</summary>
        const float BottomRoom = 180f;

        static void FitPage(Page p)
        {
            if (p.Layout == null || p.Content == null) return;
            if (!p.Layout.gameObject.activeInHierarchy) { p.TopFrames = 5; return; }   // shown again: start at the top
            // the rows grow downwards from the top of the page: the copied layout grew from its middle, so every
            // added row pushed the first ones further up, out of the page (the page looked as if it started too low)
            if (!LaunchArgs.Mutant("pagetop"))
            {
                var L = p.Layout;
                if (!p.TopPivot) { L.pivot = new Vector2(L.pivot.x, 1f); p.TopPivot = true; }
                float top = L.localPosition.y + L.rect.yMax * L.localScale.y;
                float wantTop = p.Content.rect.yMax - TopPad;
                if (Mathf.Abs(top - wantTop) > 1f) L.localPosition += new Vector3(0, wantTop - top, 0);
            }
            // in the content's own coordinates (GetWorldCorners can't be used: through the interop its array comes
            // back unfilled)
            float lowest = p.Content.rect.yMax;
            for (int i = 0; i < p.Content.childCount; i++)
            {
                var ch = p.Content.GetChild(i).GetComponent<RectTransform>();
                if (ch == null || !ch.gameObject.activeSelf) continue;
                lowest = Mathf.Min(lowest, ch.localPosition.y + ch.rect.yMin * ch.localScale.y);
            }
            // room below the last row: the bottom of the scroll window lies behind the Reset/Close/Apply strip, and
            // a content no taller than the window doesn't scroll at all (the game's pages leave 100-165 px)
            float need = p.Content.rect.yMax - lowest + BottomRoom;
            if (Mathf.Abs(p.Content.rect.height - need) > 1f)
            {
                CoopLog.Debug($"[gametab] page content {p.Content.rect.height:F0} -> {need:F0} px");
                p.Content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, need);
            }
            // the page opens at its first row: the content grows after the scroll view placed it (it used to open
            // part way down, the first row cut off), so for the first frames it is put back at the top
            if (p.TopFrames > 0 && !LaunchArgs.Mutant("pagetop"))
            {
                p.TopFrames--;
                var sr = p.Content.GetComponentInParent<ScrollRect>();
                if (sr != null && sr.verticalNormalizedPosition < 0.999f) { sr.StopMovement(); sr.verticalNormalizedPosition = 1f; }
            }
        }

        /// <summary>Test hook: how far the page's first row (its "Multiplayer" headline) is below the top of the visible
        /// part of the page, in page units (negative: cut off above), or NaN.</summary>
        public static float FirstRowMargin()
        {
            foreach (var p in Pages)
            {
                if (p.Layout == null || !p.Layout.gameObject.activeInHierarchy || p.Layout.childCount == 0) continue;
                var sr = p.Content.GetComponentInParent<ScrollRect>();
                var view = sr != null && sr.viewport != null ? sr.viewport : sr?.GetComponent<RectTransform>();
                var first = p.Layout.GetChild(0).GetComponent<RectTransform>();
                if (view == null || first == null) continue;
                var topOfFirst = view.InverseTransformPoint(first.TransformPoint(new Vector3(0, first.rect.yMax, 0)));
                return view.rect.yMax - topOfFirst.y;
            }
            return float.NaN;
        }

        /// <summary>Test hook: how far down the Multiplayer page is scrolled (1 = top), or -1.</summary>
        public static float PageScroll()
        {
            foreach (var p in Pages)
                if (p.Content != null && p.Content.gameObject.activeInHierarchy) { var sr = p.Content.GetComponentInParent<ScrollRect>(); if (sr != null) return sr.verticalNormalizedPosition; }
            return -1f;
        }

        static void FitAll()
        {
            Rows.RemoveAll(r => r.Row == null);
            Pages.RemoveAll(p => p.Layout == null);
            foreach (var r in Rows)
            {
                var key = new System.Text.StringBuilder();
                for (int i = 0; i < r.Row.childCount; i++) key.Append(TabLabel(r.Row.GetChild(i))?.text).Append('|');
                if (key.ToString() != r.Key) FitTabs(r);
            }
            foreach (var p in Pages) FitPage(p);
        }

        static void Headline(Transform layout, GameObject template, string text)
        {
            var go = Object.Instantiate(template, layout); go.name = "HeadlineUGUI (" + text + ")";
            Strip(go);
            foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) t.text = text;
        }

        static void ToggleRow(Transform layout, GameObject template, string text, Func<bool> get, Action<bool> set, Func<bool> editable = null)
        {
            var go = Object.Instantiate(template, layout); go.name = "ToggleConsoleUGUI (" + text + ")";
            Strip(go);
            var label = go.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null) label.text = text;
            Bindings.Add(new Binding { Toggle = go.GetComponent<ToggleUGUI>(), Get = () => get() ? 1 : 0, Set = v => set(v != 0), Editable = editable });
        }

        static void OptionsRow(Transform layout, GameObject template, string text, string[] options, Func<int> get, Action<int> set, Func<bool> editable = null)
        {
            var go = Object.Instantiate(template, layout); go.name = "OptionsButtonConsoleUGUI (" + text + ")";
            Strip(go);
            var label = go.transform.Find("Label")?.GetComponent<TMP_Text>();
            if (label != null) label.text = text;
            var ob = go.GetComponent<OptionsButtonUGUI>();
            var list = new Il2CppSystem.Collections.Generic.List<string>();
            foreach (var s in options) list.Add(s);
            ob.SetOptions(list.Cast<Il2CppSystem.Collections.Generic.IList<string>>());
            Bindings.Add(new Binding { Options = ob, Get = get, Set = set, Editable = editable });
        }

        static void ButtonRow(Transform layout, GameObject template, Func<string> text, Action click)
        {
            var go = Object.Instantiate(template, layout); go.name = "ButtonRow (co-op)";
            Strip(go);
            var button = go.GetComponentInChildren<Button>(true);
            var label = go.GetComponentInChildren<TMP_Text>(true);
            // the game shows its "Customize cat" button only while the cat is on: this copy is always shown
            foreach (var tr in go.GetComponentsInChildren<Transform>(true))
                if (tr.name.StartsWith("Cat Icon")) tr.gameObject.SetActive(false);
                else if (!tr.name.StartsWith("Selected") && tr.name != "BgShadow") tr.gameObject.SetActive(true);
            if (button != null)
            {
                button.onClick = new Button.ButtonClickedEvent();
                button.onClick.AddListener((UnityAction)click);
            }
            if (label != null) { label.enableWordWrapping = false; label.enableAutoSizing = true; label.fontSizeMax = label.fontSize; label.fontSizeMin = label.fontSize * 0.7f; }
            Bindings.Add(new Binding { Label = label, Text = text, Frame = button?.GetComponent<RectTransform>(), RowRect = go.GetComponent<RectTransform>() });
        }

        /// <summary>A button frame (500 px in the game's prefab) as wide as its label needs, up to the row's width.</summary>
        static void FitButton(Binding b)
        {
            if (b.Frame == null || b.Label == null || b.RowRect == null) return;
            float size = b.Label.fontSizeMax;
            float pref = b.Label.GetPreferredValues(b.Label.text, 10000, 1000).x * (size / Mathf.Max(1f, b.Label.fontSize));
            float pad = Mathf.Max(40f, b.Frame.rect.width - b.Label.rectTransform.rect.width);   // the frame's own margin around its text
            float w = Mathf.Clamp(pref + pad, 500f, b.RowRect.rect.width);
            if (Mathf.Abs(b.Frame.rect.width - w) > 1f) b.Frame.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, w);
        }

        static readonly string[] HueNames =
        {
            "Red", "Vermilion", "Orange", "Amber", "Yellow", "Chartreuse", "Lime", "Grass", "Green", "Jade", "Mint", "Teal",
            "Cyan", "Sky", "Azure", "Cobalt", "Blue", "Indigo", "Violet", "Purple", "Magenta", "Fuchsia", "Rose", "Crimson",
        };

        static string[] TimerNames()
        {
            var names = new string[HostRules.TimerChoices.Length];
            for (int i = 0; i < names.Length; i++) names[i] = CoopUI.TimerName(HostRules.TimerChoices[i]);
            return names;
        }

        static bool CanSetRules() => !Coop.InSession || Coop.IsHost;

        static void Build(Transform layout, GameObject head, GameObject toggle, GameObject options, GameObject button)
        {
            Headline(layout, head, "Multiplayer");
            ButtonRow(layout, button, () => Coop.InSession
                ? $"Nestmates menu (F7)  ({(Coop.IsHost ? "hosting" : "joined")}, {Coop.Session.PlayerCount} players)"
                : "Nestmates menu (F7)  (host, join, invite)", () => CoopUI.Show(0));

            Headline(layout, head, "Your player");
            OptionsRow(layout, options, "Colour", HueNames,
                () => Theme.NearestHueCell(PlayerInfo.Local.Color),
                i => { PlayerInfo.Local.Color = Theme.HueCell(i); Settings.Save(); Coop.Session?.SetLocalInfo(Coop.LocalName, PlayerInfo.Encode()); });
            ToggleRow(layout, toggle, "Bring my own cat", () => PlayerInfo.Local.CatEnabled, v => { PlayerInfo.Local.CatEnabled = v; Settings.Save(); Coop.Session?.SetLocalInfo(Coop.LocalName, PlayerInfo.Encode()); });
            ToggleRow(layout, toggle, "Show my cat's name above it", () => Settings.ShowOwnCatName, v => Settings.ShowOwnCatName = v);
            ToggleRow(layout, toggle, "Emotes on keys 1-8", () => Settings.EmoteKeys, v => Settings.EmoteKeys = v);

            Headline(layout, head, "On screen");
            ToggleRow(layout, toggle, "Show name tags above players", () => Settings.ShowNameTags, v => Settings.ShowNameTags = v);
            ToggleRow(layout, toggle, "Show the crew list while playing", () => Settings.ShowHud, v => Settings.ShowHud = v);
            ToggleRow(layout, toggle, "Show where others work on the map", () => Settings.ShowMapPresence, v => Settings.ShowMapPresence = v);
            ToggleRow(layout, toggle, "Show when players join or leave", () => Settings.ShowJoinLeave, v => Settings.ShowJoinLeave = v);
            ToggleRow(layout, toggle, "Skip the game's intro", () => Settings.SkipIntro, v => Settings.SkipIntro = v);

            Headline(layout, head, "Privacy & status");
            ToggleRow(layout, toggle, "Streamer mode: hide lobby codes", () => Settings.HideJoinCodes, v => Settings.HideJoinCodes = v);
            ToggleRow(layout, toggle, "Steam status: what I am doing", () => Settings.SteamStatus, v => Settings.SteamStatus = v);
            ToggleRow(layout, toggle, "Discord status: what I am doing", () => Settings.DiscordStatus, v => Settings.DiscordStatus = v);
            ToggleRow(layout, toggle, "Discord: friends can join me", () => Settings.DiscordJoin, v => Settings.DiscordJoin = v);
            ToggleRow(layout, toggle, "Status: show where I am", () => Settings.StatusStation, v => Settings.StatusStation = v);

            Headline(layout, head, "Host rules");
            ToggleRow(layout, toggle, "Cards only for the computer operator", () => HostRules.On(Rule.CardsToOperator), v => Settings.CardsToComputerOnly = v, CanSetRules);
            ToggleRow(layout, toggle, "Shared record player volume", () => HostRules.On(Rule.SharedRecordVolume), v => Settings.SharedRecordVolume = v, CanSetRules);
            ToggleRow(layout, toggle, "Show hover effects to everyone", () => HostRules.On(Rule.SharedHover), v => Settings.SharedHover = v, CanSetRules);
            ToggleRow(layout, toggle, "Share lines drawn on the map", () => HostRules.On(Rule.SharedMapDrawings), v => Settings.SharedMapDrawings = v, CanSetRules);
            ToggleRow(layout, toggle, "Share map measurement notes", () => HostRules.On(Rule.SharedMeasureNotes), v => Settings.SharedMeasureNotes = v, CanSetRules);
            OptionsRow(layout, options, "Counter-battery timer", TimerNames(),
                () => Math.Max(0, Array.IndexOf(HostRules.TimerChoices, CanSetRules() ? Settings.CounterBatteryTimerPercent : HostRules.TimerPercent)),
                i => Settings.CounterBatteryTimerPercent = HostRules.TimerChoices[i], CanSetRules);
        }

        // ------------------------------------------------------------------ keeping widgets and settings in step

        static void Sync()
        {
            foreach (var b in Bindings)
            {
                try
                {
                    if (b.Label != null) { if (b.Label.gameObject.activeInHierarchy) { string t = b.Text(); if (b.Label.text != t) { b.Label.text = t; FitButton(b); } else if (b.Shown == int.MinValue) { FitButton(b); b.Shown = 0; } } else b.Shown = int.MinValue; continue; }
                    if (b.Toggle != null)
                    {
                        if (!b.Toggle.gameObject.activeInHierarchy) { b.Shown = int.MinValue; continue; }
                        bool editable = b.Editable?.Invoke() ?? true;
                        if (b.Toggle.Toggle != null && b.Toggle.Toggle.interactable != editable) b.Toggle.Toggle.interactable = editable;
                        int now = b.Toggle.Value ? 1 : 0;
                        if (b.Shown != int.MinValue && now != b.Shown && editable) { b.Set(now); b.Shown = now; continue; }   // the player clicked it
                        int want = b.Get();
                        if (now != want || b.Shown == int.MinValue) { b.Toggle.Value = want != 0; b.Shown = want; }
                    }
                    else if (b.Options != null)
                    {
                        if (!b.Options.gameObject.activeInHierarchy) { b.Shown = int.MinValue; continue; }
                        bool editable = b.Editable?.Invoke() ?? true;
                        var sel = b.Options.GetComponent<Selectable>();
                        if (sel != null && sel.interactable != editable) sel.interactable = editable;
                        int now = b.Options.SelectedIndex;
                        if (b.Shown != int.MinValue && now != b.Shown)
                        {
                            if (editable) { b.Set(now); b.Shown = now; continue; }
                        }
                        int want = b.Get();
                        if (now != want || b.Shown == int.MinValue) { b.Options.SelectedIndex = want; b.Shown = want; }
                    }
                }
                catch (Exception e) { CoopLog.ErrorOnce("gametab-sync", "[gametab] " + e.Message); }
            }
        }
    }
}
