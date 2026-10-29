using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>
    /// The game's launch intro (logo, credits, the camera flight to the turret) plays on every start
    /// and only Esc skips it. With [Display] SkipIntro on, the mod presses that skip for you: the intro's own skip
    /// relay (YourGame.Input.InputActionUnityEventRelay on "Game Launch Cinamatic") fires its event once, exactly as the key would,
    /// so the game ends the intro its own way. Once per game run.
    /// </summary>
    public static class SkipIntro
    {
        static bool _done;
        static float _seenAt = -1f, _nextLook;

        public static void Update()
        {
            if (_done || !Settings.SkipIntro || Time.unscaledTime < _nextLook) return;
            _nextLook = Time.unscaledTime + 0.1f;
            if (Time.unscaledTime > 180f) { _done = true; return; }   // never long after start-up
            YourGame.Input.InputActionUnityEventRelay relay = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<YourGame.Input.InputActionUnityEventRelay>()))
            {
                var r = o.TryCast<YourGame.Input.InputActionUnityEventRelay>();
                if (r != null && r.isActiveAndEnabled && r.gameObject.name == "Game Launch Cinamatic") { relay = r; break; }
            }
            if (relay == null) { _seenAt = -1f; return; }
            if (_seenAt < 0f) { _seenAt = Time.unscaledTime; return; }
            if (Time.unscaledTime - _seenAt < 0.5f) return;   // let the intro set itself up first, like a human key press
            _done = true;
            try
            {
                relay.onPerformed?.Invoke();
                CoopLog.Info("[intro] skipped the launch intro (setting SkipIntro)");
            }
            catch (System.Exception e) { CoopLog.Warn("[intro] could not skip the intro: " + e.Message); }
        }
    }
}
