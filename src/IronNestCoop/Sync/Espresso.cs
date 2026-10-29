using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Syncs the espresso machine. The can and cup are ordinary items (Items puts them in the machine's slots on every
    /// machine), the dials are ordinary controls, and the brew button is a forwarded press. The brewing simulation
    /// (noise and random spikes) runs on the host. Guests show its state, temperature, pressure and timer, receive
    /// the finished cup with the host's scores, and see the grounds can run down. Whoever drinks a cup empties it
    /// for everyone.
    /// </summary>
    public sealed class Espresso : SyncModule
    {
        public override string Name => "espresso";
        public static Espresso Instance { get; private set; }

        enum Ev : byte { State = 1, Fill = 2, Can = 3, CupEmpty = 4, DrinkStart = 5 }

        EspressoBrewingController _m;
        int _sceneSig;
        float _nextSend;
        int _lastState = -1, _lastCanUses = int.MinValue;
        bool _have; int _hState; float _hTemp, _hPress, _hElapsed, _hScore;

        public int Fills, StatesApplied, EffectsReplayed, DrinksShown;

        public Espresso() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Espresso, OnEvent);
        int _haveEpoch = -1;
        public override void OnSessionEnd() => OnSessionStart();
        public override void OnSessionStart() { _drinks.Clear(); _localDrinks.Clear(); _localDrinkers.Clear(); _have = false; _haveEpoch = -1; _lastState = -1; _lastCanUses = int.MinValue; }

        float _retryFind;

        EspressoBrewingController Machine()
        {
            int sig = UnityEngine.SceneManagement.SceneManager.sceneCount;
            // Search the scene only when the scenes changed, or every 2 s while the machine is missing (not every frame)
            if (sig != _sceneSig || (_m == null && Time.unscaledTime >= _retryFind))
            { _sceneSig = sig; _m = Object.FindObjectOfType<EspressoBrewingController>(); _retryFind = Time.unscaledTime + 2f; }
            return _m;
        }

        // ------------------------------------------------------------------ a drink, seen by the others

        // The drinker's game lifts the cup to their camera, tilts it and puts it back (EspressoCupDrinker). The others
        // used to see only the cup turn empty. Now they see it go to the drinker's figure and back, with the same timings.
        sealed class Drink { public Transform Cup; public System.IntPtr Item; public byte Slot; public Vector3 LocalPos; public Quaternion LocalRot; public Vector3 FromWorld; public Quaternion FromRot; public float T0, In, Hold, Out; public Vector3 Tilt; }
        static readonly List<Drink> _drinks = new();
        static readonly HashSet<System.IntPtr> _localDrinks = new();   // this player's own cup while their game animates it

        /// <summary>True for a cup being drunk, either here or shown for another player. The item sync leaves such a cup alone.</summary>
        public static bool InDrink(DraggableItem d) => d != null && (_localDrinks.Contains(d.Pointer) || _drinks.Exists(x => x.Item == d.Pointer));

        void StartRemoteDrink(EspressoCup cup, byte slot)
        {
            var drinker = cup.GetComponentInParent<EspressoCupDrinker>() ?? cup.GetComponentInChildren<EspressoCupDrinker>();
            var item = cup.GetComponentInParent<DraggableItem>();
            var t = item != null ? item.transform : cup.transform;
            if (_drinks.Exists(x => x.Cup == t)) return;
            static float D(float v, float dflt) => float.IsFinite(v) && v > 0.05f && v < 10f ? v : dflt;
            _drinks.Add(new Drink
            {
                Cup = t, Item = item != null ? item.Pointer : System.IntPtr.Zero, Slot = slot,
                LocalPos = t.localPosition, LocalRot = t.localRotation, FromWorld = t.position, FromRot = t.rotation, T0 = Time.time,
                In = D(drinker != null ? drinker.animateInDuration : 0, 0.5f), Hold = D(drinker != null ? drinker.drinkHoldDuration : 0, 1.2f),
                Out = D(drinker != null ? drinker.animateOutDuration : 0, 0.5f), Tilt = drinker != null ? drinker.drinkTiltOffset : new Vector3(45, 0, 0),
            });
            DrinksShown++;
            CoopLog.Debug($"[espresso] player {slot + 1} drinks {cup.name}: shown at their figure");
        }

        void TickDrinks()
        {
            _localDrinks.RemoveWhere(p => !_localDrinkers.TryGetValue(p, out var dr) || dr == null || !dr._isAnimating);
            for (int i = _drinks.Count - 1; i >= 0; i--)
            {
                var d = _drinks[i];
                if (d.Cup == null) { _drinks.RemoveAt(i); continue; }
                float e = Time.time - d.T0, total = d.In + d.Hold + d.Out;
                if (e >= total || !Avatars.Instance.TryGetHead(d.Slot, out var head, out var look))
                {
                    d.Cup.localPosition = d.LocalPos; d.Cup.localRotation = d.LocalRot;   // puts it back exactly where it was
                    _drinks.RemoveAt(i); continue;
                }
                var to = head + look * new Vector3(0.04f, -0.1f, 0.22f);   // in front of the mouth
                var tilted = look * Quaternion.Euler(d.Tilt);
                float k; Quaternion rot;
                if (e < d.In) { k = Mathf.SmoothStep(0, 1, e / d.In); rot = Quaternion.Slerp(d.FromRot, look, k); }
                else if (e < d.In + d.Hold) { k = 1; float h = (e - d.In) / d.Hold; rot = Quaternion.Slerp(look, tilted, Mathf.Sin(h * Mathf.PI)); }
                else { k = 1 - Mathf.SmoothStep(0, 1, (e - d.In - d.Hold) / d.Out); rot = Quaternion.Slerp(d.FromRot, look, k); }
                d.Cup.position = Vector3.Lerp(d.FromWorld, to, k);
                d.Cup.rotation = rot;
            }
        }

        static readonly Dictionary<System.IntPtr, EspressoCupDrinker> _localDrinkers = new();

        /// <summary>Called when this player starts drinking. The others are told, so they can show it at this player's figure.</summary>
        [HarmonyLib.HarmonyPatch(typeof(EspressoCupDrinker), nameof(EspressoCupDrinker.DrinkCoffee))]
        static class DrinkPatch
        {
            static void Postfix(EspressoCupDrinker __instance)
            {
                try
                {
                    if (!Coop.InSession || Coop.Applying || __instance == null || !__instance._isAnimating) return;
                    var cup = __instance._cup ?? __instance.GetComponentInParent<EspressoCup>() ?? __instance.GetComponentInChildren<EspressoCup>();
                    if (cup == null) return;
                    var item = cup.GetComponentInParent<DraggableItem>();
                    if (item != null) { _localDrinks.Add(item.Pointer); _localDrinkers[item.Pointer] = __instance; }
                    var w = Begin(Ev.DrinkStart); w.Str(ItemId.Of(cup)); Coop.Send(Msg.Espresso, w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Espresso.DrinkPatch", e); }
            }
        }

        static NetWriter Begin(Ev ev) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); return w; }

        // Drinking empties the cup without a call we can catch (EspressoCupDrinker sets it directly), so watch the
        // cups instead. A cup that went from full to empty here, and not because another player drank it, was
        // drunk here.
        readonly Dictionary<System.IntPtr, bool> _wasFull = new();
        static readonly HashSet<System.IntPtr> _emptiedRemotely = new();
        public int DrinksHeard;   // for the tests: counts remote drinks whose sounds played here
        readonly List<EspressoCup> _cups = new();
        float _nextCupScan, _nextCupCheck;

        void WatchCups()
        {
            if ((Time.unscaledTime >= _nextCupScan || _cups.Exists(c => c == null)) && Perf.HeavyFrame())
            {
                _nextCupScan = Time.unscaledTime + 10f;   // new cups are rare, so scan seldom (the drink itself is checked every 0.25 s)
                _cups.Clear();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<EspressoCup>(), true))
                { var c = o.TryCast<EspressoCup>(); if (c != null) _cups.Add(c); }
            }
            if (Time.unscaledTime < _nextCupCheck) return;
            _nextCupCheck = Time.unscaledTime + 0.25f;
            foreach (var cup in _cups)
            {
                if (cup == null) continue;
                bool full = cup.isFull;
                if (_wasFull.TryGetValue(cup.Pointer, out var was) && was && !full)
                {
                    if (_emptiedRemotely.Remove(cup.Pointer)) { }
                    else
                    {
                        var w = Begin(Ev.CupEmpty); w.Str(ItemId.Of(cup)); Coop.Send(Msg.Espresso, w);
                        CoopLog.Debug($"[espresso] drank {ItemId.Of(cup)}: emptied for everyone");
                    }
                }
                _wasFull[cup.Pointer] = full;
            }
        }

        public override void Tick(float dt)
        {
            TickDrinks();
            if (Coop.InSession) WatchCups();
            if (!Coop.IsHost || !Coop.InSession) return;
            var m = Machine();
            if (m == null || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 0.2f;
            int st = (int)m.currentState;
            SendState(m, st != _lastState ? Delivery.Reliable : Delivery.Unreliable, null);
            _lastState = st;
            var can = m._loadedCan;
            int uses = can != null ? can.remainingUses : int.MinValue;
            if (can != null && uses != _lastCanUses)
            {
                _lastCanUses = uses;
                var w = Begin(Ev.Can); w.Str(ItemId.Of(can)); w.VarI(uses); WorldReady.SendToReady(Msg.Espresso, w);
            }
        }

        void SendState(EspressoBrewingController m, Delivery d, byte? slot)
        {
            var w = Begin(Ev.State);
            w.VarI((int)m.currentState); w.F32(m.simTemperature); w.F32(m.simPressure); w.F32(m.brewElapsedSeconds); w.F32(m.runningDialScore);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Espresso, w, d, one);
            else WorldReady.SendToReady(Msg.Espresso, w, d);
        }

        public override void SendSnapshot(byte slot)
        {
            var m = Machine();
            if (m != null) SendState(m, Delivery.Reliable, slot);
        }

        void OnEvent(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            if (!(WorldReady.LocalReady || Coop.IsHost)) return;
            var ev = (Ev)r.U8();
            var m = Machine();
            switch (ev)
            {
                case Ev.State:
                    if (!c.FromHost) return;
                    _hState = (int)r.VarI(); _hTemp = r.F32(); _hPress = r.F32(); _hElapsed = r.F32(); _hScore = r.F32();
                    _have = true; _haveEpoch = MissionFlow.Instance.Epoch;
                    break;
                case Ev.Fill:
                {
                    if (!c.FromHost) return;
                    float p = r.F32(), t = r.F32(), timing = r.F32(), quality = r.F32();
                    if (m != null) { m.finalQuality = quality; Coop.Apply("espresso fill", () => m.FillCupInSlot(p, t, timing)); Fills++; }
                    break;
                }
                case Ev.Can:
                {
                    if (!c.FromHost) return;
                    var can = ItemId.Find<CoffeeGroundsCan>(r.Str(2048)); int uses = (int)r.VarI();
                    if (can != null) can.remainingUses = uses;
                    break;
                }
                case Ev.DrinkStart:
                {
                    if (c.FromSlot == Coop.Session.LocalSlot) return;
                    string cupId = r.Str(2048);
                    var cup = ItemId.Find<EspressoCup>(cupId);
                    if (cup != null) StartRemoteDrink(cup, c.FromSlot);
                    return;
                }
                case Ev.CupEmpty:
                {
                    string cupId = r.Str(2048);
                    var cup = ItemId.Find<EspressoCup>(cupId);
                    CoopLog.Debug($"[espresso] slot {c.FromSlot} drank {cupId}: {(cup == null ? "not found here" : cup.isFull ? "emptied here" : "already empty here")}");
                    if (cup != null && cup.isFull)
                    {
                        _emptiedRemotely.Add(cup.Pointer);
                        // What the others hear of a drink (the slurp) comes only from the sounds and effects of the game's own
                        // drink events. Their other listeners (the drinker's own view, the empty-cup logic) are skipped.
                        var drinker = cup.GetComponentInParent<EspressoCupDrinker>() ?? cup.GetComponentInChildren<EspressoCupDrinker>();
                        bool heard = drinker != null && (Controls.ReplayEffects(drinker.OnDrinkStarted, "drink sounds") | Controls.ReplayEffects(drinker.OnDrinkEmptied, "drink sounds"));
                        if (!heard) CoopLog.Debug("[espresso] this cup's drink has no sounds to replay");
                        Coop.Apply("cup emptied", () => cup.MarkEmpty());
                        DrinksHeard += heard ? 1 : 0;
                    }
                    break;
                }
            }
        }

        /// <summary>Guest only: shows the host's brew instead of simulating it.</summary>
        void ApplyHost(EspressoBrewingController m)
        {
            if (!_have || _haveEpoch != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;   // only accept a brew from this mission
            if ((int)m.currentState != _hState)
            {
                var target = (EspressoBrewingController.BrewState)_hState;
                Coop.Apply("espresso state", () => m.SetState(target)); StatesApplied++;
                // The brew runs on the host, so its start and finish (the steam over the cup, their sounds) never fire
                // here on their own. We play only their effects; the rest of those events is the host's logic.
                if (target == EspressoBrewingController.BrewState.Brewing) { if (Controls.ReplayEffects(m.OnBrewStarted, "brew effects", () => m.OnBrewStarted.Invoke(0f))) EffectsReplayed++; }
                else if (target == EspressoBrewingController.BrewState.Complete) { if (Controls.ReplayEffects(m.OnBrewComplete, "brew effects", () => m.OnBrewComplete.Invoke(m.finalQuality))) EffectsReplayed++; }
            }
            m.simTemperature = _hTemp; m.simPressure = _hPress; m.brewElapsedSeconds = _hElapsed; m.runningDialScore = _hScore;
            Coop.Apply("espresso gauges", () =>
            {
                m.ComputeMappedValues();
                m.WriteGaugeOutputs();
                if (_hState == (int)EspressoBrewingController.BrewState.Brewing) m.WriteTimerDial(_hElapsed);
            });
        }

        /// <summary>Test hook that returns the brew status as "state|temp|pressure".</summary>
        public string Line { get { var m = Machine(); return m == null ? "no machine" : $"{m.currentState}|{m.simTemperature:F1}|{m.simPressure:F1}"; } }

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(EspressoBrewingController), "Update")]
        static class Sim
        {
            static bool Prefix(EspressoBrewingController __instance)
            {
                try
                {
                    if (!GraphGate.Suppress) return true;
                    Instance?.ApplyHost(__instance);
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Espresso.Prefix line 182", e); return !GraphGate.Suppress; }   // a guest never runs its own brew simulation
            }
        }

        [HarmonyPatch(typeof(EspressoBrewingController), "FillCupInSlot")]
        static class Fill
        {
            static void Postfix(EspressoBrewingController __instance, float pressureScorePct, float temperatureScorePct, float timingScorePct)
            {
                try
                {
                    if (!Coop.HostEmits) return;
                    var w = Begin(Ev.Fill); w.F32(pressureScorePct); w.F32(temperatureScorePct); w.F32(timingScorePct); w.F32(__instance.finalQuality);
                    WorldReady.SendToReady(Msg.Espresso, w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Espresso.Postfix line 193", e); }
            }
        }

    }
}
