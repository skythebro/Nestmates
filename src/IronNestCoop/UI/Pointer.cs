using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestCoop.UI
{
    /// <summary>
    /// The mouse as the game sees it. The game drives its own VirtualCursor through the Input System and warps
    /// the system mouse to it every frame, so IMGUI's own click handling (GUI.Button, which needs a matching
    /// MouseDown and MouseUp from window messages) does not fire reliably in this game. The co-op window
    /// therefore reads position, button and wheel from the Input System once per frame and hit-tests itself.
    /// Positions are IMGUI screen pixels (origin top-left), before the window's GUI.matrix scale.
    /// </summary>
    public static class Pointer
    {
        static int _frame = -1;
        static bool _wasDown, _consumed;

        public static Vector2 Screen { get; private set; }
        public static bool PressedThisFrame { get; private set; }
        public static float Scroll { get; private set; }

        /// <summary>Test hook: a press to inject at a screen position (IMGUI pixels), consumed on the next frame.</summary>
        public static Vector2? TestPress;

        /// <summary>Clicks seen since start (diagnostics).</summary>
        public static int Presses;

        /// <summary>Call at the start of every OnGUI; reads the devices once per frame.</summary>
        public static void Update()
        {
            if (Time.frameCount == _frame) return;
            _frame = Time.frameCount;
            _consumed = false;
            PressedThisFrame = false; Scroll = 0;
            var m = Mouse.current;
            if (m != null)
            {
                var p = m.position.ReadValue();
                Screen = new Vector2(p.x, UnityEngine.Screen.height - p.y);
                bool down = m.leftButton.isPressed;
                PressedThisFrame = down && !_wasDown;
                _wasDown = down;
                // one notch is 120 on older input backends and 1 on newer ones (without this, scrolling took tiny steps on some setups);
                // IMGUI scrolls down for positive
                float raw = m.scroll.ReadValue().y;
                Scroll = -(Mathf.Abs(raw) >= 20f ? raw / 120f : raw);
            }
            if (TestPress is Vector2 t) { Screen = t; PressedThisFrame = true; TestPress = null; }
            if (PressedThisFrame) Presses++;
        }

        /// <summary>A control took this frame's press.</summary>
        public static bool Consumed => _consumed;

        /// <summary>Hit-test once per frame, in the Repaint pass, so each press acts once and only on the first control hit.</summary>
        public static bool TakePress(Vector2 guiPoint, Rect r)
        {
            if (_consumed || !PressedThisFrame || Event.current == null || Event.current.type != EventType.Repaint) return false;
            if (!r.Contains(guiPoint)) return false;
            _consumed = true;
            return true;
        }
    }
}
