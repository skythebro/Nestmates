using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>Hand-built IMGUI skin: dark olive panels, brass accent, field-manual feel. Built lazily inside OnGUI.</summary>
    public static class Theme
    {
        public static readonly Color Panel = new(0.09f, 0.10f, 0.08f, 0.96f);
        public static readonly Color PanelLight = new(0.15f, 0.16f, 0.12f, 1f);
        public static readonly Color Border = new(0.42f, 0.38f, 0.22f, 1f);
        public static readonly Color Accent = new(0.85f, 0.68f, 0.28f, 1f);
        public static readonly Color AccentDim = new(0.55f, 0.45f, 0.20f, 1f);
        public static readonly Color Text = new(0.90f, 0.88f, 0.80f, 1f);
        public static readonly Color TextDim = new(0.62f, 0.60f, 0.52f, 1f);
        public static readonly Color Good = new(0.48f, 0.78f, 0.38f, 1f);
        public static readonly Color Bad = new(0.90f, 0.38f, 0.30f, 1f);

        public static readonly Color[] PlayerColors =
        {
            new(0.85f, 0.68f, 0.28f), new(0.40f, 0.70f, 0.90f), new(0.55f, 0.82f, 0.40f),
            new(0.90f, 0.45f, 0.40f), new(0.75f, 0.55f, 0.90f), new(0.90f, 0.85f, 0.85f),
        };

        /// <summary>A player's colour from their hue (0-255 around the colour wheel).</summary>
        public static Color PlayerColor(byte hue) => Color.HSVToRGB(hue / 256f, 0.62f, 0.92f);

        /// <summary>Distance of two hues around the wheel (0-128).</summary>
        public static int HueDistance(byte a, byte b) { int d = System.Math.Abs(a - b); return System.Math.Min(d, 256 - d); }

        /// <summary>The colour strip: this many cells around the hue wheel.</summary>
        public const int HueSteps = 24;
        public static byte HueCell(int i) => (byte)(((i % HueSteps + HueSteps) % HueSteps) * 256 / HueSteps);
        public static int NearestHueCell(byte hue) => (int)System.Math.Round(hue * HueSteps / 256.0) % HueSteps;

        /// <summary>Two players can't have hues this close: the same cell of the strip (cells are ~10.7 apart),
        /// so each other player locks exactly one cell.</summary>
        public const int HueClash = 6;

        public static GUIStyle Window, Title, Label, LabelDim, Small, Button, ButtonAccent, Tab, TabOn, Field, FieldFocus, FieldText, FieldHint, Row, Code, Toggle, Section, Error, Right, HoverTint, Check;
        static bool _built;
        static Texture2D _white;

        /// <summary>The game's stripped UnityEngine has no GUIStyle copy constructor; copy what we use.</summary>
        public static GUIStyle Copy(GUIStyle src)
        {
            var d = new GUIStyle
            {
                font = src.font, fontSize = src.fontSize, fontStyle = src.fontStyle, wordWrap = src.wordWrap,
                alignment = src.alignment, richText = src.richText, clipping = src.clipping,
                fixedHeight = src.fixedHeight, fixedWidth = src.fixedWidth, stretchWidth = src.stretchWidth, stretchHeight = src.stretchHeight,
                padding = R(src.padding.left, src.padding.right, src.padding.top, src.padding.bottom),
                margin = R(src.margin.left, src.margin.right, src.margin.top, src.margin.bottom),
                border = R(src.border.left, src.border.right, src.border.top, src.border.bottom),
                overflow = R(src.overflow.left, src.overflow.right, src.overflow.top, src.overflow.bottom),
                contentOffset = src.contentOffset, imagePosition = src.imagePosition,
            };
            CopyState(src.normal, d.normal); CopyState(src.hover, d.hover); CopyState(src.active, d.active); CopyState(src.focused, d.focused);
            CopyState(src.onNormal, d.onNormal); CopyState(src.onHover, d.onHover); CopyState(src.onActive, d.onActive); CopyState(src.onFocused, d.onFocused);
            return d;
        }

        public static GUIStyle Copy(GUIStyle src, System.Action<GUIStyle> edit) { var d = Copy(src); edit(d); return d; }

        static void CopyState(GUIStyleState s, GUIStyleState d) { d.background = s.background; d.textColor = s.textColor; }

        public static RectOffset R(int l, int r, int t, int b) => new RectOffset { left = l, right = r, top = t, bottom = b };

        public static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c); t.Apply();
            return t;
        }

        /// <summary>Panel texture with a 1px border, 9-sliced via GUIStyle.border.</summary>
        static void SetAll(GUIStyle st, Texture2D bg, Color text)
        {
            foreach (var state in new[] { st.normal, st.hover, st.active, st.focused, st.onNormal, st.onHover, st.onActive, st.onFocused })
            { state.background = bg; state.textColor = text; }
        }

        static Texture2D Framed(Color fill, Color edge)
        {
            var t = new Texture2D(4, 4) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
            for (int x = 0; x < 4; x++)
                for (int y = 0; y < 4; y++)
                    t.SetPixel(x, y, x == 0 || y == 0 || x == 3 || y == 3 ? edge : fill);
            t.Apply();
            return t;
        }

        public static void Ensure()
        {
            if (_built && _white != null) return;
            _built = true;
            _white = Tex(Color.white);

            Window = Theme.Copy(GUI.skin.box, s => { s.padding = Theme.R(14, 14, 12, 12); s.border = Theme.R(1, 1, 1, 1); });
            Window.normal.background = Framed(Panel, Border);

            Title = Theme.Copy(GUI.skin.label, s => { s.fontSize = 18; s.fontStyle = FontStyle.Bold; });
            Title.normal.textColor = Accent;

            Label = Theme.Copy(GUI.skin.label, s => { s.fontSize = 14; s.wordWrap = true; });
            Label.normal.textColor = Text;
            LabelDim = Theme.Copy(Label);
            LabelDim.normal.textColor = TextDim;
            Small = Theme.Copy(Label, s => { s.fontSize = 12; });
            Small.normal.textColor = TextDim;

            Button = Theme.Copy(GUI.skin.button, s => { s.fontSize = 14; s.fixedHeight = 30; s.padding = Theme.R(10, 10, 4, 4); s.border = Theme.R(1, 1, 1, 1); });
            Button.normal.background = Framed(PanelLight, Border);
            Button.hover.background = Framed(new Color(0.22f, 0.22f, 0.15f, 1f), Accent);
            Button.active.background = Framed(AccentDim, Accent);
            Button.normal.textColor = Button.hover.textColor = Button.active.textColor = Text;

            ButtonAccent = Theme.Copy(Button, s => { s.fontStyle = FontStyle.Bold; });
            ButtonAccent.normal.background = Framed(AccentDim, Accent);
            ButtonAccent.hover.background = Framed(Accent, Accent);
            ButtonAccent.normal.textColor = ButtonAccent.hover.textColor = new Color(0.08f, 0.07f, 0.04f);

            Tab = Theme.Copy(Button, s => { s.fixedHeight = 28; });
            TabOn = Theme.Copy(Tab, s => { s.fontStyle = FontStyle.Bold; });
            TabOn.normal.background = Framed(Panel, Accent);
            TabOn.normal.textColor = Accent;

            Field = Theme.Copy(GUI.skin.textField, s => { s.fontSize = 16; s.fixedHeight = 30; s.padding = Theme.R(8, 8, 5, 5); s.border = Theme.R(1, 1, 1, 1); });
            // every state our own: the default skin's hover/active/focus look is a small glow texture, stretched
            // and blurry over the field (it looked stretched and low-res while typing a code)
            var fieldBg = Framed(new Color(0.05f, 0.05f, 0.04f, 1f), Border);
            SetAll(Field, fieldBg, Accent);
            // typing in it: the same field with a gold edge
            FieldFocus = Theme.Copy(Field);
            SetAll(FieldFocus, Framed(new Color(0.07f, 0.06f, 0.04f, 1f), Accent), Accent);

            Row = Theme.Copy(GUI.skin.box, s => { s.padding = Theme.R(8, 8, 6, 6); s.margin = Theme.R(0, 0, 2, 2); s.border = Theme.R(1, 1, 1, 1); });
            Row.normal.background = Framed(PanelLight, new Color(0.25f, 0.24f, 0.16f, 1f));

            Code = Theme.Copy(Label, s => { s.fontSize = 26; s.fontStyle = FontStyle.Bold; s.alignment = TextAnchor.MiddleCenter; });
            Code.normal.textColor = Accent;

            Toggle = Theme.Copy(Label, s => { s.fontSize = 14; s.alignment = TextAnchor.MiddleLeft; s.wordWrap = false; });
            HoverTint = Theme.Copy(GUI.skin.box, s => { s.border = Theme.R(0, 0, 0, 0); });
            HoverTint.normal.background = Tex(new Color(1f, 1f, 1f, 0.07f));
            Check = Theme.Copy(Label, s => { s.fontSize = 14; s.fontStyle = FontStyle.Bold; s.alignment = TextAnchor.MiddleCenter; });
            Check.normal.textColor = Accent;
            FieldText = Theme.Copy(Label, s => { s.fontSize = 16; s.wordWrap = false; s.alignment = TextAnchor.MiddleLeft; s.clipping = TextClipping.Clip; });
            FieldText.normal.textColor = Accent;
            FieldHint = Theme.Copy(FieldText);
            FieldHint.normal.textColor = TextDim;
            Section = Theme.Copy(Label, s => { s.fontStyle = FontStyle.Bold; s.fontSize = 13; });
            Section.normal.textColor = Accent;
            Error = Theme.Copy(Label);
            Error.normal.textColor = Bad;
            Right = Theme.Copy(Small, s => { s.alignment = TextAnchor.MiddleRight; });
            Toggle.normal.textColor = Text;
        }

        public static void Dot(Rect r, Color c, float size = 12)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(new Rect(r.x, r.y + (r.height - size) / 2, size, size), _white);
            GUI.color = old;
        }

        public static void Fill(Rect r, Color c)
        {
            var old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _white);
            GUI.color = old;
        }
    }
}
