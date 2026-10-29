using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;
using UnityEngine.Events;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static DieselEngineController Diesel => Object.FindObjectOfType<DieselEngineController>();

        static bool CallsMethod(UnityEventBase ev, Object target, string method)
        {
            if (ev == null) return false;
            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
            {
                var t = ev.GetPersistentTarget(i);
                if (t != null && ev.GetPersistentMethodName(i) == method && (t.Pointer == target.Pointer || (t.TryCast<Component>() is Component c && target.TryCast<Component>() is Component tc && c.gameObject.Pointer == tc.gameObject.Pointer)))
                    return true;
            }
            return false;
        }

        /// <summary>The button wired (in the scene) to DieselEngineController.AttemptIgnition.</summary>
        static LookAtTarget IgnitionButton(DieselEngineController e)
        {
            foreach (var h in Wiring.Callers("AttemptIgnition"))
            {
                var b = h.Owner.TryCast<LookAtTarget>() ?? h.Owner.GetComponent<LookAtTarget>();
                if (b != null) return b;
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), true))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b != null && (CallsMethod(b.onClickDown, e, "AttemptIgnition") || CallsMethod(b.onClickUp, e, "AttemptIgnition"))) return b;
            }
            return null;
        }

        static string EngineDiag(DieselEngineController e) =>
            $"fuel dial {e.fuelMixtureDial?.AccumulatedValue:F3} sys {e._FuelMixtureSystemValue_k__BackingField:F3} target {e.fuelMixtureTarget:F3}±{e.fuelMixtureTolerance:F3} | " +
            $"timing dial {e.injectionTimingDial?.AccumulatedValue:F3} sys {e._InjectionTimingSystemValue_k__BackingField:F3} target {e.injectionTimingTarget:F3}±{e.injectionTimingTolerance:F3} | " +
            $"running {e._EnginesRunning_k__BackingField} warning {e._InWarningState_k__BackingField} balanced {e._BothInBalance_k__BackingField} cooldown {e._ignitionCooldownRemaining:F1}";

        /// <summary>
        /// Guest: start the diesel engine like a player, steering each dial by the (host-mirrored) system value
        /// until it sits on its target, then pressing ignition. Returns when running or after the timeout.
        /// </summary>
        static IEnumerable<object> StartEngineByHand(Bot bot)
        {
            var e = Diesel;
            if (e == null) { bot.Check(false, "mission has a diesel engine"); yield break; }
            bot.Note("engine before: " + EngineDiag(e));
            SliderEnergyMomentumSpinner crank = null;
            foreach (var h in Wiring.Callers("AttemptIgnition")) crank ??= h.Owner.TryCast<SliderEnergyMomentumSpinner>();
            var btn = crank == null ? IgnitionButton(e) : null;
            bot.Check(crank != null || btn != null, $"found the ignition control ({(crank != null ? "crank " + crank.name : btn?.name)})");
            if (crank != null) bot.Note($"crank: energy range {crank.energyRangeMin:F2}..{crank.energyRangeMax:F2} of {crank.maxEnergy:F2}, {crank.energyPerValue:F2}/value, needs drag {crank.requireDraggingToAddEnergy}, slider {crank.sliderSource?.name}");
            double deadline = Time.unscaledTimeAsDouble + 120;
            while (!e._EnginesRunning_k__BackingField && Time.unscaledTimeAsDouble < deadline)
            {
                // steer both dials onto their targets (drift moves them; the host's sweep brings that back here)
                for (int round = 0; round < 8; round++)
                {
                    float fErr = e.fuelMixtureTarget - e._FuelMixtureSystemValue_k__BackingField;
                    float tErr = e.injectionTimingTarget - e._InjectionTimingSystemValue_k__BackingField;
                    if (Mathf.Abs(fErr) < e.fuelMixtureTolerance * 0.4f && Mathf.Abs(tErr) < e.injectionTimingTolerance * 0.4f) break;
                    if (Mathf.Abs(fErr) >= e.fuelMixtureTolerance * 0.4f) foreach (var x in DragDial(bot, e.fuelMixtureDial, e.fuelMixtureDial.AccumulatedValue + fErr)) yield return x;
                    if (Mathf.Abs(tErr) >= e.injectionTimingTolerance * 0.4f) foreach (var x in DragDial(bot, e.injectionTimingDial, e.injectionTimingDial.AccumulatedValue + tErr)) yield return x;
                    foreach (var x in bot.Wait(0.6)) yield return x;   // host simulates, mirror comes back
                }
                bot.Note("engine dials set: " + EngineDiag(e));
                foreach (var x in bot.Until(() => e._ignitionCooldownRemaining <= 0, 15, null, soft: true)) yield return x;
                if (crank != null)
                {
                    // pump the crank: quick upward strokes while dragging, let it spring back in between
                    var sl = crank.sliderSource;
                    for (int stroke = 0; stroke < 12 && !e._EnginesRunning_k__BackingField; stroke++)
                    {
                        // a stroke as a hand does it (DragSlider holds the slider's cursor-driven Update, like a remote hand)
                        foreach (var x in DragSlider(bot, sl, Mathf.Max(sl.Value, sl.maxOutputValue))) yield return x;
                        foreach (var x in bot.Wait(0.5)) yield return x;
                    }
                    bot.Note($"after cranking: local crank energy {crank.energy:F2}");
                }
                else if (btn != null) foreach (var x in Press(bot, btn)) yield return x;
                else yield break;
                foreach (var x in bot.Until(() => e._EnginesRunning_k__BackingField, 6, null, soft: true)) yield return x;
                bot.Note("after ignition: " + EngineDiag(e));
            }
        }

        static IEnumerable<object> EngineHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Diesel != null && Turret != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var e = Diesel;
            bot.Note("host engine at start: " + EngineDiag(e));
            bot.Note("AttemptIgnition is wired from: " + Wiring.Describe(Wiring.Callers("AttemptIgnition")));
            if (e._EnginesRunning_k__BackingField)
            {
                bot.Note("engine already running at mission start: shutting it down so the guest can start it");
                e.ShutdownEngine();
                foreach (var x in bot.Wait(2)) yield return x;
                bot.Check(!e._EnginesRunning_k__BackingField, "host engine stopped for the test");
            }
            bot.Signal("ready");
            foreach (var x in bot.Until(() => e._EnginesRunning_k__BackingField, 150, "guest started the host's engine")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Note("host engine running: " + EngineDiag(e));
            bot.Signal("engine", Machinery.Instance.EngineLine);
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> EngineGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Diesel != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(!Diesel._EnginesRunning_k__BackingField, "guest sees the host's engine stopped");
            bot.Check(Machinery.Instance.Transitions >= 1, $"guest played the shutdown ({Machinery.Instance.Transitions} transitions)");
            foreach (var x in StartEngineByHand(bot)) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("engine"), 60, "host engine state")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            bot.Check(Machinery.Instance.EngineLine == bot.SignalValue("engine"), $"guest engine shows the host's ({Machinery.Instance.EngineLine} vs {bot.SignalValue("engine")})");
            bot.Check(Machinery.Instance.Transitions >= 2, $"guest played the engine's transition events ({Machinery.Instance.Transitions})");
            bot.Note($"engine updates received {Machinery.Instance.EngineUpdates}");
            foreach (var x in bot.Shot("guest-engine")) yield return x;
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
