using System.Collections.Generic;
using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>
    /// Short messages in the top-left corner, under the crew list ("<name> joined", "<name> left"), each in the player's
    /// colour; they fade out after a few seconds. At most four at a time.
    /// </summary>
    public static class Toasts
    {
        const float Life = 5f, Fade = 1f;
        const int Max = 4;
        sealed class Toast { public string Text; public Color Color; public float At; }
        static readonly List<Toast> _list = new();

        /// <summary>For tests: the messages shown lately (newest last).</summary>
        public static readonly List<string> Recent = new();

        public static void Add(string text, Color color)
        {
            if (string.IsNullOrEmpty(text)) return;
            _list.Add(new Toast { Text = text, Color = color, At = Time.unscaledTime });
            while (_list.Count > Max) _list.RemoveAt(0);
            Recent.Add(text); if (Recent.Count > 20) Recent.RemoveAt(0);
            CoopLog.Info("[toast] " + text);
        }

        /// <summary>Draws from <paramref name="y"/> down; returns where the next thing may go.</summary>
        public static float Draw(float x, float y)
        {
            float now = Time.unscaledTime;
            _list.RemoveAll(t => now - t.At > Life);
            var keep = GUI.color;
            foreach (var t in _list)
            {
                float age = now - t.At;
                float a = age > Life - Fade ? Mathf.Clamp01((Life - age) / Fade) : Mathf.Clamp01(age / 0.2f);
                var size = Theme.Small.CalcSize(new GUIContent(t.Text));
                var r = new Rect(x, y, size.x + 30, 22);
                GUI.color = new Color(1, 1, 1, a);
                GUI.Box(r, GUIContent.none, Theme.Window);
                Theme.Dot(new Rect(r.x + 7, r.y + 2, 10, 18), new Color(t.Color.r, t.Color.g, t.Color.b, a), 8);
                GUI.Label(new Rect(r.x + 22, r.y + 2, size.x + 4, 18), t.Text, Theme.Small);
                y += 26;
            }
            GUI.color = keep;
            return y;
        }

        public static void Clear() => _list.Clear();
    }
}
