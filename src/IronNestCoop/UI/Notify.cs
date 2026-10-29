using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>Short on-screen message (top centre), e.g. "Only the host can change missions."</summary>
    public static class Notify
    {
        static string _text;
        static float _until;

        public static void Show(string text, float seconds = 4f)
        {
            _text = text;
            _until = Time.unscaledTime + seconds;
            CoopLog.Info("[notify] " + text);
        }

        public static void Draw(float screenW)
        {
            if (_text == null || Time.unscaledTime > _until) return;
            var size = Theme.Label.CalcSize(new GUIContent(_text));
            var r = new Rect((screenW - size.x) / 2 - 16, 60, size.x + 32, size.y + 16);
            GUI.Box(r, GUIContent.none, Theme.Window);
            GUI.Label(new Rect(r.x + 16, r.y + 8, size.x + 4, size.y), _text, Theme.Label);
        }
    }
}
