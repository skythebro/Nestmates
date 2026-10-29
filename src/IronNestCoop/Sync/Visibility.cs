using System;
using System.Collections.Generic;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// What the mission shows and hides. Mission graphs run on the host only (GraphGate), and many of their steps
    /// just switch scene objects on or off (the "mission complete" newspaper, a delivery, a prop for the next
    /// step). The host watches every object of the mission scene and of the turret's mission-event objects and
    /// sends each on/off change; guests switch the same object. Joiners get the full list.
    /// </summary>
    public sealed class Visibility : SyncModule
    {
        public override string Name => "visibility";
        public static Visibility Instance { get; private set; }

        const string TurretScene = "Master Turret Scene";
        static readonly string[] TurretMissionRoots = { "- Mission Graph Events ---" };

        sealed class Entry
        {
            public string Id; public GameObject Go; public Light L; public IntPtr Ptr; public bool Last;
            public bool Value => L != null ? L.enabled : Go.activeSelf;
            public void Set(bool on) { if (L != null) L.enabled = on; else Go.SetActive(on); }
        }

        readonly Dictionary<string, Entry> _byId = new();
        readonly List<Entry> _list = new();
        int _sceneSig = -1;
        float _nextScan, _nextPoll;
        float _nextForcedScan;
        public int Applied, Sent;

        public Visibility() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Visibility, OnMessage);
        public override void OnSessionStart() { _byId.Clear(); _list.Clear(); _seen.Clear(); _sceneSig = -1; Applied = Sent = 0; }

        // transforms (and lights) already looked at: their path is not built again on every scan (that was most
        // of a ~16 ms frame every 4 s); a destroyed one is forgotten so a new object there is looked at
        readonly HashSet<IntPtr> _seen = new();

        static int SceneSignature()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        static bool Watched(string path) => path != null && !Animators.IsPersonal(path);

        void Scan()
        {
            int sig = SceneSignature();
            if (sig == _sceneSig && (Time.unscaledTime < _nextScan || !Perf.HeavyFrame())) return;
            _nextScan = Time.unscaledTime + 4f;
            if (sig != _sceneSig) { _byId.Clear(); _list.Clear(); _seen.Clear(); }
            _sceneSig = sig;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
            {
                var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || scene.name == "Main Menu") continue;
                foreach (var root in scene.GetRootGameObjects())
                {
                    if (scene.name == TurretScene && Array.IndexOf(TurretMissionRoots, root.name) < 0) continue;
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (!_seen.Add(t.Pointer)) continue;
                        string id = ScenePath.Of(t);
                        if (!Watched(id)) continue;
                        if (_byId.TryGetValue(id, out var have) && have.Go != null) continue;
                        var e = new Entry { Id = id, Go = t.gameObject, Ptr = t.Pointer, Last = t.gameObject.activeSelf };
                        _byId[id] = e; _list.Add(e);
                    }
                }
            }
            // lights a script switches (the bedroom's sleep lights-off); lamps that flicker run themselves
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Light>(), true))
            {
                var l = o.TryCast<Light>();
                if (l == null || !_seen.Add(l.Pointer) || l.GetComponentInParent<SwingLightFlicker>() != null) continue;
                string id = "light " + ScenePath.Of(l.transform);
                if (!Watched(id)) continue;
                if (_byId.TryGetValue(id, out var have) && have.Go != null) continue;
                var e = new Entry { Id = id, Go = l.gameObject, L = l, Ptr = l.Pointer, Last = l.enabled };
                _byId[id] = e; _list.Add(e);
            }
            foreach (var e in _list) if (e.Go == null) _seen.Remove(e.Ptr);
            _list.RemoveAll(e => e.Go == null);
        }

        static NetWriter Begin() { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); return w; }

        public override void Tick(float dt)
        {
            if (!Coop.IsHost || Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 0.25f;
            Scan();
            List<Entry> changed = null;
            foreach (var e in _list)
            {
                if (e.Go == null) continue;
                bool on = e.Value;
                if (on == e.Last) continue;
                e.Last = on;
                (changed ??= new()).Add(e);
            }
            if (changed == null) return;
            var w = Begin(); w.VarU((ulong)changed.Count);
            foreach (var e in changed) { w.Str(e.Id); w.Bool(e.Last); }
            WorldReady.SendToReady(Msg.Visibility, w);
            Sent += changed.Count;
        }

        public override void SendSnapshot(byte slot)
        {
            Scan();
            // lights only travel as changes: which lights are on at rest can depend on each player's graphics settings
            var list = _list.FindAll(e => e.L == null);
            for (int at = 0; at < list.Count; at += 256)
            {
                var w = Begin();
                int n = Math.Min(256, list.Count - at);
                w.VarU((ulong)n);
                for (int i = at; i < at + n; i++) { var e = list[i]; e.Last = e.Go != null && e.Value; w.Str(e.Id); w.Bool(e.Last); }
                Coop.Session.Send((ushort)Msg.Visibility, w, Delivery.Reliable, slot);
            }
        }

        void OnMessage(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            Scan();
            int n = r.Count(4096, "visibility states");
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(2048); bool on = r.Bool();
                if (!_byId.TryGetValue(id, out var e) || e.Go == null)
                {
                    if (Time.unscaledTime < _nextForcedScan) continue;   // rescan at most once a second for unknown ids
                    _nextForcedScan = Time.unscaledTime + 1f; _nextScan = 0; Scan();
                    if (!_byId.TryGetValue(id, out e) || e.Go == null) continue;
                }
                e.Last = on;
                if (e.Value == on) continue;
                var ee = e;
                Coop.ApplyVisual("show " + id, () => ee.Set(on));
                Applied++;
            }
        }

        public override string Status => $"{_list.Count} mission objects, sent {Sent}, applied {Applied}";
    }
}
