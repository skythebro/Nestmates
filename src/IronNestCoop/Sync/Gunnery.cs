using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Turret, guns and ammunition. The host's turret is the source of truth: its angle, speed and every
    /// barrel's elevation are sent to guests 10 times a second. A shot is fired once, on the host (a guest's
    /// button presses are forwarded there). Guests play the same fire sequence (recoil, sound, chambered
    /// round removed) but launch the projectile with the host's exact start, target and flight times
    /// instead of their own random dispersion. Reload steps, cylinder turns, powder selection and resupply
    /// are replayed on guests, and a gun-state digest triggers a full resync if anything still differs.
    /// </summary>
    public sealed class Gunnery : SyncModule
    {
        public override string Name => "gunnery";
        public static Gunnery Instance { get; private set; }

        enum Ev : byte { FireRequest = 1, Shot = 2, Reload = 3, CylinderTurn = 4, Charge = 5, LoadCharges = 6, InsertShell = 7, Digest = 8, Full = 9, AutoReload = 10 }

        struct ShotData { public Vector2 Start, Target; public float Duration; public string ShellId; public bool Immune; public int Tracking; public double StartedAt, EndsAt; }

        // host capture
        static GunController _capturingGun;
        static readonly List<ShotData> _captured = new();
        // guest replay
        readonly Dictionary<int, Queue<ShotData>> _pending = new();
        readonly Dictionary<int, float> _awaitingSince = new();   // the local fire sequence is paused at FireShell, waiting for the host's shot
        readonly Dictionary<int, float> _fallbackAt = new();      // the host's shot has arrived, but the local sequence has not reached FireShell yet
        static int _replayGun = -1;
        static bool _replayWasApplying;
        float _nextState, _nextDigest, _lastResyncAsk;

        public int ShotsReplayed, ShotsSent, ReloadsApplied, DigestMismatches, Resyncs;
        /// <summary>The last shot sent (host) or launched from host data (guest), for tests and logs.</summary>
        public string LastShot = "";

        static string Describe(ShotData s) =>
            $"{s.ShellId} {s.Start.x:F3},{s.Start.y:F3}>{s.Target.x:F3},{s.Target.y:F3} {s.Duration:F3}s @{s.StartedAt:F3}-{s.EndsAt:F3}";

        public Gunnery() { Instance = this; }

        /// <summary>A shot queued in one session must not pair with the next session's shots.</summary>
        public override void OnSessionStart()
        {
            _pending.Clear(); _awaitingSince.Clear(); _fallbackAt.Clear(); _captured.Clear();
            _replayGun = -1; _capturingGun = null; LastShot = "";
            _hostShown.Clear(); _fromHost.Clear(); _watches = null;
        }
        public override void OnSessionEnd() => OnSessionStart();

        public override void Register(Session s)
        {
            s.On((ushort)Msg.ShotFired, OnEvent);
            s.On((ushort)Msg.TurretState, OnTurretState);
            s.On((ushort)Msg.MissionStats, OnStats);
        }

        static TurretController T => TurretController.Instance;

        static int GunIndex(GunController g)
        {
            var t = T;
            if (t?.guns == null || g == null) return -1;
            for (int i = 0; i < t.guns.Count; i++) if (t.guns[i] != null && t.guns[i].Pointer == g.Pointer) return i;
            return -1;
        }

        static GunController Gun(int i) { var t = T; return t?.guns != null && i >= 0 && i < t.guns.Count ? t.guns[i] : null; }

        static NetWriter Begin(Ev ev) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); return w; }
        static void Send(NetWriter w) => WorldReady.SendToReady(Msg.ShotFired, w);
        static bool HostCapture => Coop.HostEmits;

        // ------------------------------------------------------------------ turret state (host to guests)

        public override void Tick(float dt)
        {
            var t = T;
            if (Coop.IsHost && t != null && Time.unscaledTime >= _nextState)
            {
                _nextState = Time.unscaledTime + 0.1f;
                var w = Coop.Writer();
                w.VarU((ulong)MissionFlow.Instance.Epoch);
                w.F32(t._CurrentAngle_k__BackingField); w.F32(t._DesiredRotation_k__BackingField); w.F32(t._DesiredElevation_k__BackingField);
                w.F32(t.rotationVelocity); w.F32(t.desiredRotationVelocityTarget);
                int n = t.guns?.Count ?? 0;
                w.U8((byte)n);
                for (int i = 0; i < n; i++)
                {
                    var g = t.guns[i];
                    w.F32(g._CurrentElevation_k__BackingField); w.F32(g.internalDesiredElevation); w.F32(g.elevationChangeVelocity);
                }
                WriteWatches(w);   // added in 1.0.1: older readers stop before it
                WorldReady.SendToReady(Msg.TurretState, w, Delivery.Unreliable);
            }
            if (Coop.IsHost) SendStats();
            if (Coop.IsHost && t != null && Time.unscaledTime >= _nextDigest)
            {
                _nextDigest = Time.unscaledTime + 2f;
                var w = Begin(Ev.Digest); w.Str(GunDigest()); Send(w);
            }
            if (Coop.IsGuest)
            {
                // The host's shot arrived but the local fire sequence never reached FireShell (for example, this guest
                // joined mid-sequence). Launch it anyway.
                foreach (var kv in new List<KeyValuePair<int, float>>(_fallbackAt))
                    if (Time.unscaledTime >= kv.Value)
                    {
                        _fallbackAt.Remove(kv.Key);
                        if (_pending.TryGetValue(kv.Key, out var q) && q.Count > 0) ReplayShot(kv.Key);
                    }
                foreach (var kv in new List<KeyValuePair<int, float>>(_awaitingSince))
                    if (Time.unscaledTime - kv.Value > 5f) _awaitingSince.Remove(kv.Key);
            }
        }

        // ------------------------------------------------------------------ stopwatches (host to guests)

        // The stopwatch shows the predicted shell flight time and then counts it down after a shot. Each machine
        // used to work that out on its own, from its own prediction and its own start time, so a guest's watch
        // could be 10-15 s off the host's (worse after a pause). Players time their shots with it, so the host
        // now sends what its watches show ten times a second and guests show exactly that.

        struct WatchShown { public float Seconds; public bool CountingDown; public float At; }

        static readonly Dictionary<IntPtr, float> _hostShown = new();       // host: the last value each watch showed
        static readonly Dictionary<int, WatchShown> _fromHost = new();      // guest: what the host's watch shows, by gun
        static List<GunStopwatch> _watches;
        static float _nextWatchScan;

        static List<GunStopwatch> Watches()
        {
            if (_watches == null || Time.unscaledTime >= _nextWatchScan)
            {
                _nextWatchScan = Time.unscaledTime + 5f;
                _watches = new List<GunStopwatch>();
                foreach (var o in UnityEngine.Object.FindObjectsOfType<GunStopwatch>()) if (o != null) _watches.Add(o);
            }
            return _watches;
        }

        static void WriteWatches(NetWriter w)
        {
            var list = LaunchArgs.Mutant("nowatchsync") ? new List<GunStopwatch>() : Watches();
            var send = new List<(int gun, float secs, bool down)>();
            foreach (var sw in list)
            {
                if (sw == null || !_hostShown.TryGetValue(sw.Pointer, out float secs)) continue;
                int gun = GunIndex(sw.watchedGun);
                if (gun < 0 || gun > 250) continue;
                send.Add((gun, secs, sw.currentState == GunStopwatch.WatchState.CountingDown));
            }
            w.U8((byte)send.Count);
            foreach (var x in send) { w.U8((byte)x.gun); w.F32(x.secs); w.Bool(x.down); }
        }

        static void ReadWatches(NetReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                int gun = r.U8(); float secs = r.F32(); bool down = r.Bool();
                if (!float.IsFinite(secs) || secs < 0f || secs > 3600f) continue;
                _fromHost[gun] = new WatchShown { Seconds = secs, CountingDown = down, At = Time.unscaledTime };
            }
        }

        /// <summary>Tests: what this machine's stopwatch for a gun shows right now (-1: none).</summary>
        public static float WatchSeconds(int gun)
        {
            foreach (var sw in Watches())
                if (sw != null && GunIndex(sw.watchedGun) == gun) return sw.GetDisplaySeconds();
            return -1f;
        }

        [HarmonyPatch(typeof(GunStopwatch), "GetDisplaySeconds")]
        static class WatchDisplayPatch
        {
            static void Postfix(GunStopwatch __instance, ref float __result)
            {
                try
                {
                    if (!Coop.InSession) return;
                    if (Coop.IsHost) { _hostShown[__instance.Pointer] = __result; return; }
                    if (!_fromHost.TryGetValue(GunIndex(__instance.watchedGun), out var h)) return;
                    float age = Time.unscaledTime - h.At;
                    if (age > 1.5f) return;   // the host went quiet: the watch keeps its own time
                    __result = h.CountingDown ? Mathf.Max(0f, h.Seconds - Mathf.Min(age, 0.3f)) : h.Seconds;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.WatchDisplayPatch", e); }
            }
        }

        // ------------------------------------------------------------------ mission statistics (host to guests)

        // Shots, hits, misses, destroyed targets and the rest of the end-of-mission numbers are counted by mission
        // logic that only runs on the host, so a guest's end screen stayed empty. The host sends its mission numbers
        // whenever they change (and every few seconds anyway); guests show those. Each player's own campaign totals
        // are left alone.

        string _statsSent = ""; float _nextStats, _statsForce;
        public int StatsApplied;

        static float[] StatsOf(MissionStatsTracker.Stats m) => new float[]
        {
            m.shotsFired, m.targetsDestroyed, m.hitsOnTargets, m.missedShots, m.missionTime, m.accuracy, m.directHits, m.hitStreak, m.maxHitStreak,
        };

        void SendStats()
        {
            var st = MissionStatsTracker.Instance;
            if (st == null || st.mission == null || Time.unscaledTime < _nextStats || LaunchArgs.Mutant("nostatsync")) return;
            _nextStats = Time.unscaledTime + 0.5f;
            var v = StatsOf(st.mission);
            // mission time ticks every frame: it does not count as a change on its own
            string key = string.Join("/", v.Take(4)) + "/" + string.Join("/", v.Skip(5));
            if (key == _statsSent && Time.unscaledTime < _statsForce) return;
            _statsSent = key; _statsForce = Time.unscaledTime + 5f;
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.U8((byte)v.Length);
            foreach (var f in v) w.F32(f);
            WorldReady.SendToReady(Msg.MissionStats, w);
        }

        void OnStats(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            int n = r.U8();
            var v = new float[n];
            for (int i = 0; i < n; i++) v[i] = r.F32();
            if (n < 9 || v.Any(f => !float.IsFinite(f) || f < 0f || f > 1e7f)) return;
            var st = MissionStatsTracker.Instance;
            if (st == null || st.mission == null) return;
            Coop.Apply("mission stats", () =>
            {
                var m = st.mission;
                m.shotsFired = (int)v[0]; m.targetsDestroyed = (int)v[1]; m.hitsOnTargets = (int)v[2]; m.missedShots = (int)v[3];
                m.missionTime = v[4]; m.accuracy = v[5]; m.directHits = (int)v[6]; m.hitStreak = (int)v[7]; m.maxHitStreak = (int)v[8];
                st.UpdateMissionOdometers();
            });
            StatsApplied++;
        }

        void OnTurretState(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            var t = T;
            if (t == null) return;
            float angle = r.F32(), desired = r.F32(), desiredElev = r.F32(), vel = r.F32(), velTarget = r.F32();
            float lead = (Coop.Session.GetPlayer(0)?.RttMs ?? 0) / 2000f;
            float predicted = angle + vel * lead;
            Coop.Apply("turret state", () =>
            {
                // Tight when still (land exactly on the host's aim), looser while slewing (no visible jitter).
                float deadband = Mathf.Abs(vel) < 0.01f ? 0.02f : 0.3f;
                if (Mathf.Abs(Mathf.DeltaAngle(t._CurrentAngle_k__BackingField, predicted)) > deadband) t._CurrentAngle_k__BackingField = predicted;
                t._DesiredRotation_k__BackingField = desired;
                t._DesiredElevation_k__BackingField = desiredElev;
                t.rotationVelocity = vel;
                t.desiredRotationVelocityTarget = velTarget;
            });
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                float cur = r.F32(), want = r.F32(), v = r.F32();
                var g = Gun(i);
                if (g == null) continue;
                Coop.Apply("gun elevation", () =>
                {
                    float eb = Mathf.Abs(v) < 0.01f ? 0.01f : 0.1f;
                    if (Mathf.Abs(g._CurrentElevation_k__BackingField - cur) > eb) g._CurrentElevation_k__BackingField = cur;
                    g.internalDesiredElevation = want;
                    g.elevationChangeVelocity = v;
                });
            }
            if (r.Remaining > 0) ReadWatches(r);
        }

        // ------------------------------------------------------------------ gun digest and full resync

        static string GunDigest()
        {
            var sb = new System.Text.StringBuilder();
            var t = T;
            if (t?.guns == null) return "";
            foreach (var g in t.guns)
            {
                var rc = g.artilleryReloadController;
                sb.Append(rc != null ? rc.currentStateIndex : -1).Append('/')
                  .Append(g.ChamberedShellBlueprint?.shellDefinition?.ShellId ?? "-").Append(':');
                var cyl = rc?.cylinderShellSelector;
                if (cyl?.bullets != null)
                    foreach (var b in cyl.bullets) sb.Append(b == null ? "-" : (b.GetComponent<ShellBlueprint>()?.shellDefinition?.ShellId ?? "?")).Append(',');
                sb.Append('|');
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<PowderChargeController>()))
                {
                    var p = o.TryCast<PowderChargeController>();
                    if (p != null && rc != null && p.reloadController != null && p.reloadController.Pointer == rc.Pointer) sb.Append(p.currentSelectedCharges).Append('|');
                }
                sb.Append(';');
            }
            return sb.ToString();
        }

        public static string CurrentDigest => GunDigest();

        public override void SendSnapshot(byte slot)
        {
            var t = T;
            if (t == null) return;
            var w = Begin(Ev.Full); w.Str(GameJson.Write(t.CaptureMissionState()));
            Coop.Session.Send((ushort)Msg.ShotFired, w, Delivery.Reliable, slot);
        }

        // ------------------------------------------------------------------ guest apply

        void OnEvent(in MsgContext c, NetReader r)
        {
            if (Coop.IsHost)
            {
                // Guests only ask for a resync or toggle auto-reload (which then runs here, on the real gun).
                if ((int)r.VarU() != MissionFlow.Instance.Epoch) return;
                var hev = (Ev)r.U8();
                if (hev == Ev.Full) SendSnapshot(c.FromSlot);
                else if (hev == Ev.AutoReload)
                {
                    bool on = r.Bool();
                    var arm = UnityEngine.Object.FindObjectOfType<AutoReloadManager>();
                    if (arm != null) Coop.Apply("auto reload", () => arm.SetAutoReload(on));
                }
                return;
            }
            if (!c.FromHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            var ev = (Ev)r.U8();
            switch (ev)
            {
                case Ev.FireRequest:
                {
                    var g = Gun(r.U8());
                    if (g != null) Coop.Apply("fire request", () => g.RequestFire());
                    break;
                }
                case Ev.Shot:
                {
                    int gi = r.U8(); int n = r.U8();
                    if (!_pending.TryGetValue(gi, out var q)) _pending[gi] = q = new Queue<ShotData>();
                    for (int i = 0; i < n; i++)
                        q.Enqueue(new ShotData
                        {
                            Start = new Vector2(r.F32(), r.F32()), Target = new Vector2(r.F32(), r.F32()), Duration = r.F32(),
                            ShellId = r.Str(128), Immune = r.Bool(), Tracking = (int)r.VarI(), StartedAt = r.F64(), EndsAt = r.F64(),
                        });
                    // If the local fire sequence is already waiting at FireShell, launch now. Otherwise
                    // FireShellDelayed launches it once it gets there, or the fallback does.
                    if (_awaitingSince.Remove(gi)) ReplayShot(gi);
                    else _fallbackAt[gi] = Time.unscaledTime + 1.5f;
                    break;
                }
                case Ev.Reload:
                {
                    var rc = FindComp<ArtilleryReloadController>(r.Str(2048));
                    int idx = (int)r.VarI(); bool anim = r.Bool();
                    if (rc != null && rc.currentStateIndex != idx) { ReloadsApplied++; Coop.Apply("reload state", () => rc.SetState(idx, true, anim)); }
                    break;
                }
                case Ev.CylinderTurn:
                {
                    var cyl = FindComp<CylinderShellSelector>(r.Str(2048));
                    if (cyl != null) Coop.Apply("cylinder turn", () => cyl.OnMoveButtonClicked());
                    break;
                }
                case Ev.Charge:
                {
                    var p = FindComp<PowderChargeController>(r.Str(2048)); int idx = (int)r.VarI();
                    if (p != null) Coop.Apply("charge button", () => p.OnChargeButtonPressed(idx));
                    break;
                }
                case Ev.LoadCharges:
                {
                    var p = FindComp<PowderChargeController>(r.Str(2048));
                    if (p != null) Coop.Apply("load charges", () => p.OnLoadChargesPressed());
                    break;
                }
                case Ev.InsertShell:
                {
                    var cyl = FindComp<CylinderShellSelector>(r.Str(2048));
                    var shell = MissionSaveManager.FindShell(r.Str(128));
                    var src = (ShellSlotPool.ShellSource)(int)r.VarI();
                    if (cyl != null && shell != null) Coop.Apply("insert shell", () => cyl.TryInsertShellRuntime(shell, src, out _));
                    break;
                }
                case Ev.Digest:
                {
                    string host = r.Str(65536), mine = GunDigest();
                    if (host != mine && T != null)
                    {
                        DigestMismatches++;
                        CoopLog.Info($"[gunnery] gun state differs (host {host} / here {mine}); asking for a resync");
                        AskResync();
                    }
                    break;
                }
                case Ev.AutoReload:
                {
                    bool on = r.Bool();
                    var arm = UnityEngine.Object.FindObjectOfType<AutoReloadManager>();
                    if (arm != null) Coop.Apply("auto reload", () => arm.SetAutoReload(on));   // updates the UI state only; Update is gated on this machine
                    break;
                }
                case Ev.Full:
                {
                    var data = GameJson.Read<TurretSaveData>(r.Str(1 << 22));
                    var t = T;
                    if (t != null && data != null) { Resyncs++; Coop.Apply("turret full state", () => t.RestoreMissionState(data)); }
                    break;
                }
            }
        }

        void AskResync()
        {
            if (Time.unscaledTime - _lastResyncAsk < 5f) return;
            _lastResyncAsk = Time.unscaledTime;
            var w = Begin(Ev.Full);
            Coop.SendToHost(Msg.ShotFired, w);
        }

        static T FindComp<T>(string path) where T : Component
        {
            var tr = ScenePath.Find(path);
            return tr != null ? tr.GetComponent<T>() : null;
        }

        void ReplayShot(int gi)
        {
            var g = Gun(gi);
            if (g == null) return;
            _replayGun = gi;
            _replayWasApplying = true;   // the postfix restores Applying, and Apply() restores it again on exit
            try { Coop.Apply("replay shot", () => g.FireShell()); }
            finally { _replayGun = -1; }
        }

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(GunController), nameof(GunController.RequestFire))]
        static class RequestFirePatch
        {
            static void Postfix(GunController __instance)
            {
                try
                {
                    if (!HostCapture) return;
                    var w = Begin(Ev.FireRequest); w.U8((byte)GunIndex(__instance)); Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 324", e); }
            }
        }

        [HarmonyPatch(typeof(GunController), nameof(GunController.FireShell))]
        static class FireShellPatch
        {
            static bool Prefix(GunController __instance)
            {
                try
                {
                    if (Coop.IsHost) { _capturingGun = __instance; _captured.Clear(); return true; }
                    if (!Coop.IsGuest) return true;
                    int gi = GunIndex(__instance);
                    if (_replayGun == gi) return true;                         // we are replaying the host's data, so let the original run
                    var inst = Instance;
                    if (inst != null && inst._pending.TryGetValue(gi, out var q) && q.Count > 0)
                    {
                        inst._fallbackAt.Remove(gi);
                        _replayGun = gi;                                          // the host's shot data has already arrived
                        _replayWasApplying = Coop.Applying; Coop.Applying = true; // let the shot's own reload step and similar code run
                        return true;
                    }
                    inst?._awaitingSince.TryAdd(gi, Time.unscaledTime);           // wait for the host's shot
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Prefix line 334", e); return true; }
            }

            /// <summary>Runs even if FireShell throws, so a guest is never left stuck in Applying.</summary>
            static Exception Finalizer(Exception __exception, GunController __instance)
            {
                if (Coop.IsGuest && _replayGun >= 0 && _replayGun == GunIndex(__instance))
                {
                    _replayGun = -1; Instance.ShotsReplayed++;
                    Coop.Applying = _replayWasApplying; _replayWasApplying = false;
                }
                if (Coop.IsHost) _capturingGun = null;
                return __exception;
            }

            static void Postfix(GunController __instance)
            {
                try
                {
                    if (!Coop.IsHost || _capturingGun == null) return;
                    _capturingGun = null;
                    var w = Begin(Ev.Shot);
                    w.U8((byte)GunIndex(__instance)); w.U8((byte)_captured.Count);
                    foreach (var s in _captured)
                    {
                        w.F32(s.Start.x); w.F32(s.Start.y); w.F32(s.Target.x); w.F32(s.Target.y); w.F32(s.Duration);
                        w.Str(s.ShellId); w.Bool(s.Immune); w.VarI(s.Tracking); w.F64(s.StartedAt); w.F64(s.EndsAt);
                    }
                    Send(w);
                    Instance.ShotsSent++;
                    Instance.LastShot = string.Join(" | ", _captured.ConvertAll(Describe));
                    CoopLog.Debug($"[gunnery] shot from gun {GunIndex(__instance)}: {Instance.LastShot}");
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 364", e); }
            }
        }

        [HarmonyPatch(typeof(ShellVisual), nameof(ShellVisual.Initialize))]
        static class ShellInitPatch
        {
            static void Prefix(ref Vector2 startPos, ref Vector2 targetPos, ref float travelDuration, ref bool isShellCounterBatteryImmune, ref ImpactTrackingType trackingType, out ShotData? __state)
            {
                __state = null;
                try
                {
                    if (!Coop.IsGuest || _replayGun < 0) return;
                    var inst = Instance;
                    if (inst == null || !inst._pending.TryGetValue(_replayGun, out var q) || q.Count == 0) return;
                    var s = q.Dequeue();
                    startPos = s.Start; targetPos = s.Target; travelDuration = s.Duration;
                    isShellCounterBatteryImmune = s.Immune; trackingType = (ImpactTrackingType)s.Tracking;
                    __state = s;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.ShellInitPatch.Prefix", e); __state = null; }
            }

            static void Postfix(ShellVisual __instance, Vector2 startPos, Vector2 targetPos, float travelDuration, ShellDefinition shell,
                                bool isShellCounterBatteryImmune, ImpactTrackingType trackingType, ShotData? __state)
            {
                try
                {
                    if (__state is ShotData s)
                    {
                        // Same flight times as the host's shell (mission clocks are aligned).
                        __instance.startedAt = s.StartedAt; __instance.endsAt = s.EndsAt;
                        if (Instance != null) Instance.LastShot = Describe(s);
                        CoopLog.Debug($"[gunnery] launched host shot: {Describe(s)}");
                        return;
                    }
                    if (Coop.IsHost && _capturingGun != null)
                        _captured.Add(new ShotData
                        {
                            Start = startPos, Target = targetPos, Duration = travelDuration, ShellId = shell?.ShellId,
                            Immune = isShellCounterBatteryImmune, Tracking = (int)trackingType,
                            StartedAt = __instance.startedAt, EndsAt = __instance.endsAt,
                        });
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.ShellInitPatch.Postfix", e); }
            }
        }

        [HarmonyPatch(typeof(ArtilleryReloadController), nameof(ArtilleryReloadController.SetState))]
        static class ReloadPatch
        {
            /// <summary>Guests never advance their own reload sequence (buttons, animation events, elevation).</summary>
            static bool Prefix()
            {
                try { return !GraphGate.Suppress; }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Prefix line 422", e); return true; }
            }

            static void Postfix(ArtilleryReloadController __instance, int newIndex, bool triggerAnimations)
            {
                try
                {
                    if (!HostCapture) return;
                    var w = Begin(Ev.Reload); w.Str(ScenePath.Of(__instance.transform)); w.VarI(newIndex); w.Bool(triggerAnimations); Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 424", e); }
            }
        }

        /// <summary>
        /// Auto-reload presses the gun's buttons by itself. On a guest those presses would be forwarded and
        /// race the host's own state, so a guest's toggle is sent to the host and only the host's manager runs.
        /// </summary>
        [HarmonyPatch(typeof(AutoReloadManager), nameof(AutoReloadManager.SetAutoReload))]
        static class AutoReloadToggle
        {
            static bool Prefix(bool enabled)
            {
                try
                {
                    if (!GraphGate.Suppress) return true;
                    var w = Begin(Ev.AutoReload); w.Bool(enabled); Coop.SendToHost(Msg.ShotFired, w);
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Prefix line 438", e); return true; }
            }

            static void Postfix(bool enabled)
            { try { if (Coop.IsHost) { var w = Begin(Ev.AutoReload); w.Bool(enabled); Send(w); } } catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 469", e); } }
        }

        [HarmonyPatch(typeof(AutoReloadManager), "Update")]
        static class AutoReloadUpdate { static bool Prefix() => !Coop.IsGuest; }

        [HarmonyPatch(typeof(AutoReloadManager), nameof(AutoReloadManager.StartAutoReload))]
        static class AutoReloadStart { static bool Prefix() => !Coop.IsGuest; }

        [HarmonyPatch(typeof(CylinderShellSelector), nameof(CylinderShellSelector.OnMoveButtonClicked))]
        static class CylinderPatch
        {
            static void Postfix(CylinderShellSelector __instance)
            { try { if (HostCapture) { var w = Begin(Ev.CylinderTurn); w.Str(ScenePath.Of(__instance.transform)); Send(w); } } catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 482", e); } }
        }

        [HarmonyPatch(typeof(PowderChargeController), nameof(PowderChargeController.OnChargeButtonPressed))]
        static class ChargePatch
        {
            static void Postfix(PowderChargeController __instance, int index)
            { try { if (HostCapture) { var w = Begin(Ev.Charge); w.Str(ScenePath.Of(__instance.transform)); w.VarI(index); Send(w); } } catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 489", e); } }
        }

        [HarmonyPatch(typeof(PowderChargeController), nameof(PowderChargeController.OnLoadChargesPressed))]
        static class LoadChargesPatch
        {
            static void Postfix(PowderChargeController __instance)
            { try { if (HostCapture) { var w = Begin(Ev.LoadCharges); w.Str(ScenePath.Of(__instance.transform)); Send(w); } } catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 496", e); } }
        }

        [HarmonyPatch(typeof(CylinderShellSelector), nameof(CylinderShellSelector.TryInsertShellRuntime))]
        static class InsertPatch
        {
            static void Postfix(CylinderShellSelector __instance, ShellDefinition shell, ShellSlotPool.ShellSource source, bool __result)
            {
                try
                {
                    if (!HostCapture || !__result || shell == null) return;
                    var w = Begin(Ev.InsertShell); w.Str(ScenePath.Of(__instance.transform)); w.Str(shell.ShellId); w.VarI((int)source); Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Gunnery.Postfix line 479", e); }
            }
        }
    }
}
