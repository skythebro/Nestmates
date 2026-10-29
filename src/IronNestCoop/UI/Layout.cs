using System;
using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>
    /// Tiny immediate-mode layout on top of the GUI.* calls the game's stripped UnityEngine still has
    /// (GUILayout, TextField and ScrollView all fail with "Method unstripping failed").
    /// A vertical cursor stacks controls; Row() splits one line horizontally. Includes a text field.
    /// </summary>
    public static class L
    {
        static float _x, _y, _w, _top;
        static float _scroll, _viewTop, _viewBottom;

        public const float Gap = 6;

        public static float Width => _w;
        public static float Cursor => _y;
        public static float Used => _y - _top;

        public static void Begin(float x, float y, float w) { _x = x; _y = y; _w = w; _top = y; }

        // -------------------------------------------------------------- pointer (see Pointer)

        static float _scale = 1;
        static Vector2 _group;

        /// <summary>The scale CoopUI puts into GUI.matrix; pointer positions are divided by it.</summary>
        public static void SetScale(float s) { _scale = s; _group = Vector2.zero; }

        /// <summary>The pointer in the current GUI coordinates (scaled, inside the current group).</summary>
        public static Vector2 PointerPos => Pointer.Screen / _scale - _group;

        public static bool Hover(Rect r) => r.Contains(PointerPos);

        /// <summary>True once when the left button goes down over <paramref name="r"/>.</summary>
        public static bool Clicked(Rect r) => Pointer.TakePress(PointerPos, r);

        /// <summary>A button: drawn as a box, clicked through <see cref="Pointer"/> (GUI.Button does not fire in this game).</summary>
        public static bool Btn(Rect r, string text, GUIStyle s)
        {
            GUI.Box(r, text, s);
            if (Hover(r)) GUI.Box(r, GUIContent.none, Theme.HoverTint);
            return Clicked(r);
        }

        public static Rect Next(float h, float gap = Gap)
        {
            var r = new Rect(_x, _y, _w, h);
            _y += h + gap;
            return r;
        }

        public static void Space(float h) => _y += h;

        public static float LabelHeight(string text, GUIStyle s) => Mathf.Max(s.CalcHeight(new GUIContent(text), _w), s.fontSize + 6);

        public static void Label(string text, GUIStyle s)
        {
            var r = Next(LabelHeight(text, s), 2);
            GUI.Label(r, text, s);
        }

        public static bool Button(string text, GUIStyle s, float h = 30) => Btn(Next(h), text, s);

        public static bool Toggle(bool value, string text, GUIStyle s)
        {
            var r = Next(24, 4);
            var box = new Rect(r.x, r.y + 3, 18, 18);
            GUI.Box(box, GUIContent.none, value ? Theme.TabOn : Theme.Tab);
            if (value) GUI.Label(box, "x", Theme.Check);
            GUI.Label(new Rect(r.x + 22, r.y, r.width - 22, r.height), text, s);
            if (Hover(r)) GUI.Box(r, GUIContent.none, Theme.HoverTint);
            return Clicked(r) ? !value : value;
        }

        // -------------------------------------------------------------- rows

        public struct Row
        {
            float _x, _right;
            public readonly float Y, H;
            public Row(float x, float y, float w, float h) { _x = x; _right = x + w; Y = y; H = h; }

            /// <summary>Take a fixed width from the left.</summary>
            public Rect Take(float w) { var r = new Rect(_x, Y, w, H); _x += w + Gap; return r; }

            /// <summary>Take a fixed width from the right.</summary>
            public Rect TakeRight(float w) { _right -= w; var r = new Rect(_right, Y, w, H); _right -= Gap; return r; }

            /// <summary>Whatever is left between the two sides.</summary>
            public Rect Rest() => new Rect(_x, Y, Mathf.Max(0, _right - _x), H);

            /// <summary>Split the remaining width into n equal cells.</summary>
            public Rect Cell(int i, int n)
            {
                float total = _right - _x, cw = (total - Gap * (n - 1)) / n;
                return new Rect(_x + i * (cw + Gap), Y, cw, H);
            }
        }

        public static Row BeginRow(float h = 30, float gap = Gap)
        {
            var row = new Row(_x, _y, _w, h);
            _y += h + gap;
            return row;
        }

        /// <summary>Framed row background (list entries).</summary>
        public static Row BoxRow(float h = 40)
        {
            var r = Next(h, 3);
            GUI.Box(r, GUIContent.none, Theme.Row);
            return new Row(r.x + 8, r.y + (h - 30) / 2, r.width - 16, 30);
        }

        // -------------------------------------------------------------- text field

        static string _focus;
        internal static string TestFocus;   // test hook: focus this field (as a click would)
        static string _submitted;

        /// <summary>Single-line text field: click to focus, type, Backspace, Ctrl+V, Enter submits, Esc leaves.</summary>
        public static string TextField(Rect r, string id, string value, int max, GUIStyle s, string placeholder = null, bool masked = false)
        {
            value ??= "";
            var e = Event.current;
            if (TestFocus != null && TestFocus == id) { _focus = id; TestFocus = null; }
            bool focused = _focus == id;
            _drawnFields.Add(id);
            if (Clicked(r)) { _focus = id; focused = true; }
            else if (focused && Pointer.PressedThisFrame && e.type == EventType.Repaint && !Hover(r)) { _focus = null; focused = false; }
            if (focused && e.type == EventType.KeyDown)
            {
                bool ctrl = e.control || e.command;
                if (e.keyCode == KeyCode.Backspace) { if (value.Length > 0) value = value.Substring(0, value.Length - 1); e.Use(); }
                else if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter) { _submitted = id; e.Use(); }
                else if (e.keyCode == KeyCode.Escape) { _focus = null; e.Use(); }
                else if (ctrl && e.keyCode == KeyCode.V) { value += Clean(GUIUtility.systemCopyBuffer); e.Use(); }
                else if (ctrl && e.keyCode == KeyCode.C) { GUIUtility.systemCopyBuffer = value; e.Use(); }
                else if (!ctrl && e.character != 0 && !char.IsControl(e.character)) { value += e.character; e.Use(); }
                if (value.Length > max) value = value.Substring(0, max);
            }
            GUI.Box(r, GUIContent.none, focused && s == Theme.Field ? Theme.FieldFocus : s);
            bool caret = focused && (Time.unscaledTime * 2 % 2) < 1.2f;
            var inner = new Rect(r.x + 8, r.y, r.width - 16, r.height);
            if (value.Length == 0 && !focused && placeholder != null) GUI.Label(inner, placeholder, Theme.FieldHint);
            else GUI.Label(inner, (masked ? Mask(value) : value) + (caret ? "|" : ""), Theme.FieldText);
            return value;
        }

        /// <summary>A secret shown as dots (one per character, so the length still reads).</summary>
        public static string Mask(string s) => string.IsNullOrEmpty(s) ? "" : new string('•', s.Length);

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
            return sb.ToString().Trim();
        }

        /// <summary>True once after Enter was pressed in that field.</summary>
        public static bool TakeSubmit(string id) { if (_submitted != id) return false; _submitted = null; return true; }

        public static bool AnyFieldFocused => _focus != null;

        static readonly System.Collections.Generic.HashSet<string> _drawnFields = new();

        /// <summary>
        /// Call once per frame after drawing (Repaint): a focused field that was not drawn (window closed, other
        /// tab, other state) loses focus, so F7 and the game's keys never stay captured.
        /// </summary>
        public static void EndFrame()
        {
            if (_focus != null && !_drawnFields.Contains(_focus)) _focus = null;
            _drawnFields.Clear();
        }

        public static void ClearFocus() { _focus = null; _drawnFields.Clear(); }

        // -------------------------------------------------------------- scrolling (wheel only)

        /// <summary>Start a clipped, wheel-scrollable region of the given screen height.</summary>
        static int _wheelEventFrame = -1;

        public static void BeginScroll(Rect view, ref float scroll, float contentHeight)
        {
            var e = Event.current;
            // Unity's own wheel event comes once per notch, however small the turn; the device read is the fallback
            // for frames without one (a light turn could fall between the input update and this draw)
            if (e.type == EventType.ScrollWheel && view.Contains(PointerPos))
            {
                scroll += Mathf.Sign(e.delta.y) * Mathf.Max(1f, Mathf.Abs(e.delta.y) / 3f) * 90f;
                _wheelEventFrame = Time.frameCount;
                e.Use();
            }
            else if (e.type == EventType.Repaint && Pointer.Scroll != 0 && _wheelEventFrame != Time.frameCount && view.Contains(PointerPos))
                scroll += Pointer.Scroll * 90;
            scroll = Mathf.Clamp(scroll, 0, Mathf.Max(0, contentHeight - view.height));
            GUI.BeginGroup(view);
            _group += view.position;
            _scroll = scroll; _viewTop = view.y; _viewBottom = view.height;
            Begin(0, -scroll, view.width);
        }

        public static void EndScroll(float x, float y, float w)
        {
            GUI.EndGroup();
            _group = Vector2.zero;
            Begin(x, y, w);
        }
    }
}
