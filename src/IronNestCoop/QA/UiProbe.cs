using System;
using System.Collections.Generic;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// -coop-uiprobe: once, call each IMGUI method the UI might use and log which ones the game's
    /// stripped UnityEngine supports ("Method unstripping failed" otherwise).
    /// </summary>
    public static class UiProbe
    {
        static int _frames;
        static bool _done;

        public static readonly bool Enabled = LaunchArgs.InSandbox && Array.IndexOf(Environment.GetCommandLineArgs(), "-coop-uiprobe") >= 0;

        public static void OnGUI()
        {
            if (_done || Event.current.type != EventType.Repaint || ++_frames < 3) return;
            _done = true;
            var ok = new List<string>(); var bad = new List<string>();
            void T(string name, Action a)
            {
                try { a(); ok.Add(name); }
                catch (Exception e) { bad.Add($"{name} ({e.GetType().Name}: {e.Message})"); }
            }
            var style = GUI.skin.box;
            var r = new Rect(10, 10, 100, 20);
            T("GUI.skin", () => { var _ = GUI.skin.button; });
            T("GUI.Box", () => GUI.Box(r, "x"));
            T("GUI.Box(style)", () => GUI.Box(r, "x", style));
            T("GUI.Label", () => GUI.Label(r, "x"));
            T("GUI.Label(style)", () => GUI.Label(r, "x", style));
            T("GUI.Button", () => GUI.Button(r, "x"));
            T("GUI.Button(style)", () => GUI.Button(r, "x", style));
            T("GUI.TextField", () => GUI.TextField(r, "x"));
            T("GUI.TextField(max,style)", () => GUI.TextField(r, "x", 10, style));
            T("GUI.Toggle", () => GUI.Toggle(r, true, "x"));
            T("GUI.DrawTexture", () => GUI.DrawTexture(r, Texture2D.whiteTexture));
            T("GUI.BeginGroup", () => { GUI.BeginGroup(r); GUI.EndGroup(); });
            T("GUI.BeginScrollView", () => { GUI.BeginScrollView(r, Vector2.zero, new Rect(0, 0, 100, 200)); GUI.EndScrollView(); });
            T("GUI.color", () => { var c = GUI.color; GUI.color = c; });
            T("GUI.enabled", () => { var c = GUI.enabled; GUI.enabled = c; });
            T("GUI.FocusControl", () => GUI.FocusControl(null));
            T("GUIStyle.CalcSize", () => style.CalcSize(new GUIContent("x")));
            T("GUIStyle.CalcHeight", () => style.CalcHeight(new GUIContent("x"), 100));
            T("GUIStyle.Draw", () => style.Draw(r, new GUIContent("x"), false, false, false, false));
            T("GUIContent(string)", () => new GUIContent("x"));
            T("GUIUtility.systemCopyBuffer", () => { var s = GUIUtility.systemCopyBuffer; });
            T("Event.current.mousePosition", () => { var p = Event.current.mousePosition; });
            T("GUILayout.BeginArea", () => { GUILayout.BeginArea(r); GUILayout.EndArea(); });
            T("GUILayout.BeginVertical(style)", () => { GUILayout.BeginVertical(style); GUILayout.EndVertical(); });
            T("GUILayout.BeginHorizontal", () => { GUILayout.BeginHorizontal(); GUILayout.EndHorizontal(); });
            T("GUILayout.Label", () => GUILayout.Label("x"));
            T("GUILayout.Button", () => GUILayout.Button("x"));
            T("GUILayout.TextField", () => GUILayout.TextField("x"));
            T("GUILayout.Toggle", () => GUILayout.Toggle(true, "x"));
            T("GUILayout.Space", () => GUILayout.Space(4));
            T("GUILayout.FlexibleSpace", () => GUILayout.FlexibleSpace());
            T("GUILayout.Width", () => GUILayout.Width(10));
            T("GUILayout.BeginScrollView", () => { GUILayout.BeginScrollView(Vector2.zero); GUILayout.EndScrollView(); });
            T("GUILayoutUtility.GetRect", () => GUILayoutUtility.GetRect(10, 10));
            CoopLog.Info($"uiprobe OK: {string.Join(", ", ok)}");
            CoopLog.Info($"uiprobe FAILED: {string.Join("; ", bad)}");
        }
    }
}
