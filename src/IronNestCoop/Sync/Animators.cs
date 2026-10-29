using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Keeps the look of the turret's animated parts in line with the host. A button press runs on the host only
    /// (guests forward it), so the lever it pulls, the switch it flips and the lamp it lights would otherwise move
    /// only there. The turret's ~290 animators keep their look in bool/int parameters (IsClicked, IsActive, the
    /// toggled position), so the host sends those whenever they change and guests set them. The guest's animator
    /// then plays the same transition itself, at full frame rate. One-shot animations started by triggers (breach,
    /// rammer, dispenser) leave no parameter behind, so for animators with triggers the host also sends the state
    /// it enters. Guests re-assert the host's values every 2 s, so a local slip (for example a game script on the
    /// guest) heals itself.
    /// </summary>
    public sealed class Animators : SyncModule
    {
        public override string Name => "animators";
        public static Animators Instance { get; private set; }

        // Animators that belong to each player alone (camera moves, menus, screens) and our avatar puppets are not synced.
        static readonly string[] Personal =
        {
            "Main Camera", "IronNestCoop", "Coop ", ":Canvas", "/Canvas", "Main Menu", "Camera", "NewsPaper", "Loading Bar", "Fade",
            "Tutorial Cutaway", "cinamatic", "Mission Load", "Console UI prompt", "Hold Enter", "Clipboard", "Cat",
        };
        static readonly HashSet<string> PersonalParams = new() { "IsWorn" };   // the gas mask, which only the player wearing it sees
        const string HoverParam = "IsHoverdOver";

        sealed class Entry
        {
            public string Id;
            public Animator A;
            public int[] Hash; public bool[] IsInt; public bool[] Hover;
            public int[] Last;           // on the host: the last value sent; on a guest: the host's value
            public bool[] Have;
            public bool Triggers;
            public bool CaughtUp;                       // guests only: already jumped to the current host state once
            public float MismatchSince, LastCatchUp;
            public int SentState, HostState;
            public float HostStateAt;
            public LookAtTarget Button;   // the button this animator shows, if any
        }

        readonly Dictionary<string, Entry> _byId = new();
        int _sceneSig = -1;
        float _nextScan, _nextPoll, _nextAssert, _nextStates;
        float _nextForcedScan;

        /// <summary>Counters used by the tests.</summary>
        public int ParamsApplied, StatesApplied, Sent;

        public Animators() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Animators, OnMessage);
        public override void OnSessionStart() { _byId.Clear(); _pathOf.Clear(); _sceneSig = -1; ParamsApplied = StatesApplied = Sent = 0; }

        public static bool IsPersonal(string path) => path == null || Personal.Any(path.Contains);

        static int SceneSignature()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        readonly Dictionary<IntPtr, (string Name, string Path)> _pathOf = new();

        void Scan()
        {
            int sig = SceneSignature();
            if (sig == _sceneSig && (Time.unscaledTime < _nextScan || !Perf.HeavyFrame())) return;
            _nextScan = Time.unscaledTime + 3f;
            if (sig != _sceneSig) { _byId.Clear(); _pathOf.Clear(); }
            _sceneSig = sig;
            if (_pathOf.Count > 20000) _pathOf.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Animator>(), true))
            {
                var a = o.TryCast<Animator>();
                if (a == null) continue;
                // Each animator's path is built once, because building them all on every scan was most of the cost.
                // A pointer can be reused by a new object, so the name must still match.
                string name = a.name, id;
                if (_pathOf.TryGetValue(a.Pointer, out var c) && c.Name == name) id = c.Path;
                else _pathOf[a.Pointer] = (name, id = ScenePath.Of(a.transform));
                if (IsPersonal(id) || _byId.TryGetValue(id, out var known) && known.A != null && known.A.Pointer == a.Pointer) continue;
                if (a.runtimeAnimatorController == null) continue;
                if (_byId.TryGetValue(id, out var have) && have.A != null && have.A.Pointer == a.Pointer) continue;
                if (!a.isActiveAndEnabled) continue;   // the parameters of an inactive animator cannot be read; it is picked up once it is active
                var hashes = new List<int>(); var ints = new List<bool>(); var hover = new List<bool>();
                bool triggers = false;
                for (int i = 0; i < a.parameterCount; i++)
                {
                    var p = a.GetParameter(i);
                    if (p.type == AnimatorControllerParameterType.Trigger) { triggers = true; continue; }
                    if (p.type == AnimatorControllerParameterType.Float || PersonalParams.Contains(p.name)) continue;
                    hashes.Add(p.nameHash); ints.Add(p.type == AnimatorControllerParameterType.Int); hover.Add(p.name == HoverParam);
                }
                if (hashes.Count == 0 && !triggers) continue;
                _byId[id] = new Entry
                {
                    Id = id, A = a, Hash = hashes.ToArray(), IsInt = ints.ToArray(), Hover = hover.ToArray(),
                    Last = new int[hashes.Count], Have = new bool[hashes.Count], Triggers = triggers, SentState = 0, HostState = 0,
                    Button = a.GetComponentInParent<LookAtTarget>(),
                };
            }
        }

        /// <summary>For the tests: describes what the mirror knows about one animator.</summary>
        public string Describe(Animator a)
        {
            if (a == null) return "no animator";
            string id = ScenePath.Of(a.transform);
            if (IsPersonal(id)) return "personal (not mirrored)";
            if (!_byId.TryGetValue(id, out var e)) return "not tracked";
            var cur = a.GetCurrentAnimatorStateInfo(0);
            return $"tracked usable={Usable(e)} triggers={e.Triggers} hostState={e.HostState} here={cur.fullPathHash} inTransition={a.IsInTransition(0)} caughtUp={e.CaughtUp} hasHostState={(e.HostState != 0 && a.HasState(0, e.HostState))}";
        }

        static bool Usable(Entry e) => e.A != null && e.A.isActiveAndEnabled && !(e.Button != null && Controls.Instance.IsPersonal(e.Button));
        static int Get(Entry e, int i) => e.IsInt[i] ? e.A.GetInteger(e.Hash[i]) : (e.A.GetBool(e.Hash[i]) ? 1 : 0);
        static void Set(Entry e, int i, int v) { if (e.IsInt[i]) e.A.SetInteger(e.Hash[i], v); else e.A.SetBool(e.Hash[i], v != 0); }
        static int State(Animator a) => a.IsInTransition(0) ? a.GetNextAnimatorStateInfo(0).fullPathHash : a.GetCurrentAnimatorStateInfo(0).fullPathHash;
        static bool HoverShared => HostRules.On(Rule.SharedHover);

        public override void Tick(float dt)
        {
            if (!Coop.InSession) return;
            if (Coop.IsHost) { if (Time.unscaledTime >= _nextPoll) { _nextPoll = Time.unscaledTime + 0.05f; Scan(); HostPoll(); } }
            else if (WorldReady.LocalReady)
            {
                if (Time.unscaledTime >= _nextAssert) { _nextAssert = Time.unscaledTime + 2f; Reassert(); }
                if (Time.unscaledTime >= _nextStates) { _nextStates = Time.unscaledTime + 0.25f; CatchUpStates(); }
            }
        }

        // ------------------------------------------------------------------ host

        void HostPoll()
        {
            NetWriter w = null; int count = 0;
            var items = new List<(Entry e, List<(int i, int v)> ps, int state)>();
            foreach (var e in _byId.Values)
            {
                if (!Usable(e)) continue;
                List<(int, int)> ps = null;
                for (int i = 0; i < e.Hash.Length; i++)
                {
                    if (e.Hover[i] && !HoverShared) continue;
                    int v = Get(e, i);
                    if (e.Have[i] && v == e.Last[i]) continue;
                    e.Last[i] = v; e.Have[i] = true;
                    (ps ??= new()).Add((i, v));
                }
                // the state it is in or heading to, either a one-shot (trigger) or a pose a script put it in
                int st = State(e.A);
                if (st == e.SentState) st = 0; else e.SentState = st;
                if (ps != null || st != 0) items.Add((e, ps, st));
            }
            if (items.Count == 0) return;
            w = Begin(); w.VarU((ulong)items.Count);
            foreach (var (e, ps, st) in items) { Write(w, e, ps, st); count++; }
            WorldReady.SendToReady(Msg.Animators, w);
            Sent += count;
        }

        static NetWriter Begin() { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); return w; }

        static void Write(NetWriter w, Entry e, List<(int i, int v)> ps, int state)
        {
            w.Str(e.Id);
            w.VarU((ulong)(ps?.Count ?? 0));
            if (ps != null) foreach (var (i, v) in ps) { w.VarI(e.Hash[i]); w.VarI(v); }
            w.VarI(state);
        }

        public override void SendSnapshot(byte slot)
        {
            Scan();
            var items = new List<(Entry, List<(int, int)>, int)>();
            foreach (var e in _byId.Values)
            {
                if (!Usable(e)) continue;
                var ps = new List<(int, int)>();
                for (int i = 0; i < e.Hash.Length; i++)
                {
                    if (e.Hover[i]) continue;   // nobody is hovering anything in a joiner's first frame
                    int v = Get(e, i); e.Last[i] = v; e.Have[i] = true; ps.Add((i, v));
                }
                items.Add((e, ps, 0));   // one-shots are already over by the time anyone sees them
            }
            // sent in pieces, one message per 64 animators
            for (int at = 0; at < items.Count; at += 64)
            {
                var part = items.Skip(at).Take(64).ToList();
                var w = Begin(); w.VarU((ulong)part.Count);
                foreach (var (e, ps, st) in part) Write(w, e, ps, st);
                Coop.Session.Send((ushort)Msg.Animators, w, Delivery.Reliable, slot);
            }
        }

        // ------------------------------------------------------------------ guest

        void OnMessage(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            Scan();
            int n = r.Count(4096, "animators");
            for (int k = 0; k < n; k++)
            {
                string id = r.Str(2048);
                int pcRaw = r.Count(4096, "animator values");
                if (pcRaw < 0 || pcRaw > 4096) throw new IronNestCoop.Net.Wire.MalformedMessageException("too many animator values");
                int pc = Math.Min(pcRaw, 256);
                var ps = new (int hash, int v)[pc];
                for (int j = 0; j < pcRaw; j++) { var pv = ((int)r.VarI(), (int)r.VarI()); if (j < pc) ps[j] = pv; }   // read every entry, but keep only the first 256
                int state = (int)r.VarI();
                if (!_byId.TryGetValue(id, out var e))
                {
                    // An animator this machine has not seen yet: rescan, but at most once a second. Each scan walks every
                    // animator, and an inactive one here would otherwise trigger a rescan on every host update.
                    if (Time.unscaledTime < _nextForcedScan) continue;
                    _nextForcedScan = Time.unscaledTime + 1f; _nextScan = 0; Scan();
                    if (!_byId.TryGetValue(id, out e)) continue;
                }
                if (!Usable(e)) continue;
                foreach (var (hash, v) in ps)
                {
                    int i = Array.IndexOf(e.Hash, hash);
                    if (i < 0) continue;
                    e.Last[i] = v; e.Have[i] = true;
                    if (e.Hover[i] && !HoverShared) continue;
                    if (Get(e, i) == v) continue;
                    int ii = i;
                    Coop.ApplyVisual("animator " + id, () => Set(e, ii, v));
                    ParamsApplied++;
                }
                if (state != 0)
                {
                    e.HostState = state; e.HostStateAt = Time.unscaledTime; e.CaughtUp = false; e.MismatchSince = 0;
                    // One-shots play at once. Parameter-driven animators get there by themselves (checked later).
                    if (e.Triggers && State(e.A) != state && e.A.HasState(0, state))
                    {
                        Coop.ApplyVisual("animator state " + id, () => e.A.Play(state, 0, 0f));
                        StatesApplied++;
                    }
                }
            }
        }

        /// <summary>
        /// Guest only: if an animator is still not in the host's state well after the host got there, it jumps to that
        /// state. This covers poses a script set with Play, which no parameter carries.
        /// </summary>
        void CatchUpStates()
        {
            float now = Time.unscaledTime;
            foreach (var e in _byId.Values)
            {
                if (e.HostState == 0 || !Usable(e) || now - e.HostStateAt < 0.6f || e.A.IsInTransition(0)) { e.MismatchSince = 0; continue; }
                if (e.A.GetCurrentAnimatorStateInfo(0).fullPathHash == e.HostState || !e.A.HasState(0, e.HostState)) { e.MismatchSince = 0; continue; }
                // This is not a one-time fix. A pose the host's script holds (such as a pulled charge lever) can be undone
                // here by this machine's own transitions when the press flags reset, so keep it where the host has it.
                bool hovered = false;
                for (int i = 0; i < e.Hash.Length && !hovered; i++) if (e.Hover[i] && Get(e, i) != 0) hovered = true;
                if (hovered) { e.MismatchSince = 0; continue; }   // this player is looking at it right now, so that pose is theirs
                if (e.MismatchSince == 0) e.MismatchSince = now;
                bool first = !e.CaughtUp;
                if (!first && (now - e.MismatchSince < 1f || now - e.LastCatchUp < 2f)) continue;
                int st = e.HostState; var a = e.A;
                Coop.ApplyVisual("animator state " + e.Id, () => a.Play(st, 0, 1f));   // it has settled, so jump to the end of that state
                e.CaughtUp = true; e.LastCatchUp = now; e.MismatchSince = 0;
                StatesApplied++;
            }
        }

        /// <summary>Guest only: puts back the host's values wherever something on this machine changed them. Hover is left alone, because it is live.</summary>
        void Reassert()
        {
            int fixedUp = 0;
            foreach (var e in _byId.Values)
            {
                if (!Usable(e)) continue;
                for (int i = 0; i < e.Hash.Length; i++)
                {
                    if (!e.Have[i] || e.Hover[i] || Get(e, i) == e.Last[i]) continue;
                    int ii = i; var ee = e;
                    Coop.ApplyVisual("animator " + e.Id, () => Set(ee, ii, ee.Last[ii]));
                    fixedUp++;
                }
            }
            if (fixedUp > 0) CoopLog.Debug($"[animators] put back {fixedUp} value(s) that differed from the host's");
        }

        /// <summary>Used by the tests.</summary>
        public int Count { get { Scan(); return _byId.Count; } }

        public override string Status => $"{_byId.Count} animators, sent {Sent}, applied {ParamsApplied} values / {StatesApplied} states";
    }
}
