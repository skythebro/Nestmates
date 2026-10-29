using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Time never stops in a shared world. The game pauses by setting Time.timeScale to 0, scaling the physics
    /// step down with it and pausing audio (from the pause menu, and automatically when the window loses
    /// focus). If one player did that, only their copy of the world would freeze while everyone else's ran on.
    /// In a session the menu still opens and closes, but time keeps going, and the game keeps running in the
    /// background when alt-tabbed.
    ///
    /// Shared pause: when every player has their pause menu open, the world stops for everyone (the host
    /// decides, based on each player's menu state). As soon as one of them closes it, time runs again for all.
    /// </summary>
    public sealed class NoPause : SyncModule
    {
        public override string Name => "nopause";
        public static NoPause Instance { get; private set; }
        bool _savedRunInBackground;

        public NoPause() { Instance = this; }

        /// <summary>Everyone has their pause menu open: the world is stopped for all (host's verdict).</summary>
        public static bool AllPaused { get; private set; }
        static readonly bool NoSharedPause = LaunchArgs.Mutant("nosharedpause");

        /// <summary>
        /// A menu counts for the shared pause only once it has been open this long. The menu's own open and close
        /// animations need time to run: pausing on the very frame Escape was pressed (and running again on the
        /// next press) froze them halfway when a player pressed Escape quickly, and the menu could stay stuck.
        /// </summary>
        static readonly float Settle = LaunchArgs.Mutant("nopausesettle") ? 0f : 1f;
        public static int PauseFlips;   // read by the tests: how often the shared pause turned on or off

        bool _localOpen, _sentOpen;
        float _nextMenuSend, _localOpenSince;
        readonly Dictionary<byte, bool> _open = new();   // host: each player's menu
        readonly Dictionary<byte, float> _openSince = new();   // host: since when (as received)

        /// <summary>This player's pause menu (the game's Escape clipboard) is open.</summary>
        public static bool LocalMenuOpen
        {
            get
            {
                // The menus come with the scenes, so search again when the scenes change (or while none has been
                // found) rather than on a fixed 2 s timer.
                int sig = SceneSig();
                if ((sig != _menuScene || _menus.Exists(m => m == null) || (_menus.Count == 0 && Time.unscaledTime >= _nextMenuScan)) && Perf.HeavyFrame())
                {
                    _menuScene = sig;
                    _nextMenuScan = Time.unscaledTime + 2f;
                    _menus.Clear();
                    foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<EscapeMenuToggleUnityEvent>()))
                    { var m = o.TryCast<EscapeMenuToggleUnityEvent>(); if (m != null) _menus.Add(m); }
                }
                foreach (var m in _menus) if (m != null && m.isActiveAndEnabled && m.isOpen) return true;
                return false;
            }
        }
        static readonly List<EscapeMenuToggleUnityEvent> _menus = new();
        static float _nextMenuScan;
        static int _menuScene = int.MinValue;

        /// <summary>
        /// Closing the menu starts a timer (DelayedUnityEvent on the menu) that hides its clipboard once the close
        /// animation is over. Alone, opening the menu stops time, so an old timer never fires while it is open; in a
        /// session time runs on, and a quick close and open again let that timer hide the open menu (players saw the
        /// world paused for everyone with no menu visible). A timer still running when the menu opens is stopped.
        /// </summary>
        public static int CloseTimersStopped;   // read by the tests

        static void StopCloseTimers()
        {
            if (!Coop.InSession || LaunchArgs.Mutant("menutimer")) return;
            foreach (var m in _menus)
            {
                if (m == null) continue;
                foreach (var d in m.GetComponents<Minimal.Generic.DelayedUnityEvent>())
                {
                    if (d == null || d.running == null) continue;
                    bool fire = d.triggerWhenStopped;
                    d.triggerWhenStopped = false;   // stopping must not run what the timer would have run
                    try { d.StopTimer(); CloseTimersStopped++; CoopLog.Debug("[nopause] the menu opened again before its close timer ran: timer stopped"); }
                    finally { d.triggerWhenStopped = fire; }
                }
            }
        }

        static int SceneSig()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        public override void Register(Session s) => s.On((ushort)Msg.MenuState, OnMenu);

        void OnMenu(in MsgContext c, NetReader r)
        {
            bool v = r.Bool();
            if (Coop.IsHost)
            {
                if (c.FromHost) return;
                bool was = _open.TryGetValue(c.FromSlot, out var o) && o;
                if (v && !was) _openSince[c.FromSlot] = Time.unscaledTime;
                _open[c.FromSlot] = v;
                Decide();
            }
            else if (c.FromHost) SetAll(v);
        }

        public override void OnPlayerLeft(Player p) { _open.Remove(p.Slot); _openSince.Remove(p.Slot); if (Coop.IsHost) Decide(); }

        public override void SendSnapshot(byte slot)
        {
            var w = Coop.Writer(); w.Bool(AllPaused);
            Coop.Session.Send((ushort)Msg.MenuState, w, Delivery.Reliable, slot);
        }

        void Decide()
        {
            float now = Time.unscaledTime;
            bool all = !NoSharedPause && _localOpen && now - _localOpenSince >= Settle && Coop.Session.PlayerCount > 0;
            if (all)
                foreach (var p in Coop.Session.Players)
                    if (p != null && p.Slot != Coop.Session.LocalSlot
                        && !(_open.TryGetValue(p.Slot, out var o) && o && _openSince.TryGetValue(p.Slot, out var since) && now - since >= Settle)) { all = false; break; }
            // once paused, it stays paused until a menu closes (no settle time for running again)
            if (AllPaused && !all && _localOpen && !NoSharedPause)
            {
                all = true;
                foreach (var p in Coop.Session.Players)
                    if (p != null && p.Slot != Coop.Session.LocalSlot && !(_open.TryGetValue(p.Slot, out var o) && o)) { all = false; break; }
            }
            if (all == AllPaused) return;
            SetAll(all);
            var w = Coop.Writer(); w.Bool(all);
            Coop.Send(Msg.MenuState, w);
        }

        void SetAll(bool on)
        {
            if (on == AllPaused) return;
            AllPaused = on; PauseFlips++;
            CoopLog.Info(on ? "[nopause] every player has the pause menu open: the world is paused" : "[nopause] the world runs again");
            Time.timeScale = on && _localOpen ? 0f : 1f;
            RestorePhysicsStep();
        }

        void TickMenu()
        {
            bool open = LocalMenuOpen;
            if (open && !_localOpen) { _localOpenSince = Time.unscaledTime; StopCloseTimers(); }
            _localOpen = open;
            if (Coop.IsHost) { Decide(); return; }
            if (_localOpen != _sentOpen || Time.unscaledTime >= _nextMenuSend)
            {
                _sentOpen = _localOpen; _nextMenuSend = Time.unscaledTime + 3f;
                var w = Coop.Writer(); w.Bool(_localOpen);
                Coop.SendToHost(Msg.MenuState, w);
            }
        }

        public override void OnSessionStart()
        {
            _savedRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
        }

        public override void OnSessionEnd()
        {
            Application.runInBackground = _savedRunInBackground;
            if (AllPaused) { AllPaused = false; Time.timeScale = 1f; }
            _open.Clear(); _openSince.Clear();
        }

        public override void Tick(float dt)
        {
            if (!Coop.InSession) return;
            TickMenu();
            // a guest that closed its menu runs on at once (its closing animation too): the host's "runs again"
            // is a round trip away
            if (AllPaused && _localOpen) { if (Time.timeScale != 0f) Time.timeScale = 0f; RestorePhysicsStep(); return; }
            // safeguard: anything else that zeroes timeScale during a session
            if (Time.timeScale == 0f) { Time.timeScale = 1f; CoopLog.Debug("[nopause] time scale was 0 in a session; restored"); }
            RestorePhysicsStep();
        }

        /// <summary>
        /// The game pauses with timeScale 0 AND fixedDeltaTime scaled down with it. Running time again with the
        /// paused physics step made Unity run thousands of physics steps per frame (2 FPS in the Esc menu).
        /// </summary>
        static void RestorePhysicsStep()
        {
            float original = PauseManager.s_originalFixedDeltaTime;
            if (LaunchArgs.Mutant("nophysicsfix")) return;
            if (original > 0f && Time.fixedDeltaTime < original * 0.5f)
            {
                Time.fixedDeltaTime = original;
                CoopLog.Debug($"[nopause] physics step restored to {original}");
            }
        }

        [HarmonyPatch(typeof(PauseManager), nameof(PauseManager.RequestPause))]
        static class Pause
        {
            static void Postfix(PauseManager __instance)
            {
                if (!Coop.InSession || AllPaused) return;
                if (Time.timeScale == 0f) Time.timeScale = 1f;
                RestorePhysicsStep();
                // the menu stays, but the world (and its sound) keeps running for this player too
                try { __instance.RestoreAudioOnUnpause(); }
                catch (System.Exception e) { CoopLog.ErrorOnce("nopause-audio", "[nopause] could not keep audio running: " + e.Message); }
            }
        }
    }
}
