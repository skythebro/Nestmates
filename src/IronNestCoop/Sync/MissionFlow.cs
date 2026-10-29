using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using SleepyNodes;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// The host decides where everyone is: starting, restarting or leaving a mission, and returning to the
    /// map or menu. Guests follow and cannot start missions themselves. Every transition carries an epoch
    /// number so stale or duplicated messages are ignored. The mission clock is locked to the host's.
    /// </summary>
    public sealed class MissionFlow : SyncModule
    {
        public override string Name => "mission";
        public static MissionFlow Instance { get; private set; }

        public enum Transition : byte { ReturnToMap = 1, ReturnToMenu = 2, Restart = 3, Finish = 4 }

        /// <summary>Incremented by the host for every mission start; guests remember the last applied.</summary>
        public int Epoch { get; private set; }
        public string OperationId { get; private set; }
        public string MissionId { get; private set; }
        public bool JoinedInProgress { get; private set; }
        public double LastClockError { get; private set; }

        float _nextClock;
        (int epoch, string op, string mission, bool join)? _pendingStart;
        // Guest only: it joined from inside its own mission and is on its way back to the menu. The host's start
        // waits for that. Applying it at once let the menu load finish last and left the player at the main menu.
        float _leavingLocalSince = -1f, _menuSince = -1f;
        bool LeavingLocal => _leavingLocalSince >= 0f;
        static float _menuSeenAt = -1;

        /// <summary>
        /// The game finished booting: the main menu scene is loaded and a moment has passed.
        /// Starting a mission earlier (e.g. a guest launched by a Steam invite) breaks the game's own
        /// initialisation (NullReference in MissionAnalyticsRelay, phase never changes).
        /// </summary>
        public static bool GameReady
        {
            get
            {
                if (MissionManager.Instance == null) return false;
                if (_menuSeenAt < 0)
                {
                    bool menu = false;
                    for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                    {
                        var sc = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                        if (sc.name == "Main Menu" && sc.isLoaded) menu = true;
                    }
                    if (menu) _menuSeenAt = Time.unscaledTime;
                    return false;
                }
                return Time.unscaledTime - _menuSeenAt > 3f;
            }
        }

        public MissionFlow() { Instance = this; }

        public override void Register(Session s)
        {
            s.On((ushort)Msg.MissionStart, OnMissionStart);
            s.On((ushort)Msg.MissionTransition, OnTransition);
            s.On((ushort)Msg.ClockSync, OnClock);
        }

        public override void OnSessionStart()
        {
            // Epochs must not repeat from one session to the next: caches keyed on "this mission" (deliveries, dice
            // counts, timers, card uses...) would otherwise carry a previous session's values into the first mission.
            // The host starts each session at a fresh base; guests take the host's epoch from its messages.
            Epoch = Coop.IsHost ? (int)((uint)System.Environment.TickCount % 1_000_000u) * 16 + 16 : 0;
            _pendingStart = null;   // a start the last session's host sent is not for this one
            _leavingLocalSince = _menuSince = -1f;
            if (Coop.IsGuest && Phase != MissionManager.GamePhase.MainMenu)
            {
                // Joined from inside a single-player mission (or after leaving a co-op one, which keeps running here):
                // go back to the menu, and follow the host only once it is there.
                _leavingLocalSince = Time.unscaledTime;
                CoopLog.Info($"[mission] joined from inside a mission ({Phase}): back to the menu first, then following the host");
                Coop.Apply("leave local mission", () => MissionManager.Instance.EndOperationAndReturnToMenu());
            }
        }

        public override void OnPlayerJoined(Player p)
        {
            if (!Coop.IsHost || p.IsLocal) return;
            var mm = MissionManager.Instance;
            if (mm != null && Phase == MissionManager.GamePhase.MissionActive && mm.CurrentMission != null)
                SendStart(mm.CurrentOperation, mm.CurrentMission, joinInProgress: true, target: p.Slot);
        }

        public static MissionManager.GamePhase Phase
        {
            get
            {
                var mm = MissionManager.Instance;
                return mm != null ? mm._CurrentPhase_k__BackingField : MissionManager.GamePhase.MainMenu;
            }
        }

        // ------------------------------------------------------------------ host side

        void SendStart(OperationGraph op, MissionGraph mission, bool joinInProgress, byte target = Protocol.TargetAll)
        {
            if (!joinInProgress) { Epoch++; WorldReady.NewEpoch(Epoch); }
            OperationId = op?.OperationID; MissionId = mission?.MissionID;
            var w = Coop.Writer();
            w.VarU((ulong)Epoch); w.Str(OperationId); w.Str(MissionId); w.Bool(joinInProgress);
            Coop.Send(Msg.MissionStart, w, Delivery.Reliable, target);
            CoopLog.Info($"[mission] host -> start op='{OperationId}' mission='{MissionId}' epoch={Epoch} join={joinInProgress} target={target}");
        }

        void SendTransition(Transition t)
        {
            WorldReady.Unready();
            var w = Coop.Writer();
            w.VarU((ulong)Epoch); w.U8((byte)t);
            Coop.Send(Msg.MissionTransition, w);
            CoopLog.Info($"[mission] host -> {t} epoch={Epoch}");
        }

        public override void Tick(float dt)
        {
            if (LeavingLocal)
            {
                // wait until the guest has been at the menu for half a second (its scenes have settled), or give up after 30 s
                if (Phase == MissionManager.GamePhase.MainMenu) { if (_menuSince < 0) _menuSince = Time.unscaledTime; }
                else _menuSince = -1f;
                bool settled = _menuSince >= 0 && Time.unscaledTime - _menuSince > 0.5f;
                if (settled || Time.unscaledTime - _leavingLocalSince > 30f)
                {
                    CoopLog.Info($"[mission] {(settled ? "back at the menu" : "still not at the menu after 30 s")}; {(_pendingStart != null ? "following the host now" : "waiting for the host")}");
                    _leavingLocalSince = _menuSince = -1f;
                }
            }
            if (Coop.IsGuest && !LeavingLocal && _pendingStart is var p && p != null && GameReady)
            {
                _pendingStart = null;
                ApplyStart(p.Value.epoch, p.Value.op, p.Value.mission, p.Value.join);
            }
            if (!Coop.IsHost || Time.unscaledTime < _nextClock) return;
            _nextClock = Time.unscaledTime + 0.5f;
            var w = Coop.Writer();
            w.VarU((ulong)Epoch);
            w.Bool(MissionClock.IsRunning);
            w.F64(MissionClock.TimeAsDouble);
            w.F64(Coop.Session.HostTime);
            w.F32(Time.timeScale);
            Coop.Send(Msg.ClockSync, w, Delivery.Unreliable);
        }

        // ------------------------------------------------------------------ guest side

        void OnMissionStart(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost) return;
            int epoch = (int)r.VarU();
            string op = r.Str(256), mission = r.Str(256);
            bool join = r.Bool();
            if (epoch < Epoch) { CoopLog.Info($"[mission] ignoring stale start epoch {epoch} < {Epoch}"); return; }
            if (!GameReady || LeavingLocal)
            {
                CoopLog.Info($"[mission] {(LeavingLocal ? "still leaving my own mission" : "game still booting")}; will follow host into '{mission}' when ready");
                _pendingStart = (epoch, op, mission, join);
                return;
            }
            ApplyStart(epoch, op, mission, join);
        }

        void ApplyStart(int epoch, string op, string mission, bool join)
        {
            Epoch = epoch; OperationId = op; MissionId = mission; JoinedInProgress = join;
            WorldReady.Unready();
            var opG = FindOperation(op);
            var mG = FindMission(opG, mission);
            if (opG == null || mG == null)
            {
                CoopLog.Error($"[mission] cannot follow host: operation '{op}' / mission '{mission}' not found (different game version?)");
                return;
            }
            CoopLog.Info($"[mission] following host into '{mission}' (epoch {epoch}{(join ? ", joining in progress" : "")})");
            Coop.Apply("start mission", () => MissionManager.Instance.StartOperation(opG, mG));
        }

        void OnTransition(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost) return;
            int epoch = (int)r.VarU();
            var t = (Transition)r.U8();
            if (epoch != Epoch) { CoopLog.Info($"[mission] ignoring {t} for epoch {epoch} (current {Epoch})"); return; }
            var mm = MissionManager.Instance;
            CoopLog.Info($"[mission] following host: {t}");
            WorldReady.Unready();
            Coop.Apply("transition " + t, () =>
            {
                switch (t)
                {
                    case Transition.ReturnToMap: mm.ReturnToMap(); break;
                    case Transition.ReturnToMenu: mm.EndOperationAndReturnToMenu(); break;
                    case Transition.Restart: mm.ReloadCurrentMission(); break;
                    case Transition.Finish: mm.FinishMission(); break;
                }
            });
        }

        void OnClock(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost) return;
            int epoch = (int)r.VarU();
            bool running = r.Bool();
            double hostMission = r.F64(), hostAt = r.F64();
            float timeScale = r.F32();
            if (epoch != Epoch || !running || Phase != MissionManager.GamePhase.MissionActive || !MissionClock.IsRunning) return;
            // Mission clock is scaled time; while the host is paused (timeScale 0) it does not advance.
            double target = hostMission + (Coop.Session.HostTime - hostAt) * timeScale;
            double err = MissionClock.TimeAsDouble - target;
            LastClockError = err;
            if (Math.Abs(err) > 0.05)
            {
                MissionClock.Start(target);
                CoopLog.Debug($"[mission] clock corrected by {-err * 1000:F0} ms to {target:F2}");
            }
        }

        // ------------------------------------------------------------------ lookup

        public static OperationGraph FindOperation(string id)
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<OperationGraph>()))
            {
                var g = o.TryCast<OperationGraph>();
                if (g != null && g.OperationID == id) return g;
            }
            foreach (var o in Resources.LoadAll("", Il2CppType.Of<OperationGraph>()))
            {
                var g = o.TryCast<OperationGraph>();
                if (g != null && g.OperationID == id) return g;
            }
            return null;
        }

        public static MissionGraph FindMission(OperationGraph op, string id)
        {
            if (op != null)
                foreach (var n in op.Missions)
                    if (n != null && n.Mission != null && n.Mission.MissionID == id) return n.Mission;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<MissionGraph>()))
            {
                var g = o.TryCast<MissionGraph>();
                if (g != null && g.MissionID == id) return g;
            }
            return null;
        }

        // ------------------------------------------------------------------ patches

        /// <summary>Guests may only change missions when the host says so.</summary>
        static bool GuestBlocked(string what)
        {
            if (!Coop.IsGuest || Coop.Applying) return false;
            CoopLog.Info($"[mission] blocked local '{what}' on guest (host decides)");
            UI.Notify.Show("Only the host can change missions.");
            return true;
        }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartOperation), typeof(OperationGraph), typeof(MissionGraph), typeof(MissionSaveData))]
        static class StartOperationPatch
        {
            static bool Prefix()
            {
                try { return !GuestBlocked("start mission"); }
                catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Prefix line 257", e); return true; }
            }

            static void Postfix(OperationGraph operation, MissionGraph mission)
            {
                try
                {
                    if (Coop.IsHost) Instance?.SendStart(operation, mission, joinInProgress: false);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Postfix line 259", e); }
            }
        }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.ReturnToMap))]
        static class ReturnToMapPatch
        {
            static bool Prefix()
            {
                try { return !GuestBlocked("return to map"); }
                catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Prefix line 268", e); return true; }
            }
            static void Postfix() { try { if (Coop.IsHost && !_finishing) Instance?.SendTransition(Transition.ReturnToMap); } catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Postfix line 281", e); } }
        }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.EndOperationAndReturnToMenu))]
        static class ReturnToMenuPatch
        {
            static bool Prefix()
            {
                try { return Phase != MissionManager.GamePhase.MissionActive || !GuestBlocked("return to menu"); }
                catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Prefix line 275", e); return true; }
            }
            static void Postfix() { try { if (Coop.IsHost) Instance?.SendTransition(Transition.ReturnToMenu); } catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Postfix line 292", e); } }
        }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.ReloadCurrentMission))]
        static class RestartPatch
        {
            static bool Prefix()
            {
                try { return !GuestBlocked("restart mission"); }
                catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Prefix line 282", e); return true; }
            }
            static void Postfix() { try { if (Coop.IsHost) Instance?.SendTransition(Transition.Restart); } catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Postfix line 303", e); } }
        }

        /// <summary>
        /// FinishMission saves the result and then calls ReturnToMap itself. Guests must get only "Finish": a
        /// ReturnToMap first would unload their mission, and their own FinishMission would then find nothing to
        /// finish (no completion saved in the guest's profile).
        /// </summary>
        static bool _finishing;

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.FinishMission))]
        static class FinishPatch
        {
            static bool Prefix()
            {
                try
                {
                    if (GuestBlocked("finish mission")) return false;
                    _finishing = Coop.IsHost;
                    return true;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Prefix line 296", e); return true; }
            }

            static void Postfix() { try { if (Coop.IsHost) Instance?.SendTransition(Transition.Finish); } catch (System.Exception e) { CoopLog.PatchFailed("MissionFlow.Postfix line 327", e); } }
            static void Finalizer() => _finishing = false;
        }
    }
}
