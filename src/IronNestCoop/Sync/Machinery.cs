using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Diesel engine and hydraulic pressure. The engine's simulation (random drift of fuel mixture and injection
    /// timing, the warning countdown, shutdowns) runs on the host only; guests mirror its state 5 times a second and play the
    /// same start/stop/warning/balance events, so sounds, lights and gauges match. The two engine dials and every
    /// valve dial are ordinary controls (Controls module): whoever turns them drives them, and the host's drift
    /// comes back through the host sweep. A guest's ignition press is executed on the host. Random valve damage
    /// is decided by the host only.
    /// </summary>
    public sealed class Machinery : SyncModule
    {
        public override string Name => "machinery";
        public static Machinery Instance { get; private set; }

        enum Ev : byte { Engine = 1, Ignition = 2 }

        struct EngineState
        {
            public bool Running, Warning, Balanced;
            public float Countdown, FuelSys, TimingSys, FuelDrift, TimingDrift, Cooldown;
            public bool SameFlags(EngineState o) => Running == o.Running && Warning == o.Warning && Balanced == o.Balanced;
        }

        DieselEngineController _engine;
        int _sceneSig;
        float _nextSend;
        EngineState _lastSent, _host;
        bool _haveHost, _applied;
        EngineState _shown;   // guest: what the local engine currently shows (for transition events)

        public int EngineUpdates, Transitions;

        public Machinery() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Machinery, OnEvent);

        int _haveEpoch = -1;
        public override void OnSessionStart() { _haveHost = _applied = false; _haveEpoch = -1; }
        public override void OnSessionEnd() => OnSessionStart();

        DieselEngineController Engine()
        {
            int sig = UnityEngine.SceneManagement.SceneManager.sceneCount;
            if (_engine == null || sig != _sceneSig) { _sceneSig = sig; _engine = Object.FindObjectOfType<DieselEngineController>(); }
            return _engine;
        }

        static EngineState? Read(DieselEngineController e) => e == null ? null : new EngineState
        {
            Running = e._EnginesRunning_k__BackingField, Warning = e._InWarningState_k__BackingField, Balanced = e._BothInBalance_k__BackingField,
            Countdown = e._WarningCountdownRemaining_k__BackingField, FuelSys = e._FuelMixtureSystemValue_k__BackingField,
            TimingSys = e._InjectionTimingSystemValue_k__BackingField, FuelDrift = e._fuelDriftOffset, TimingDrift = e._timingDriftOffset,
            Cooldown = e._ignitionCooldownRemaining,
        };

        /// <summary>Test hook: "running/warning/balanced" of the local engine.</summary>
        public string EngineLine
        {
            get { var e = Engine(); return e == null ? "no engine" : $"running {e._EnginesRunning_k__BackingField} warning {e._InWarningState_k__BackingField} balanced {e._BothInBalance_k__BackingField}"; }
        }

        static NetWriter Begin(Ev ev) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); return w; }

        public override void Tick(float dt)
        {
            if (!Coop.IsHost || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 0.2f;
            var st = Read(Engine());
            if (st == null) return;
            var s = st.Value;
            bool flip = !s.SameFlags(_lastSent);
            _lastSent = s;
            Send(s, flip ? Delivery.Reliable : Delivery.Unreliable, null);
        }

        void Send(EngineState s, Delivery d, byte? slot)
        {
            var w = Begin(Ev.Engine);
            w.Bool(s.Running); w.Bool(s.Warning); w.Bool(s.Balanced);
            w.F32(s.Countdown); w.F32(s.FuelSys); w.F32(s.TimingSys); w.F32(s.FuelDrift); w.F32(s.TimingDrift); w.F32(s.Cooldown);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Machinery, w, d, one);
            else WorldReady.SendToReady(Msg.Machinery, w, d);
        }

        public override void SendSnapshot(byte slot)
        {
            var st = Read(Engine());
            if (st != null) Send(st.Value, Delivery.Reliable, slot);
        }

        void OnEvent(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            switch ((Ev)r.U8())
            {
                case Ev.Engine:
                    _host = new EngineState
                    {
                        Running = r.Bool(), Warning = r.Bool(), Balanced = r.Bool(),
                        Countdown = r.F32(), FuelSys = r.F32(), TimingSys = r.F32(), FuelDrift = r.F32(), TimingDrift = r.F32(), Cooldown = r.F32(),
                    };
                    _haveHost = true; _haveEpoch = MissionFlow.Instance.Epoch; EngineUpdates++;
                    break;
                case Ev.Ignition:
                {
                    bool ok = r.Bool();
                    var e = Engine();
                    // The press itself ran on the host; play its feedback here (success follows via the Running flip).
                    if (e != null && !ok) Coop.Apply("ignition failed", () => e.OnEngineStartFailure?.Invoke());
                    break;
                }
            }
        }

        /// <summary>Guest: show the host's engine instead of simulating it, with the same transition events.</summary>
        void ApplyHost(DieselEngineController e)
        {
            if (!_haveHost || _haveEpoch != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;   // this mission's engine only
            var h = _host;
            e._EnginesRunning_k__BackingField = h.Running; e._InWarningState_k__BackingField = h.Warning; e._BothInBalance_k__BackingField = h.Balanced;
            e._WarningCountdownRemaining_k__BackingField = h.Countdown; e._FuelMixtureSystemValue_k__BackingField = h.FuelSys;
            e._InjectionTimingSystemValue_k__BackingField = h.TimingSys; e._fuelDriftOffset = h.FuelDrift; e._timingDriftOffset = h.TimingDrift;
            e._ignitionCooldownRemaining = h.Cooldown;
            var was = _applied ? _shown : (Read(e) ?? h);
            if (_applied && !was.SameFlags(h))
            {
                Transitions++;
                Coop.Apply("engine events", () =>
                {
                    if (!was.Running && h.Running) e.OnEngineStartSuccess?.Invoke();
                    if (was.Running && !h.Running) e.OnEngineShutdown?.Invoke();
                    if (!was.Warning && h.Warning) e.OnEnterWarning?.Invoke();
                    if (was.Warning && !h.Warning) e.OnExitWarning?.Invoke();
                    if (!was.Balanced && h.Balanced) e.OnBothValuesInBalance?.Invoke();
                    if (was.Balanced && !h.Balanced) e.OnValuesOutOfBalance?.Invoke();
                });
            }
            else if (!_applied && h.Running) Coop.Apply("engine events", () => e.OnEngineStartSuccess?.Invoke());   // joined while running
            _shown = h; _applied = true;
        }

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(DieselEngineController), "Update")]
        static class EngineUpdate
        {
            static bool Prefix(DieselEngineController __instance)
            {
                try
                {
                    if (!GraphGate.Suppress) return true;
                    Instance?.ApplyHost(__instance);
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Machinery.Prefix line 155", e); return !GraphGate.Suppress; }   // a guest never runs its own engine
            }
        }

        [HarmonyPatch(typeof(DieselEngineController), nameof(DieselEngineController.AttemptIgnition))]
        static class Ignition
        {
            static bool Prefix() => !GraphGate.Suppress;   // a guest's press is forwarded by Controls and runs on the host

            static void Postfix(DieselEngineController __instance)
            {
                try
                {
                    if (!Coop.HostEmits) return;
                    var w = Begin(Ev.Ignition); w.Bool(__instance._EnginesRunning_k__BackingField);
                    WorldReady.SendToReady(Msg.Machinery, w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Machinery.Postfix line 168", e); }
            }
        }

        [HarmonyPatch(typeof(HighPressureSystemManager), nameof(HighPressureSystemManager.DamageRandomValve))]
        static class ValveDamage { static bool Prefix() => !GraphGate.Suppress; }   // damage is random and decided on the host; the valve dial reaches guests via Controls

        // Buttons carry "valve breakers" (ValveAutoAddDamageOnEnable) that damage a random valve straight through
        // ValveController with their own dice: on a guest that broke a valve nobody else had (steam only there).
        // A valve's damage is its dial position, which comes from the host; guests never damage valves themselves.
        [HarmonyPatch(typeof(ValveController), nameof(ValveController.SetDamage01))]
        static class ValveSetDamage { static bool Prefix() => !GraphGate.Suppress; }

        [HarmonyPatch(typeof(ValveController), nameof(ValveController.DamageValve))]
        static class ValveDamageValve { static bool Prefix() => !GraphGate.Suppress; }
    }
}
