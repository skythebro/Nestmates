using System;
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
    /// Syncs every in-world control. For dials and sliders, the player who is dragging owns the value and streams it
    /// (20 times a second while dragging, reliably on release). Values the host's simulation changes by itself
    /// (back-driven dials, engine drift) are sent by the host. For buttons (LookAtTarget), a guest's press is
    /// forwarded to the host, which presses it for real, so firing, reload steps and malfunctions are decided
    /// once. The press animation is shown everywhere.
    /// </summary>
    public sealed class Controls : SyncModule
    {
        public override string Name => "controls";
        public static Controls Instance { get; private set; }

        enum Kind : byte { Dial = 1, Slider = 2 }
        const float Eps = 0.0005f;
        static readonly int IsClicked = Animator.StringToHash("IsClicked");

        sealed class Entry
        {
            public string Id;
            public Kind Kind;
            public DialInteractable Dial;
            public LinearSliderInteractable Slider;
            public float LastSent = float.NaN, LastApplied = float.NaN, LastSentAt;
            public bool WasDragging;
            public byte RemoteDragger = 255;
            public float RemoteDragUntil;
            public bool ReplayingDrag;   // we began a drag here on behalf of a remote player's hand
            public bool Volume;          // the record player's volume dial (a host rule decides whether it is shared or each player's own)
        }

        static bool Synced(Entry e) => !e.Volume || HostRules.On(Rule.SharedRecordVolume);

        readonly Dictionary<string, Entry> _byId = new();
        readonly Dictionary<IntPtr, Entry> _byPtr = new();
        readonly Dictionary<IntPtr, string> _buttonIds = new();
        readonly Dictionary<string, LookAtTarget> _buttons = new();
        float _nextScan, _nextSend, _nextHostSweep;
        int _sceneSig;

        /// <summary>Counters used by the tests.</summary>
        public int ValuesApplied, ClicksForwarded, ClicksExecuted, HoversApplied;

        /// <summary>Host only: who pressed the button whose press is executing right now (0 means the host itself).</summary>
        public static byte PressingSlot { get; private set; }

        public Controls() { Instance = this; HostRules.Changed += OnRulesChanged; }

        void OnRulesChanged(uint was, uint now)
        {
            if (!Coop.InSession) return;
            // the volume became shared, so the host's dial is the one everyone hears
            if (HostRules.Flipped(was, now, Rule.SharedRecordVolume) && HostRules.On(Rule.SharedRecordVolume) && Coop.IsHost)
                foreach (var e in _byId.Values) if (e.Volume && Alive(e)) Send(e, ValueOf(e), final: true);
            if (HostRules.Flipped(was, now, Rule.SharedHover))
            {
                if (HostRules.On(Rule.SharedHover))
                {
                    // whatever we are looking at right now starts showing for the others
                    foreach (var b in _buttons.Values) if (b != null && _localHover.Contains(b.Pointer)) LocalHover(b, true);
                }
                else
                {
                    // the others' hovers end here (ours stay, since we are still looking)
                    foreach (var kv in _remoteHover)
                    {
                        if (kv.Value.Count == 0 || _localHover.Contains(kv.Key)) continue;
                        foreach (var b in _buttons.Values)
                            if (b != null && b.Pointer == kv.Key) { var bb = b; Coop.Apply("hover end", () => bb.OnLookAway()); break; }
                    }
                    _remoteHover.Clear();
                }
            }
        }

        public override void Register(Session s)
        {
            s.On((ushort)Msg.ControlValue, OnValue);
            s.On((ushort)Msg.ControlClick, OnClick);
            s.On((ushort)Msg.ControlHover, OnHover);
        }

        public override void OnSessionEnd()
        {
            foreach (var e in _byId.Values) if (e.ReplayingDrag && Alive(e)) { try { ReplayDrag(e, false); } catch (Exception) { } }
            HeldSliders.Clear(); Replayed.Clear(); FrozenPointer.Clear(); _remoteHover.Clear();
        }

        public override void OnSessionStart() { HeldSliders.Clear(); Replayed.Clear(); FrozenPointer.Clear(); _sceneSig = -1; ValuesApplied = ClicksForwarded = ClicksExecuted = 0; _remoteHover.Clear(); _localHover.Clear(); }

        // ------------------------------------------------------------------ registry

        static int SceneSignature()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        /// <summary>
        /// Dials and sliders that appear mid-mission (such as a punchcard's own console with the magazine selector) are
        /// picked up without a full rescan. Only the dial and slider lists are walked, and buttons wait for a scene change.
        /// </summary>
        static readonly bool NoNewDials = LaunchArgs.Mutant("nonewdials");   // test switch: with it on, the magazine test must fail

        readonly HashSet<IntPtr> _duplicates = new();   // a second dial at an already used path: it is not synced and not looked at again
        bool _slidersNext;

        /// <summary>Searches for dials on one pass and sliders on the next. Each search costs a few ms, so never both in one frame.</summary>
        void AddNewDials()
        {
            if (NoNewDials) return;
            _slidersNext = !_slidersNext;
            if (!_slidersNext)
            {
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LinearSliderInteractable>(), false))
                {
                    var sl = o.TryCast<LinearSliderInteractable>();
                    if (sl != null && !_byPtr.ContainsKey(sl.Pointer) && !_duplicates.Contains(sl.Pointer)) Add(new Entry { Kind = Kind.Slider, Slider = sl }, sl);
                }
                return;
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DialInteractable>(), false))
            {
                var d = o.TryCast<DialInteractable>();
                if (d == null || _byPtr.ContainsKey(d.Pointer) || _duplicates.Contains(d.Pointer)) continue;
                bool vol = d.GetComponentInParent<RecordPlayerVolumeDialBridge>() != null;
                foreach (var b in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RecordPlayerVolumeDialBridge>(), true))
                { var vd = b.TryCast<RecordPlayerVolumeDialBridge>()?.volumeDial; if (vd != null && vd.Pointer == d.Pointer) vol = true; }
                Add(new Entry { Kind = Kind.Dial, Dial = d, Volume = vol }, d);
            }
        }

        void Rescan(bool force = false)
        {
            int sig = SceneSignature();
            if (!force && sig == _sceneSig) { AddNewDials(); return; }
            _sceneSig = sig;
            _before = new Dictionary<string, Entry>(_byId);
            _duplicates.Clear();   // a remote drag in progress carries over the rebuild
            _byId.Clear(); _byPtr.Clear(); _buttonIds.Clear(); _buttons.Clear(); _viewOnly.Clear(); _mapClear.Clear();
            // The record player's volume: the game's master volume does not reach the record player, so when the host
            // makes it each player's own, this dial sets how loud this player hears it.
            var volume = new HashSet<IntPtr>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<RecordPlayerVolumeDialBridge>(), true))
            {
                var vd = o.TryCast<RecordPlayerVolumeDialBridge>()?.volumeDial;
                if (vd != null) volume.Add(vd.Pointer);
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DialInteractable>(), true))
            {
                var d = o.TryCast<DialInteractable>();
                if (d != null) Add(new Entry { Kind = Kind.Dial, Dial = d, Volume = volume.Contains(d.Pointer) }, d);
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LinearSliderInteractable>(), true))
            {
                var sl = o.TryCast<LinearSliderInteractable>();
                if (sl != null) Add(new Entry { Kind = Kind.Slider, Slider = sl }, sl);
            }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), true))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b != null) RegisterButton(b);
            }
            _before = null;
            CoopLog.Info($"[controls] registry: {_byId.Count} dials/sliders, {_buttons.Count} buttons");
        }

        void Add(Entry e, Component c)
        {
            e.Id = IdOf(c, e.Kind == Kind.Dial ? "dial" : "slider");
            if (_byId.TryGetValue(e.Id, out var old) && !Alive(old)) _byId.Remove(e.Id);   // the console respawned
            if (_byId.ContainsKey(e.Id)) { if (_duplicates.Add(c.Pointer)) CoopLog.Debug($"[controls] duplicate id {e.Id}"); return; }
            if (_before != null && _before.TryGetValue(e.Id, out var was) && was.Kind == e.Kind && (was.Dial ?? (Component)was.Slider)?.Pointer == c.Pointer)
            {
                e.LastSent = was.LastSent; e.LastApplied = was.LastApplied; e.LastSentAt = was.LastSentAt; e.WasDragging = was.WasDragging;
                e.RemoteDragger = was.RemoteDragger; e.RemoteDragUntil = was.RemoteDragUntil; e.ReplayingDrag = was.ReplayingDrag;
            }
            _byId[e.Id] = e; _byPtr[c.Pointer] = e;
        }

        Dictionary<string, Entry> _before;
        float _nextPeerScan;

        /// <summary>Called when a peer names a control we do not know. We look again, but at most once a second, because each
        /// look is a full scene search and a peer could name unknown ids on purpose.</summary>
        bool PeerRescan()
        {
            if (Time.unscaledTime < _nextPeerScan) return false;
            _nextPeerScan = Time.unscaledTime + 1f;
            Rescan(true);
            return true;
        }

        static string IdOf(Component c, string kind) => ScenePath.Of(c.transform) + "|" + kind;

        static float ValueOf(Entry e) => e.Kind == Kind.Dial ? e.Dial.AccumulatedValue : e.Slider.Value;
        static bool Dragging(Entry e) => e.Kind == Kind.Dial ? e.Dial.IsDragging : e.Slider.IsDragging;
        /// <summary>True when a local hand is dragging, as opposed to a drag we replay for a remote player.</summary>
        static bool LocalDrag(Entry e) => !e.ReplayingDrag && Dragging(e);

        /// <summary>
        /// Some parts of the game only act while a drag is in progress (the turret reads its rotation and elevation
        /// dials only between begin and end of a drag, and backdrives them otherwise). So a remote drag is replayed
        /// as a real one: it begins on the first streamed value and ends on the final value or when the stream stops.
        /// </summary>
        /// <summary>
        /// Sliders whose cursor-driven Update must not run. During a drag a slider recomputes its value from the
        /// local cursor every frame, which would overwrite a value replayed for a remote hand. The QA bots use this too.
        /// </summary>
        internal static readonly HashSet<IntPtr> HeldSliders = new();

        static void ReplayDrag(Entry e, bool on)
        {
            if (e.ReplayingDrag == on) return;
            e.ReplayingDrag = on;
            if (e.Kind == Kind.Slider) { if (on) HeldSliders.Add(e.Slider.Pointer); else HeldSliders.Remove(e.Slider.Pointer); }
            var comp = e.Kind == Kind.Dial ? (Component)e.Dial : e.Slider;
            if (on) Replayed.Add(comp.Pointer); else { Replayed.Remove(comp.Pointer); FrozenPointer.Remove(comp.Pointer); }
            Coop.Apply("drag " + e.Id, () =>
            {
                _replayingDrag = true;
                try
                {
                    if (e.Kind == Kind.Dial) { if (on) e.Dial.BeginDialDrag(); else e.Dial.EndDialDrag(); }
                    else { if (on) e.Slider.BeginSliderDrag(); else e.Slider.EndSliderDrag(); }
                }
                finally { _replayingDrag = false; }
            });
        }

        /// <summary>
        /// Set while a drag is begun or ended here for a remote hand. The game's begin-drag step also takes the local
        /// player's input (InteractionLockBroker freezes the player while dragging) and clamps the local cursor to the
        /// dial. That is right for the hand that drags but wrong for everyone who only watches, who could not move
        /// while another player scrolled a teleprinter or turned a dial. Those parts are skipped for a replayed drag.
        /// </summary>
        static bool _replayingDrag;
        public static int LocksSkipped, AdoptionsSkipped;   // counted for the tests

        /// <summary>The dials and sliders currently being dragged here on behalf of a remote hand.</summary>
        internal static readonly HashSet<IntPtr> Replayed = new();

        /// <summary>
        /// The game's cursor takes over any drag already in progress on what it hovers (TryAdoptExternalActiveDrag).
        /// A replayed remote drag looked like one, so a player who only hovered a dial or slider that another player
        /// was turning would grab it (showing the grab cursor and tugging the control toward where they aimed).
        /// This must not happen for replays.
        /// </summary>
        [HarmonyPatch(typeof(DynamicCursorManager), "TryAdoptExternalActiveDrag")]
        static class NoAdoptReplay
        {
            static bool Prefix(DynamicCursorManager __instance)
            {
                try
                {
                    if (Replayed.Count == 0 || LaunchArgs.Mutant("adoptreplay")) return true;
                    var hover = __instance._currentHover;
                    if (hover == null) return true;
                    Component c = hover.GetComponent<DialInteractable>() ?? hover.GetComponentInParent<DialInteractable>();
                    c ??= (Component)hover.GetComponent<LinearSliderInteractable>() ?? hover.GetComponentInParent<LinearSliderInteractable>();
                    if (c == null || !Replayed.Contains(c.Pointer)) return true;
                    AdoptionsSkipped++;
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.NoAdoptReplay", e); return true; }
            }
        }

        [HarmonyPatch(typeof(DialInteractable), "TryAcquireBrokerDragLockIfNeeded")]
        static class DialLock
        {
            static bool Prefix()
            {
                try { if (!_replayingDrag || LaunchArgs.Mutant("replaylock")) return true; LocksSkipped++; return false; }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.DialLock", e); return true; }
            }
        }

        [HarmonyPatch(typeof(LinearSliderInteractable), "TryAcquireBrokerDragLockIfNeeded")]
        static class SliderLock
        {
            static bool Prefix()
            {
                try { if (!_replayingDrag || LaunchArgs.Mutant("replaylock")) return true; LocksSkipped++; return false; }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.SliderLock", e); return true; }
            }
        }

        /// <summary>Keeps the local cursor under the local player's control, while a clamp on their own drag stays in place.</summary>
        [HarmonyPatch(typeof(DynamicCursorManager), nameof(DynamicCursorManager.ClampCursorToValve))]
        static class CursorClamp
        {
            static bool Prefix()
            {
                try { return !_replayingDrag; }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.CursorClamp", e); return true; }
            }
        }

        [HarmonyPatch(typeof(DynamicCursorManager), nameof(DynamicCursorManager.DisableValveClamping))]
        static class CursorUnclamp
        {
            static bool Prefix()
            {
                try { return !_replayingDrag; }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.CursorUnclamp", e); return true; }
            }
        }
        static bool Alive(Entry e) => e.Kind == Kind.Dial ? e.Dial != null : e.Slider != null;

        public bool TryGetValue(string id, out float v)
        {
            v = 0;
            if (!_byId.TryGetValue(id, out var e) || !Alive(e)) return false;
            v = ValueOf(e); return true;
        }

        public IEnumerable<string> DialIds => _byId.Keys;
        public int ButtonCount => _buttons.Count;

        // ------------------------------------------------------------------ sending

        public override void Tick(float dt)
        {
            if (Time.unscaledTime >= _nextScan && Perf.HeavyFrame()) { _nextScan = Time.unscaledTime + 2f; Rescan(); }
            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 0.05f;
            bool hostSweep = Coop.IsHost && Time.unscaledTime >= _nextHostSweep;
            if (hostSweep) _nextHostSweep = Time.unscaledTime + 0.1f;
            foreach (var e in _byId.Values)
            {
                if (!Alive(e)) continue;
                // a replayed drag ends even if the control stopped being synced in the meantime (a host rule changed)
                if (e.ReplayingDrag && (Time.unscaledTime > e.RemoteDragUntil || !Synced(e))) { ReplayDrag(e, false); e.WasDragging = false; }
                if (!Synced(e)) { e.WasDragging = LocalDrag(e); e.LastSent = float.NaN; continue; }
                bool dragging = LocalDrag(e);
                float v = ValueOf(e);
                if (dragging)
                {
                    if (float.IsNaN(e.LastSent) || Mathf.Abs(v - e.LastSent) > Eps || Time.unscaledTime - e.LastSentAt > 0.25f) Send(e, v, final: false); // the keepalive holds the remote drag open
                }
                else if (e.WasDragging) Send(e, v, final: true);     // on release: send the final value reliably
                else if (hostSweep && e.RemoteDragger == 255 && !float.IsNaN(e.LastSent) && Mathf.Abs(v - e.LastSent) > Eps
                         && (float.IsNaN(e.LastApplied) || Mathf.Abs(v - e.LastApplied) > Eps))
                    Send(e, v, final: true);                         // the host simulation moved it
                else if (hostSweep && float.IsNaN(e.LastSent)) e.LastSent = v; // just record a baseline, nothing to send
                e.WasDragging = dragging;
                if (e.RemoteDragger != 255 && Time.unscaledTime > e.RemoteDragUntil) e.RemoteDragger = 255;
            }
        }

        void Send(Entry e, float v, bool final)
        {
            e.LastSent = v; e.LastSentAt = Time.unscaledTime;
            if (final) CoopLog.Debug($"[controls] sent {e.Id} = {v:F3}{(final ? " (final)" : "")}");
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch); w.Str(e.Id); w.U8((byte)e.Kind); w.F32(v); w.Bool(final);
            if (Coop.IsHost) WorldReady.SendToReady(Msg.ControlValue, w, final ? Delivery.Reliable : Delivery.Unreliable);
            else Coop.Send(Msg.ControlValue, w, final ? Delivery.Reliable : Delivery.Unreliable);
        }

        public override void SendSnapshot(byte slot)
        {
            Rescan(true);
            foreach (var e in _byId.Values)
            {
                if (!Alive(e) || !Synced(e)) continue;
                var w = Coop.Writer();
                w.VarU((ulong)MissionFlow.Instance.Epoch); w.Str(e.Id); w.U8((byte)e.Kind); w.F32(ValueOf(e)); w.Bool(true);
                Coop.Session.Send((ushort)Msg.ControlValue, w, Delivery.Reliable, slot);
            }
        }

        // ------------------------------------------------------------------ receiving

        /// <summary>True for a control message that belongs to this mission, when this machine has the mission loaded.</summary>
        static bool Current(NetReader r) => (int)r.VarU() == MissionFlow.Instance.Epoch && (WorldReady.LocalReady || Coop.IsHost);

        void OnValue(in MsgContext c, NetReader r)
        {
            if (!Current(r)) return;
            string id = r.Str(2048);
            var kind = (Kind)r.U8();
            float v = r.F32();
            bool final = r.Bool();
            if (!float.IsFinite(v)) return;
            if (!_byId.TryGetValue(id, out var e)) { if (!PeerRescan()) return; if (!_byId.TryGetValue(id, out e)) return; }
            if (final && e.Volume) CoopLog.Debug($"[controls] got {id} = {v:F3} from slot {c.FromSlot}{(final ? " (final)" : "")}: local drag {LocalDrag(e)}, synced {Synced(e)}, now {ValueOf(e):F3}");
            if (!Alive(e) || LocalDrag(e) || !Synced(e)) return;         // never fight the local player's hand
            if (!final) ReplayDrag(e, true);
            e.RemoteDragger = final ? (byte)255 : c.FromSlot;
            e.RemoteDragUntil = Time.unscaledTime + 0.5f;
            e.LastApplied = v; e.LastSent = v;
            ValuesApplied++;
            Coop.Apply("control " + id, () =>
            {
                if (e.Kind == Kind.Slider) e.Slider.SetSliderValue(v);
                else if (e.Dial.dialMode == DialInteractable.DialMode.Unlimited) e.Dial.SetAccumulatedValueUnlimited(v, true, true);
                else e.Dial.SetDialValue(v);
            });
            if (final) ReplayDrag(e, false);
        }

        void OnClick(in MsgContext c, NetReader r)
        {
            if (!Current(r)) return;
            string id = r.Str(2048);
            bool down = r.Bool();
            if (!_buttons.TryGetValue(id, out var b)) { PeerRescan(); if (!_buttons.TryGetValue(id, out b)) { CoopLog.Warn($"[controls] unknown button {id}"); return; } }
            if (b == null) return;
            if (Coop.IsHost)
            {
                ClicksExecuted++;
                PressingSlot = c.FromSlot;
                try { Coop.Apply("press " + id, () => { if (down) b.OnClickDown(); else b.OnClickUp(); }); }
                finally { PressingSlot = 0; }
            }
            else
            {
                // The look (pressed, pulled, lit) comes from the host's animators (see Animators). Setting it here too used
                // to make levers bounce: pressed locally, released early, then pulled again when the host's value came in.
                ReplayPresentation(b, down);
            }
        }

        // ------------------------------------------------------------------ press effects on guests

        // A press runs on the host. Its effects that are real state (such as a hatch opening) reach guests through the
        // state modules. Effects that are only presentation (the horn's animation, sound and screen shake, a lever's
        // pull animation) would never be seen, so guests fire the button's own event with only those listeners on.
        static readonly Dictionary<string, HashSet<string>> PresentationCalls = new()
        {
            ["AudioSource"] = new() { "Play", "PlayOneShot", "PlayDelayed", "Stop", "Pause", "UnPause" },
            ["StudioEventEmitter"] = new() { "Play", "Stop" },
            ["ScreenShakeSetter"] = new() { "ChangeShakeStrength" },
            ["CinemachineImpulseSource"] = new() { "GenerateImpulse", "GenerateImpulseAt", "GenerateImpulseWithForce", "GenerateImpulseWithVelocity" },
            ["ParticleSystem"] = new() { "Play", "Stop", "Emit", "Clear" },
            ["MMF_Player"] = new() { "PlayFeedbacks", "StopFeedbacks" },   // from the Feel plugin: the horn's shake and sound
        };

        // The button's own press animation (a lever pulls and springs back) comes from an AnimatorBoolToggler on the
        // button itself. Togglers on OTHER objects (the floor hatch) are world state and come through Toggles instead.
        static readonly HashSet<string> OwnTogglerCalls = new() { "ToggleBool", "ToggleBoolDelayed", "SetBool", "SetBoolDelayed" };

        public int PresentationsReplayed;

        static bool IsOwn(LookAtTarget b, UnityEngine.Object target)
        {
            var c = target.TryCast<Component>();
            return c != null && c.gameObject.Pointer == b.gameObject.Pointer;
        }

        void ReplayPresentation(LookAtTarget b, bool down)
        {
            try
            {
                if (!ReplayEffects(down ? b.onClickDown : b.onClickUp, "press effects")) return;
                PresentationsReplayed++;
                CoopLog.Debug($"[controls] replayed the press effects of {_buttonIds.GetValueOrDefault(b.Pointer)} ({(down ? "down" : "up")})");
            }
            catch (Exception e) { CoopLog.ErrorOnce("controls-presentation", "[controls] replaying press effects failed: " + e.Message); }
        }

        /// <summary>
        /// Fires only the presentation listeners of a game event (sounds, particles, shakes; see PresentationCalls).
        /// All other listeners, including ones added in code, are switched off and then restored exactly as they were.
        /// Use it for an action that ran on another machine, whose effects this player should still see and hear.
        /// Returns false if the event has no such listeners.
        /// </summary>
        internal static bool ReplayEffects(UnityEngine.Events.UnityEvent ev, string what) =>
            ev != null && ReplayEffects(ev, what, () => ev.Invoke());

        /// <summary>The same as above for any game event, including ones that carry a value; <paramref name="invoke"/> fires it.</summary>
        internal static bool ReplayEffects(UnityEngine.Events.UnityEventBase ev, string what, Action invoke)
        {
            if (ev == null) return false;
            int n = ev.GetPersistentEventCount();
            if (n == 0) return false;
            var keep = new bool[n];
            bool any = false, all = true;
            for (int i = 0; i < n; i++)
            {
                var target = ev.GetPersistentTarget(i);
                string type = target == null ? null : target.GetIl2CppType().Name;
                string method = ev.GetPersistentMethodName(i);
                keep[i] = type != null && PresentationCalls.TryGetValue(type, out var methods) && methods.Contains(method);
                any |= keep[i]; all &= keep[i];
            }
            if (!any) return false;
            var saved = new UnityEngine.Events.UnityEventCallState[n];
            for (int i = 0; i < n; i++)
            {
                saved[i] = ev.GetPersistentListenerState(i);
                if (!keep[i]) ev.SetPersistentListenerState(i, UnityEngine.Events.UnityEventCallState.Off);
            }
            // Listeners the game added in code (the requisition lever, the shell selector) are game logic.
            // They are switched off while the effects play, then restored exactly as they were.
            var calls = ev.m_Calls;
            var runtime = calls?.m_RuntimeCalls;
            Il2CppSystem.Collections.Generic.List<UnityEngine.Events.BaseInvokableCall> parked = null;
            if (runtime != null && runtime.Count > 0)
            {
                parked = new Il2CppSystem.Collections.Generic.List<UnityEngine.Events.BaseInvokableCall>();
                foreach (var call in runtime) parked.Add(call);
                runtime.Clear(); calls.m_NeedsUpdate = true;
            }
            try { Coop.ApplyVisual(what, invoke); }
            finally
            {
                if (!all) for (int i = 0; i < n; i++) ev.SetPersistentListenerState(i, saved[i]);
                if (parked != null) { foreach (var call in parked) runtime.Add(call); calls.m_NeedsUpdate = true; }
            }
            return true;
        }

        // ------------------------------------------------------------------ hover

        // Some controls react to being looked at (the firing lever pulls its rear clamp back while hovered). The game
        // only fires that on the machine whose cursor is there, so hovers are shared. Each machine runs the effect
        // while anyone (itself or a remote player) hovers, and ends it when the last one looks away.
        readonly Dictionary<IntPtr, HashSet<byte>> _remoteHover = new();
        readonly HashSet<IntPtr> _localHover = new();

        void LocalHover(LookAtTarget b, bool on)
        {
            if (on) _localHover.Add(b.Pointer); else _localHover.Remove(b.Pointer);
            if (!Coop.InSession || !HostRules.On(Rule.SharedHover)) return;
            string id = ButtonId(b);
            if (id == null || Personal(id)) return;
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.Str(id); w.Bool(on);
            Coop.Send(Msg.ControlHover, w, Net.Transport.Delivery.Reliable);
        }

        /// <summary>True when someone else still hovers this control, so our own look-away must not end the effect.</summary>
        bool RemoteHovering(LookAtTarget b) => _remoteHover.TryGetValue(b.Pointer, out var set) && set.Count > 0;

        void OnHover(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            if (!(WorldReady.LocalReady || Coop.IsHost)) return;
            string id = r.Str(2048); bool on = r.Bool();
            if (!HostRules.On(Rule.SharedHover)) return;
            if (!_buttons.TryGetValue(id, out var b)) { PeerRescan(); if (!_buttons.TryGetValue(id, out b)) return; }
            if (b == null) return;
            if (!_remoteHover.TryGetValue(b.Pointer, out var set)) _remoteHover[b.Pointer] = set = new HashSet<byte>();
            bool wasOn = set.Count > 0 || _localHover.Contains(b.Pointer);
            if (on) set.Add(c.FromSlot); else set.Remove(c.FromSlot);
            bool isOn = set.Count > 0 || _localHover.Contains(b.Pointer);
            if (isOn == wasOn) return;
            Coop.ApplyVisual("hover " + id, () => { if (isOn) b.OnLookAt(); else b.OnLookAway(); });
            HoversApplied++;
            CoopLog.Debug($"[controls] player {c.FromSlot + 1} {(on ? "hovers" : "left")} {id}");
        }

        /// <summary>Called when a player leaves, so they stop hovering everything.</summary>
        public override void OnPlayerLeft(Player p)
        {
            foreach (var kv in _remoteHover)
            {
                if (!kv.Value.Remove(p.Slot) || kv.Value.Count > 0 || _localHover.Contains(kv.Key)) continue;
                foreach (var b in _buttons.Values)
                    if (b != null && b.Pointer == kv.Key) { var bb = b; Coop.ApplyVisual("hover end", () => bb.OnLookAway()); break; }
            }
        }

        [HarmonyPatch(typeof(LookAtTarget), "UpdateHover")]
        static class Hover
        {
            static bool Prefix(LookAtTarget __instance, bool nowHovered, bool force, out bool __state)
            {
                __state = false;
                try
                {
                    __state = __instance._isHovered;
                    if (Coop.Applying || Instance == null || !Coop.InSession || nowHovered || !__state) return true;
                    // Our own look-away while someone else still hovers: the effect keeps running and we just stop hovering.
                    if (!Instance.RemoteHovering(__instance)) return true;
                    __instance._isHovered = false;
                    Instance.LocalHover(__instance, false);
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.Hover.Prefix", e); return true; }
            }

            static void Postfix(LookAtTarget __instance, bool nowHovered, bool __state)
            {
                try
                {
                    if (Coop.Applying || Instance == null || __instance._isHovered == __state) return;
                    Instance.LocalHover(__instance, nowHovered);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.Postfix line 486", e); }
            }
        }

        /// <summary>True for controls under a player's own camera. The same path exists on every machine, but each one belongs to its own player.</summary>
        bool Personal(string id) => id != null && (id.Contains(":Main Camera/") || id.Contains("/Main Camera/") || id.Contains("/ - Main Menu - /")
                                                    || id.Contains("/Campaign 1/MissionPages/") || _viewOnly.Contains(id)
                                                    || _mapClear.Contains(id) && !HostRules.On(Rule.SharedMapDrawings));

        /// <summary>Identifies the tactical map's "delete all lines" switch.</summary>
        readonly HashSet<string> _mapClear = new();

        static bool ClearsMap(LookAtTarget b)
        {
            foreach (var l in Util.Listeners.Of(b))
                if (l.TargetType == "ClearChildren" && l.Method == "DeleteAllChildren" && l.TargetName == "MapMarkers") return true;
            return false;
        }

        /// <summary>For the tests: true when the map's delete-all switch is currently each player's own.</summary>
        public bool IsPersonal(LookAtTarget b) => Personal(ButtonId(b));

        /// <summary>Identifies buttons that only bring something into this player's view, such as a tutorial sheet zoomed up close.</summary>
        readonly HashSet<string> _viewOnly = new();

        // Calls that only change what THIS player sees. A button counts as view-only when every click call is one of
        // these and at least one of them actually opens something (a zoomed sheet, the credits, a mission page).
        static readonly HashSet<string> ViewCalls = new()
        {
            "PickUpZoomTarget.PickUp", "PickUpZoomTarget.TogglePickUp", "PickUpZoomTarget.Release",
            "CreditsPanel.Show", "CreditsPanel.Hide", "MapCard.PopulateMissionInfo",
        };
        static readonly HashSet<string> NeutralCalls = new() { "GameObject.SetActive", "StudioEventEmitter.Play", "VirtualCursorInputModule.SetSelectedEventSystemObject" };

        static bool ViewOnly(LookAtTarget b)
        {
            bool any = false, opens = false;
            foreach (var l in Util.Listeners.Of(b))
            {
                if (l.Event != "onClickDown" && l.Event != "onClickUp") continue;
                any = true;
                string call = l.TargetType + "." + l.Method;
                if (ViewCalls.Contains(call)) opens = true;
                else if (!NeutralCalls.Contains(call)) return false;
            }
            if (any) return opens;
            // wired up in code instead of in the scene, so it has no persistent click listeners and sits on a zoomable object
            return b.GetComponent<PickUpZoomTarget>() != null || b.GetComponentInParent<PickUpZoomTarget>() != null;
        }

        static void SetClickVisual(LookAtTarget b, bool down)
        {
            try { var a = b.animator; if (a != null && a.isActiveAndEnabled) a.SetBool(IsClicked, down); }
            catch (Exception) { }
        }

        /// <summary>Gives one button its id. The first button to claim a path keeps it, and a destroyed one hands its path to its replacement.</summary>
        string RegisterButton(LookAtTarget b)
        {
            string id = IdOf(b, "btn");
            if (_buttons.TryGetValue(id, out var had) && had != null && had.Pointer != b.Pointer) return null;   // two buttons at one path: only the first one is synced
            if (had != null && had.Pointer == b.Pointer) return id;
            _buttons[id] = b; _buttonIds[b.Pointer] = id;
            _viewOnly.Remove(id); _mapClear.Remove(id);
            if (ViewOnly(b)) _viewOnly.Add(id);
            if (ClearsMap(b)) _mapClear.Add(id);
            return id;
        }

        /// <summary>Handles a button that was looked at or pressed here but was missing from the last scan (such as a
        /// card's buttons, dealt later). It gets its id on its own, because searching the whole scene again used to
        /// cause a hitch of about 45 ms each time.</summary>
        string ButtonId(LookAtTarget b)
        {
            if (_buttonIds.TryGetValue(b.Pointer, out var id)) return id;
            return RegisterButton(b);
        }

        /// <summary>Called when a guest presses a button. The press is forwarded to the host instead of running game logic here.</summary>
        internal bool GuestPress(LookAtTarget b, bool down)
        {
            if (!Coop.IsGuest || Coop.Applying) return true;
            string id = ButtonId(b);
            if (Personal(id)) return true;   // a button on the player's own notepad or clipboard (such as a card's Delete) belongs to that player alone
            if (id == null) { CoopLog.Warn("[controls] pressed an unregistered button; running locally"); return true; }
            ClicksForwarded++;
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.Str(id); w.Bool(down);
            Coop.SendToHost(Msg.ControlClick, w);
            return false;
        }

        /// <summary>Called when the host pressed a button (for itself or for a guest). Shows the press on every guest.</summary>
        internal void HostPressed(LookAtTarget b, bool down)
        {
            if (!Coop.IsHost) return;
            string id = ButtonId(b);
            if (id == null || Personal(id)) return;
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.Str(id); w.Bool(down);
            WorldReady.SendToReady(Msg.ControlClick, w);
        }

        // ---- a control another player holds is theirs while they hold it ----
        // A dial turned by another player is replayed here as a real drag, and a dragging dial turns by how far THIS
        // player's pointer moved on its plane. Hovering it used to make it follow the hover, which looked snappy
        // until the host's next value snapped it back. While a drag is replayed, the dial sees the pointer standing
        // still, and a click on a control someone else holds does not start a second drag (or end theirs on release).
        static readonly Dictionary<IntPtr, Vector3> FrozenPointer = new();
        public static int PointerFrozen, PressesRefused;   // counted for the tests

        [HarmonyPatch(typeof(DialInteractable), "GetPointerWorldPointOnDialPlane")]
        static class DialPointer
        {
            static void Postfix(DialInteractable __instance, ref Vector3 __result)
            {
                try
                {
                    if (Replayed.Count == 0 || !Replayed.Contains(__instance.Pointer) || LaunchArgs.Mutant("dialpointer")) return;
                    if (FrozenPointer.TryGetValue(__instance.Pointer, out var held)) { if (held != __result) PointerFrozen++; __result = held; }
                    else FrozenPointer[__instance.Pointer] = __result;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.DialPointer", e); }
            }
        }

        /// <summary>
        /// The cursor can also take over a drag on its own: its Update begins a drag on a hovered control that is being
        /// dragged, bypassing TryAdoptExternalActiveDrag. The cursor then showed the grabbing hand until the other
        /// player let go. A control another player holds is never this player's drag.
        /// </summary>
        [HarmonyPatch(typeof(DynamicCursorManager), "BeginDrag")]
        static class NoBeginReplay
        {
            static bool Prefix(ICursorDraggable draggable)
            {
                try
                {
                    if (Replayed.Count == 0 || draggable == null || LaunchArgs.Mutant("beginreplay")) return true;
                    var c = (Component)draggable.TryCast<DialInteractable>() ?? draggable.TryCast<LinearSliderInteractable>();
                    if (c == null || !Replayed.Contains(c.Pointer)) return true;
                    AdoptionsSkipped++;
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.NoBeginReplay", e); return true; }
            }
        }

        static bool HeldByOther(Component c) => Replayed.Count > 0 && Replayed.Contains(c.Pointer) && !LaunchArgs.Mutant("heldpress");

        [HarmonyPatch(typeof(DialInteractable), "HandlePrimaryClickDown")]
        static class DialPress
        {
            static bool Prefix(DialInteractable __instance)
            {
                try { if (!HeldByOther(__instance)) return true; PressesRefused++; return false; }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.DialPress", e); return true; }
            }
        }

        [HarmonyPatch(typeof(DialInteractable), "HandlePrimaryClickUp")]
        static class DialRelease
        {
            static bool Prefix(DialInteractable __instance)
            {
                try { return !HeldByOther(__instance); }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.DialRelease", e); return true; }
            }
        }

        [HarmonyPatch(typeof(LinearSliderInteractable), "HandlePrimaryClickDown")]
        static class SliderPress
        {
            static bool Prefix(LinearSliderInteractable __instance)
            {
                try { if (!HeldByOther(__instance)) return true; PressesRefused++; return false; }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.SliderPress", e); return true; }
            }
        }

        [HarmonyPatch(typeof(LinearSliderInteractable), "HandlePrimaryClickUp")]
        static class SliderRelease
        {
            static bool Prefix(LinearSliderInteractable __instance)
            {
                try { return !HeldByOther(__instance); }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.SliderRelease", e); return true; }
            }
        }

        [HarmonyPatch(typeof(LinearSliderInteractable), "Update")]
        static class SliderUpdate
        {
            static bool Prefix(LinearSliderInteractable __instance)
            {
                try { return HeldSliders.Count == 0 || !HeldSliders.Contains(__instance.Pointer); }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.Prefix line 578", e); return true; }
            }
        }

        [HarmonyPatch(typeof(LookAtTarget), nameof(LookAtTarget.OnClickDown))]
        static class ClickDown
        {
            static bool Prefix(LookAtTarget __instance)
            {
                try { return Instance == null || Instance.GuestPress(__instance, true); }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.Prefix line 584", e); return true; }
            }
            static void Postfix(LookAtTarget __instance) { try { if (Coop.IsHost) Instance?.HostPressed(__instance, true); } catch (System.Exception e) { CoopLog.PatchFailed("Controls.Postfix line 597", e); } }
        }

        [HarmonyPatch(typeof(LookAtTarget), nameof(LookAtTarget.OnClickUp))]
        static class ClickUp
        {
            static bool Prefix(LookAtTarget __instance)
            {
                try { return Instance == null || Instance.GuestPress(__instance, false); }
                catch (System.Exception e) { CoopLog.PatchFailed("Controls.Prefix line 591", e); return true; }
            }
            static void Postfix(LookAtTarget __instance) { try { if (Coop.IsHost) Instance?.HostPressed(__instance, false); } catch (System.Exception e) { CoopLog.PatchFailed("Controls.Postfix line 608", e); } }
        }
    }
}
