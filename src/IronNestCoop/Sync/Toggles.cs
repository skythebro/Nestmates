using System;
using HarmonyLib;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// On/off things in the turret: light switches, single lamps (click a hanging lamp), hatches and covers,
    /// animated toggles, and the teleprinters' alert lamps being dismissed (walking up to a printer).
    /// Every machine polls its toggles twice a second; a change made here (not one we applied) goes to the host,
    /// which applies it and passes it on; the host's own changes go to everyone, the full set to a joiner. Some
    /// of these are not buttons (a lamp's own click, a walk-in trigger), so they cannot be forwarded as presses.
    /// </summary>
    public sealed class Toggles : SyncModule
    {
        public override string Name => "toggles";
        public static Toggles Instance { get; private set; }

        enum Kind : byte { AnimBool = 1, ObjectToggle = 2, OnOff = 3, Lights = 4, Lamp = 5, Button = 6, LampEmergency = 7 }
        enum Op : byte { States = 0, DismissAlerts = 1 }

        /// <summary>SnapshotOnly: a button's own press animation (it travels with the press, see Controls); polling
        /// it would miss quick pulls and fight the replay, so it only goes to joiners.</summary>
        sealed class Entry { public string Id; public Kind Kind; public Component C; public IntPtr Ptr; public bool Last; public bool Known; public bool SnapshotOnly; }

        readonly Dictionary<string, Entry> _byId = new();
        int _sceneSig = int.MinValue;
        float _nextPoll, _nextScan;
        public int Applied, Sent, Dismissed;

        public Toggles() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Toggles, OnToggles);
        public override void OnSessionStart() { _byId.Clear(); _seen.Clear(); _sceneSig = int.MinValue; }

        readonly HashSet<IntPtr> _seen = new();   // components already looked at (their path is not built again)
        int _step;
        float _nextFull;

        static int SceneSignature()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        const int Steps = 5;

        /// <summary>
        /// Finds the toggles of the turret. A new scene is searched at once; after that one kind is looked for per
        /// step (a step every second), so no frame pays for every search at once (that was a ~40 ms hitch every 5 s).
        /// Components already seen are skipped without building their path again.
        /// </summary>
        void Scan(bool all = false)
        {
            int sig = SceneSignature();
            if (sig != _sceneSig) { _byId.Clear(); _seen.Clear(); _sceneSig = sig; all = true; }
            if (!all && (Time.unscaledTime < _nextScan || !Perf.HeavyFrame())) return;
            _nextScan = Time.unscaledTime + 2f;
            for (int i = 0; i < (all ? Steps : 1); i++) { ScanStep(_step); _step = (_step + 1) % Steps; }
        }

        void ScanStep(int step)
        {
            switch (step)
            {
                case 0: Add<AnimatorBoolToggler>(Kind.AnimBool); break;
                case 1: Add<AnimatedObjectToggle>(Kind.ObjectToggle); Add<AnimatorOnOffStateChanger>(Kind.OnOff); break;
                case 2: Add<SwingLightFlickerController>(Kind.Lights); break;
                case 3: Add<SwingLightFlicker>(Kind.Lamp, Kind.LampEmergency); break;   // emergency lamps keep the player's switch in a second flag
                default: AddSequenceSlots(); break;
            }
        }

        /// <summary>A peer named a toggle we don't have: search everything, at most once a second.</summary>
        void ScanForUnknown()
        {
            if (Time.unscaledTime < _nextFull) return;
            _nextFull = Time.unscaledTime + 1f;
            Scan(all: true);
        }

        // A button's own lit/active state, only where it is world state: the fire-control "confirmed" switches,
        // which unlock one after another (LookAtTargetUnlockSequence5). Other buttons switch themselves on and off
        // locally (reload cylinders, mission-spawned letters) and must not be compared or forced.
        void AddSequenceSlots()
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<LookAtTargetUnlockSequence5>(), true))
            {
                var seq = o.TryCast<LookAtTargetUnlockSequence5>();
                if (seq == null) continue;
                foreach (var b in new[] { seq.slot1, seq.slot2, seq.slot3, seq.slot4, seq.slot5 })
                {
                    if (b == null || !_seen.Add(b.Pointer)) continue;
                    string path = ScenePath.Of(b.transform);
                    if (Personal(path)) continue;
                    string id = (byte)Kind.Button + "|" + path;
                    if (!_byId.TryGetValue(id, out var was) || was.C == null) _byId[id] = new Entry { Id = id, Kind = Kind.Button, C = b, Ptr = b.Pointer };
                }
            }
        }

        void Add<T>(params Kind[] kinds) where T : Component
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<T>(), true))
            {
                var c = o.TryCast<T>();
                if (c == null || !_seen.Add(c.Pointer)) continue;
                string path = ScenePath.Of(c.transform);
                if (Personal(path)) continue;
                foreach (var kind in kinds)
                {
                    string id = (byte)kind + "|" + path;
                    // a respawned component at the same path takes the place of the destroyed one
                    if (!_byId.TryGetValue(id, out var was) || was.C == null)
                        _byId[id] = new Entry { Id = id, Kind = kind, C = c, Ptr = c.Pointer, SnapshotOnly = kind == Kind.AnimBool && c.GetComponent<LookAtTarget>() != null };
                }
            }
        }

        /// <summary>Each player's own things: what hangs off their camera (watch, clipboard), their screen HUD, our puppets.</summary>
        static bool Personal(string path) => path.Contains(":Main Camera/") || path.Contains(":Canvas/") || path.EndsWith(":Canvas") || path.Contains("Coop ");

        static bool Read(Entry e) => e.Kind switch
        {
            Kind.AnimBool => e.C.TryCast<AnimatorBoolToggler>().GetBool(),
            Kind.ObjectToggle => e.C.TryCast<AnimatedObjectToggle>().ToggleOn,
            Kind.OnOff => e.C.TryCast<AnimatorOnOffStateChanger>()._isOn,
            Kind.Lights => e.C.TryCast<SwingLightFlickerController>()._switchOn,
            Kind.Lamp => e.C.TryCast<SwingLightFlicker>()._manualOverrideOff,   // true = switched off by hand
            Kind.LampEmergency => e.C.TryCast<SwingLightFlicker>()._emergencyPlayerOverride,
            Kind.Button => e.C.TryCast<LookAtTarget>().GetActive(),
            _ => false,
        };

        static void Write(Entry e, bool on)
        {
            switch (e.Kind)
            {
                case Kind.AnimBool: e.C.TryCast<AnimatorBoolToggler>().SetBool(on); break;
                case Kind.ObjectToggle: e.C.TryCast<AnimatedObjectToggle>().ToggleOn = on; break;   // it applies this itself (polled)
                case Kind.OnOff: e.C.TryCast<AnimatorOnOffStateChanger>().SetState(on); break;
                case Kind.Lights: e.C.TryCast<SwingLightFlickerController>().SetPowerAll(on); break;
                case Kind.Lamp: e.C.TryCast<SwingLightFlicker>().SetManualOverride(on); break;
                case Kind.LampEmergency:
                {
                    var l = e.C.TryCast<SwingLightFlicker>();
                    l._emergencyPlayerOverride = on;
                    l.ApplyDesiredLightState();
                    break;
                }
                case Kind.Button: e.C.TryCast<LookAtTarget>().SetActive(on); break;
            }
        }

        /// <summary>Test hook: current value of a toggle by id.</summary>
        public bool? Value(string id) => _byId.TryGetValue(id, out var e) && e.C != null ? Read(e) : null;
        public IEnumerable<string> Ids { get { Scan(); return _byId.Keys; } }

        public override void Tick(float dt)
        {
            if (!Coop.InSession || Time.unscaledTime < _nextPoll || Coop.Applying) return;
            if (!Coop.IsHost && !WorldReady.LocalReady) return;
            _nextPoll = Time.unscaledTime + 0.5f;
            Scan();
            var changed = new List<Entry>();
            foreach (var e in _byId.Values)
            {
                if (e.C == null) { _seen.Remove(e.Ptr); continue; }   // destroyed: a new one there is looked at again
                if (e.SnapshotOnly) continue;
                bool v = Read(e);
                if (!e.Known || v != e.Last) { e.Last = v; if (e.Known && (Coop.IsHost || GuestMayReport(e.Kind))) changed.Add(e); e.Known = true; }
            }
            if (changed.Count == 0) return;
            if (Coop.IsHost) Send(changed, null);
            else
            {
                // our own change (a lamp clicked, a hatch opened by something local): the host decides and passes it on
                var w = Begin(Op.States); w.VarU((ulong)changed.Count);
                foreach (var e in changed) { w.Str(e.Id); w.Bool(e.Last); }
                Coop.SendToHost(Msg.Toggles, w);
                Sent += changed.Count;
            }
        }

        // and only while its mission runs: a scene being torn down (mission end, back to the map) switches things off
        // locally, and that must never reach the host's running Iron Nest
        static bool GuestMayReport(Kind k) => MissionRunning && GuestReportKind(k);
        static bool MissionRunning => MissionManager.Instance != null && MissionManager.Instance._CurrentPhase_k__BackingField == MissionManager.GamePhase.MissionActive
                                      && MissionManager.Instance._CurrentMission_k__BackingField?.MissionID == MissionFlow.Instance.MissionId;
        static bool GuestReportKind(Kind k) => k == Kind.Lamp || k == Kind.LampEmergency || k == Kind.Lights || k == Kind.ObjectToggle || k == Kind.OnOff;

        static NetWriter Begin(Op op) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)op); return w; }

        public override void SendSnapshot(byte slot)
        {
            Scan(all: true);
            var all = new List<Entry>();
            foreach (var e in _byId.Values) if (e.C != null) { e.Last = Read(e); e.Known = true; all.Add(e); }
            Send(all, slot);
        }

        void Send(List<Entry> list, byte? slot)
        {
            var w = Begin(Op.States); w.VarU((ulong)list.Count);
            foreach (var e in list) { w.Str(e.Id); w.Bool(e.Last); }
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Toggles, w, Net.Transport.Delivery.Reliable, one);
            else WorldReady.SendToReady(Msg.Toggles, w);
            Sent += list.Count;
        }

        void OnToggles(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            if (!(Coop.IsHost || (c.FromHost && WorldReady.LocalReady))) return;   // guests take the host's word only
            var op = (Op)r.U8();
            if (op == Op.DismissAlerts) { OnDismiss(c, r); return; }
            Scan();
            bool unknown = false;
            int n = r.Count(4096, "toggles");
            var relay = Coop.IsHost ? new List<Entry>() : null;
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(2048); bool on = r.Bool();
                if (!_byId.TryGetValue(id, out var e) || e.C == null)
                {
                    if (unknown) continue;
                    unknown = true; ScanForUnknown();
                    if (!_byId.TryGetValue(id, out e) || e.C == null) continue;
                }
                e.Last = on; e.Known = true;   // what we apply is not our own change
                if (Read(e) == on) continue;
                // guests show the host's state; its listeners must not run mission logic here (the host did)
                if (Coop.IsHost) Coop.Apply("toggle " + id, () => Write(e, on));
                else Coop.ApplyVisual("toggle " + id, () => Write(e, on));
                e.Last = Read(e);
                Applied++;
                relay?.Add(e);
            }
            if (relay != null && relay.Count > 0) Send(relay, null);   // host: a guest's change, now everyone's
        }

        // ------------------------------------------------------------------ teleprinter alert lamps

        /// <summary>Walking up to a printer dismisses its alert lamps (a trigger zone, so on that player's machine
        /// only): dismissing is shared, so the lamp goes out for everyone once someone has gone to read it.</summary>
        void OnDismiss(in MsgContext c, NetReader r)
        {
            string path = r.Str(2048);
            var sys = ScenePath.Find(path)?.GetComponent<PrinterAlertSystem>();
            if (sys == null) return;
            Coop.Apply("dismiss alerts " + path, () => sys.DismissAllAlerts());
            Dismissed++;
            if (Coop.IsHost) { var w = Begin(Op.DismissAlerts); w.Str(path); WorldReady.SendToReady(Msg.Toggles, w); }
        }

        [HarmonyPatch(typeof(PrinterAlertSystem), nameof(PrinterAlertSystem.DismissAllAlerts))]
        static class Dismiss
        {
            static void Postfix(PrinterAlertSystem __instance)
            {
                if (!Coop.InSession || Coop.Applying || Instance == null) return;
                if (!Coop.IsHost && !WorldReady.LocalReady) return;
                try
                {
                    var w = Begin(Op.DismissAlerts); w.Str(ScenePath.Of(__instance.transform));
                    if (Coop.IsHost) WorldReady.SendToReady(Msg.Toggles, w); else Coop.SendToHost(Msg.Toggles, w);
                }
                catch (Exception e) { CoopLog.ErrorOnce("toggles-dismiss", "[toggles] sharing an alert dismissal failed: " + e.Message); }
            }
        }
    }
}
