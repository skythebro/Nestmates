using HarmonyLib;
using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>
    /// While the co-op window is open during play, ask the game's own InteractionLockBroker for a
    /// free mouse and a frozen player (the same request its consoles make), and release it on close.
    /// The game draws its own (virtual) cursor and keeps the system cursor hidden; the window is IMGUI and
    /// needs the system cursor, so while it is open the system cursor is kept visible and unlocked after
    /// every change the game's cursor manager makes.
    /// </summary>
    public static class CursorRelease
    {
        static InteractionLockBroker _broker;
        static InteractionLockBroker.LockHandle _handle;
        static bool _held;

        public static void Update(bool wantFree)
        {
            if (wantFree == _held) return;
            try
            {
                if (wantFree)
                {
                    if (_broker == null)
                    {
                        var go = GameObject.FindWithTag("LockBroker");
                        _broker = go != null ? go.GetComponent<InteractionLockBroker>() : null;
                    }
                    _held = true;
                    ShowSystemCursor();
                    if (_broker == null) return; // main menu: no broker
                    var req = new InteractionLockBroker.LockRequest
                    {
                        FreezePlayerController = true,
                        UseFreeMouse = true,
                        UseUIActionMap = true,
                        HideVirtualCursorAndBlockWorld = true,
                        DebugLabel = "Nestmates window",
                    };
                    _handle = _broker.Acquire(req);
                    ShowSystemCursor();
                }
                else
                {
                    _held = false;   // first, so the game's own cursor state applies again on release
                    if (_broker != null) _broker.Release(_handle);
                }
            }
            catch (System.Exception e)
            {
                CoopLog.Warn("cursor lock: " + e.Message);
                _held = wantFree;
                _broker = null;
            }
        }

        static void ShowSystemCursor()
        {
            if (!_held || LaunchArgs.Mutant("nocursorfix")) return;
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
        }

        /// <summary>Safety net once per frame (OnGUI runs after the game's Update).</summary>
        public static void Keep() => ShowSystemCursor();

        [HarmonyPatch(typeof(DynamicCursorManager), "ApplySystemCursorSettings")]
        static class AfterApply { static void Postfix() => ShowSystemCursor(); }

        [HarmonyPatch(typeof(DynamicCursorManager), nameof(DynamicCursorManager.SwitchToPresentationMode))]
        static class AfterMode { static void Postfix() => ShowSystemCursor(); }
    }
}
