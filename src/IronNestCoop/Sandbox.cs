using System.IO;
using HarmonyLib;
using UnityEngine;

namespace IronNestCoop
{
    /// <summary>
    /// Test-copy isolation (-coop-sandbox=DIR): saves go to DIR instead of the game's LocalLow
    /// folder. Never active in a normal launch. PlayerPrefs (registry) cannot be redirected reliably
    /// (Unity writes window prefs natively; patching PlayerPrefs failed with an IL compile error),
    /// so tools/coopqa.py backs up the registry key before each run and restores it after.
    /// </summary>
    public static class Sandbox
    {
        public static string Dir { get; private set; }

        public static bool Active => Dir != null;

        public static void Enable(string dir)
        {
            Dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(Dir);
            Application.targetFrameRate = 30; // test copies must not hog the GPU
            QualitySettings.vSyncCount = 0;
            CoopLog.Info($"sandbox active: saves -> {Dir}, 30 fps cap");
        }

        static float _nextWindowCheck;

        /// <summary>
        /// The game's settings system applies its own (native, fullscreen) resolution after start,
        /// ignoring -screen-* flags. Test copies must stay a small window and must not hog the GPU.
        /// </summary>
        public static void EnforceWindow()
        {
            if (Application.targetFrameRate != 30) Application.targetFrameRate = 30;
            if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
            if (Time.unscaledTime < _nextWindowCheck) return;
            _nextWindowCheck = Time.unscaledTime + 1f;
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != 960 || Screen.height != 540)
            {
                Screen.SetResolution(960, 540, FullScreenMode.Windowed);
                CoopLog.Info($"sandbox: window forced to 960x540 (was {Screen.width}x{Screen.height} {Screen.fullScreenMode})");
            }
        }

        [HarmonyPatch(typeof(Application), nameof(Application.persistentDataPath), MethodType.Getter)]
        static class PersistentDataPath
        {
            static void Postfix(ref string __result) { if (Active) __result = Dir; }
        }
    }
}
