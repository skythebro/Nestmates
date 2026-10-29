using System;
using System.Collections.Generic;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using SleepyNodes;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Everything else the mission logic does, captured on the host where the game applies it and
    /// replayed on guests with the same calls: teleprinter text, UI and scene notifications, mission
    /// complete/failed, the counter-battery timer, requisition points and powder charges, turret
    /// relocation, scout planes and scripted (enemy) impacts. Guests never decide any of these.
    /// </summary>
    public sealed class WorldEvents : SyncModule
    {
        public override string Name => "world";
        public static WorldEvents Instance { get; private set; }

        enum Ev : byte
        {
            TeleLines = 1, TeleClear = 2, TeleAlarm = 3, TeleClearAlarm = 4,
            UiNote = 10, SceneNote = 11,
            MissionComplete = 20, MissionFailed = 21,
            Timer = 30,
            Economy = 40,
            TurretMove = 50, TurretPlace = 51,
            ScoutPlane = 60, ScriptedImpact = 61, MapObjects = 62, MapArms = 63, ArmReset = 64,
            TelePrinterState = 70, TeleBlocks = 71,
            PrefabSpawn = 80,
            GenericTimers = 90,
        }

        /// <summary>Host: what each teleprinter printed since its last clear, with how to render it (for guests in another language).</summary>
        sealed class TeleBlock { public string Source, Key; public bool CanRender; public List<(string, string)> Repl = new(); public string Loc; }
        static readonly Dictionary<int, List<TeleBlock>> History = new();
        static int _historyEpoch = -1;   // the history belongs to one mission

        static void HistoryForThisMission()
        {
            int epoch = MissionFlow.Instance.Epoch;
            if (_historyEpoch != epoch) { History.Clear(); _historyEpoch = epoch; }
        }

        float _nextEconomy, _nextTimer;
        int _lastRp = int.MinValue, _lastCharges = int.MinValue;

        /// <summary>Guest counters for tests.</summary>
        public int Applied;

        public WorldEvents() { Instance = this; if (!_ruleHooked) { _ruleHooked = true; HostRules.Changed += OnTimerRule; } }

        public override void Register(Session s) => s.On((ushort)Msg.Notification, OnEvent);

        /// <summary>Nothing from a previous session: teleprinter text, spawner replays, delivery counts, timers.</summary>
        public override void OnSessionStart()
        {
            History.Clear(); Spawns.Clear(); Scouts.Clear(); _scoutsHere.Clear(); _scoutEpoch = -1; _mapEpoch = -1; _impactParts.Clear(); _corrections.Clear(); _impactLooks.Clear(); _reapplyParts = 99; _deliveries.Clear(); _deliveryEpoch = -1;
            _timerEpoch = -1; _timerShown = false; _lastTimerState = -1; _lastGenericCount = -1;
            _baked = 1f; _pausedByRule = false;
            if (_guestTimer != null) { UnityEngine.Object.Destroy(_guestTimer); _guestTimer = null; }
        }
        public override void OnSessionEnd() => OnSessionStart();

        static NetWriter Begin(Ev ev)
        {
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.U8((byte)ev);
            return w;
        }

        static void Send(NetWriter w) => WorldReady.SendToReady(Msg.Notification, w);

        static bool HostCapture => Coop.HostEmits;

        // ------------------------------------------------------------------ host periodic state

        public override void Tick(float dt)
        {
            ReapplyImpactParts();
            if (!Coop.IsHost) return;
            if (Time.unscaledTime >= _nextEconomy)
            {
                _nextEconomy = Time.unscaledTime + 0.5f;
                int rp = MissionStatsTracker.Instance != null ? MissionStatsTracker.Instance.RequisitionPoints : -1;
                int ch = PowderChargeInventory.Instance != null ? PowderChargeInventory.Instance.CurrentCharges : -1;
                if (rp != _lastRp || ch != _lastCharges) { _lastRp = rp; _lastCharges = ch; SendEconomy(); }
            }
            if (Coop.IsHost && Time.unscaledTime >= _nextGenericTimers) { _nextGenericTimers = Time.unscaledTime + 0.5f; SendGenericTimers(); }
            if (Time.unscaledTime >= _nextTimer)
            {
                _nextTimer = Time.unscaledTime + 0.5f;
                var cb = CounterBatteryTimer.Instance;
                if (cb != null)
                {
                    // every 2 s while it runs, and at once when it starts, stops or runs out (expiry has no call to patch)
                    int state = (cb._running ? 1 : 0) | (cb._expired ? 2 : 0) | (cb._permanentlyStopped ? 4 : 0);
                    if (state != _lastTimerState || (cb._running && Time.unscaledTime >= _nextTimerTick))
                    {
                        _lastTimerState = state; _nextTimerTick = Time.unscaledTime + 2f;
                        SendTimer();
                    }
                }
            }
        }

        float _nextGenericTimers;
        int _lastGenericCount = -1;

        /// <summary>
        /// Host: the mission graph's named timers (FireMission.RunningTimers, filled by State_GenericTimer). The wrist
        /// watch reads them (GenericTimerSceneSync): guests never run that node, so their watch showed 00:00.
        /// </summary>
        void SendGenericTimers(byte? slot = null)
        {
            var fm = FireMission.Instance;
            var timers = fm?.RunningTimers;
            if (timers == null) return;
            var list = new List<(string Id, FireMission.TimerValue V)>();
            foreach (var kv in timers) if (kv.Value != null) list.Add((kv.Key, kv.Value));
            if (list.Count == 0 && _lastGenericCount == 0 && slot == null) return;
            _lastGenericCount = list.Count;
            var w = Begin(Ev.GenericTimers);
            w.VarU((ulong)list.Count);
            w.F64(MissionClock.TimeAsDouble);   // StartedAt is on this clock; each machine's mission clock reads differently
            foreach (var (id, v) in list) { w.Str(id); w.F32(v.InitialSeconds); w.F32(v.CurrentSeconds); w.F64(v.StartedAt); }
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Notification, w, Delivery.Reliable, one);
            else Send(w);
        }

        int _lastTimerState = -1;
        float _nextTimerTick;
        bool _timerShown;   // guest: the timer's start was shown here (display, sound) this mission
        int _timerEpoch = -1;

        static bool _ruleHooked;
        static GameObject _guestTimer;

        /// <summary>The counter-battery timer prefab: the one a State_StartTimer node of the mission graph holds (the
        /// graph is loaded on every machine), else any loaded copy that is not in a scene.</summary>
        internal static CounterBatteryTimer TimerPrefab()
        {
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<SleepyNodes.State_StartTimer>()))
            {
                var p = o.TryCast<SleepyNodes.State_StartTimer>()?.Prefab_BatteryTimer;
                if (p != null) return p;
            }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<CounterBatteryTimer>()))
            {
                var t = o.TryCast<CounterBatteryTimer>();
                if (t != null && string.IsNullOrEmpty(t.gameObject.scene.name)) return t;
            }
            return null;
        }

        /// <summary>Guest: a copy of the counter-battery timer prefab, as the host's State_StartTimer made one.</summary>
        static CounterBatteryTimer SpawnGuestTimer()
        {
            var prefab = TimerPrefab();
            if (prefab == null) { CoopLog.Warn("[world] counter-battery timer prefab not found; the timer display stays shut here"); return null; }
            CounterBatteryTimer made = null;
            Coop.ApplyVisual("spawn counter-battery timer", () =>
            {
                _guestTimer = UnityEngine.Object.Instantiate(prefab.gameObject);
                made = _guestTimer.GetComponent<CounterBatteryTimer>();
            });
            CoopLog.Info("[world] counter-battery timer spawned here (the host's mission started it)");
            return CounterBatteryTimer.Instance ?? made;
        }

        void SendEconomy()
        {
            var w = Begin(Ev.Economy);
            w.VarI(_lastRp); w.VarI(_lastCharges);
            Send(w);
        }

        void SendTimer(byte target = Protocol.TargetAll)
        {
            var cb = CounterBatteryTimer.Instance;
            if (cb == null) return;
            var w = Begin(Ev.Timer);
            w.Str(GameJson.Write(cb.CaptureMissionState()));
            if (target == Protocol.TargetAll) Send(w); else Coop.Session.Send((ushort)Msg.Notification, w, Delivery.Reliable, target);
        }

        public override void SendSnapshot(byte slot)
        {
            void To(NetWriter w) => Coop.Session.Send((ushort)Msg.Notification, w, Delivery.Reliable, slot);
            int rp = MissionStatsTracker.Instance != null ? MissionStatsTracker.Instance.RequisitionPoints : -1;
            int ch = PowderChargeInventory.Instance != null ? PowderChargeInventory.Instance.CurrentCharges : -1;
            var e = Begin(Ev.Economy); e.VarI(rp); e.VarI(ch); To(e);
            SendGenericTimers(slot);
            Spawns.RemoveAll(sp => sp.Epoch != MissionFlow.Instance.Epoch);
            foreach (var sp in Spawns) { var w = Begin(Ev.PrefabSpawn); w.Str(sp.Path); To(w); }
            Scouts.RemoveAll(sc => sc.Epoch != MissionFlow.Instance.Epoch);
            if (!LaunchArgs.Mutant("noscoutsnapshot")) foreach (var sc in Scouts) To(ScoutMessage(sc));   // used by the tests to prove the rejoin check depends on this snapshot
            if (!LaunchArgs.Mutant("nomapsnapshot")) { if (MapObjectsMessage() is NetWriter mo) To(mo); To(MapArmsMessage()); }
            SendTimer(slot);
            foreach (Teleprinter.Teleprinters t in Enum.GetValues(typeof(Teleprinter.Teleprinters)))
            {
                var tp = Teleprinter.GetTeleprinter(t);
                if (tp == null) continue;
                var w = Begin(Ev.TelePrinterState); w.VarI((int)t); w.Str(GameJson.Write(tp.CaptureMissionState()));
                To(w);
                HistoryForThisMission();
                if (History.TryGetValue((int)t, out var blocks) && blocks.Count > 0)
                {
                    var b = Begin(Ev.TeleBlocks); b.VarI((int)t); b.Str(Localisation.LocalisationManager.Instance?.CurrentLanguage); b.VarU((ulong)blocks.Count);
                    foreach (var blk in blocks)
                    {
                        b.Str(blk.Source); b.Str(blk.Key); b.Bool(blk.CanRender);
                        b.VarU((ulong)blk.Repl.Count); foreach (var (ph, val) in blk.Repl) { b.Str(ph); b.Str(val); }
                        b.Str(blk.Loc);
                    }
                    To(b);
                }
            }
            var turret = TurretController.Instance;
            if (turret != null)
            {
                var w = Begin(Ev.TurretPlace);
                var p = turret.turretBase.position;
                w.Vec3(p.x, p.y, p.z);
                To(w);
            }
            var mm = MissionManager.Instance;
            if (mm?.CurrentMissionState != null && (mm.CurrentMissionState.Complete || mm.CurrentMissionState.Failed))
                To(Begin(mm.CurrentMissionState.Complete ? Ev.MissionComplete : Ev.MissionFailed));
        }

        // ------------------------------------------------------------------ guest apply

        void OnEvent(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost) return;
            if ((int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            var ev = (Ev)r.U8();
            Applied++;
            switch (ev)
            {
                case Ev.TeleLines:
                {
                    var type = (Teleprinter.Teleprinters)(int)r.VarI();
                    string source = r.Str(256), lang = r.Str(64), key = r.Str(512);
                    int n = r.Count(512, "teleprinter lines");
                    var lines = new Il2CppSystem.Collections.Generic.List<string>();
                    for (int i = 0; i < n; i++) lines.Add(r.Str(8192));
                    // how the host built the text: placeholder values (entity ids, language-free) + location context
                    bool canRender = r.Bool();
                    CoopLog.Debug($"[events] live lines {key} in {lang}, recipe {canRender}");
                    var repl = new List<(string, string)>();
                    string locJson = null;
                    if (canRender)
                    {
                        int k = r.Count(64, "replacements");
                        for (int i = 0; i < k; i++) repl.Add((r.Str(256), r.Str(1024)));
                        locJson = r.Str(1 << 16);
                    }
                    var mine = canRender ? RenderHere(lang, key, repl, locJson) : null;
                    if (mine != null) { lines = mine; Rerendered++; }
                    var tp = Teleprinter.GetTeleprinter(type);
                    if (tp != null) Coop.Apply("teleprinter", () => tp.SubmitLines(source, lines.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>()));
                    break;
                }
                case Ev.TeleClear:
                {
                    var tp = Teleprinter.GetTeleprinter((Teleprinter.Teleprinters)(int)r.VarI());
                    if (tp != null) Coop.Apply("teleprinter clear", () => tp.ClearAll());
                    break;
                }
                case Ev.TeleAlarm:
                {
                    var tp = Teleprinter.GetTeleprinter((Teleprinter.Teleprinters)(int)r.VarI());
                    var st = (Teleprinter.TeleprinterAlarmState)(int)r.VarI();
                    if (tp != null) Coop.Apply("teleprinter alarm", () => tp.SignalAlarm(st));
                    break;
                }
                case Ev.TeleClearAlarm:
                {
                    var tp = Teleprinter.GetTeleprinter((Teleprinter.Teleprinters)(int)r.VarI());
                    if (tp != null) Coop.Apply("teleprinter clear alarm", () => tp.ClearAlarm());
                    break;
                }
                case Ev.TeleBlocks:
                {
                    var tp = Teleprinter.GetTeleprinter((Teleprinter.Teleprinters)(int)r.VarI());
                    string lang = r.Str(64);
                    int n = r.Count(4096, "teleprinter blocks");
                    CoopLog.Debug($"[events] teleprinter history: {n} blocks in {lang}");
                    var rendered = new List<(string source, Il2CppSystem.Collections.Generic.List<string> lines)>();
                    bool all = true;
                    for (int i = 0; i < n; i++)
                    {
                        string source = r.Str(256), key = r.Str(512); bool can = r.Bool();
                        int k = r.Count(64, "replacements");
                        var repl = new List<(string, string)>();
                        for (int j = 0; j < k; j++) repl.Add((r.Str(256), r.Str(1024)));
                        string loc = r.Str(1 << 16);
                        var lines = can ? RenderHere(lang, key, repl, loc) : null;
                        if (lines == null) all = false; else rendered.Add((source, lines));
                    }
                    // only if every block renders here: reprint the history in this player's language
                    if (tp == null || !all || rendered.Count == 0) break;
                    Coop.Apply("teleprinter history", () =>
                    {
                        tp.ClearAll();
                        foreach (var (source, lines) in rendered) tp.SubmitLines(source, lines.Cast<Il2CppSystem.Collections.Generic.IEnumerable<string>>());
                    });
                    Rerendered += rendered.Count;
                    break;
                }
                case Ev.TelePrinterState:
                {
                    var tp = Teleprinter.GetTeleprinter((Teleprinter.Teleprinters)(int)r.VarI());
                    var data = GameJson.Read<TeleprinterSaveData>(r.Str(4 << 20));
                    if (tp != null && data != null) Coop.Apply("teleprinter state", () => tp.RestoreMissionState(data));
                    break;
                }
                case Ev.UiNote:
                {
                    var node = Nodes.Find(r.Str(128))?.TryCast<State_SendUINotification>();
                    if (node == null) { CoopLog.Warn("[world] notification node not found"); break; }
                    Coop.Apply("ui notification", () =>
                        UINotificationManager.ShowNotification(node.Text_Title?.Get(), node.Text_Description?.Get(), node.Duration, new Il2CppSystem.Nullable<Color>(node.Tint)));
                    break;
                }
                case Ev.SceneNote:
                {
                    string path = r.Str(2048), message = r.Str(512);
                    var t = ScenePath.Find(path);
                    var l = t != null ? t.GetComponent<MissionGraphNotificationListener>() : null;
                    if (l == null) { CoopLog.Warn($"[world] scene listener not found: {path}"); break; }
                    Coop.Apply("scene notification", () => l.Trigger(message));
                    break;
                }
                case Ev.MissionComplete: Coop.Apply("mission complete", () => MissionManager.Instance.MarkMissionComplete(true)); break;
                case Ev.MissionFailed: Coop.Apply("mission failed", () => MissionManager.Instance.MarkMissionFailed(true)); break;
                case Ev.Timer:
                {
                    var data = GameJson.Read<CounterBatterySaveData>(r.Str(65536));
                    if (data == null) break;
                    if (_timerEpoch != MissionFlow.Instance.Epoch)
                    {
                        _timerEpoch = MissionFlow.Instance.Epoch; _timerShown = false;
                        if (_guestTimer != null) { UnityEngine.Object.Destroy(_guestTimer); _guestTimer = null; }   // last mission's copy
                    }
                    var cb = CounterBatteryTimer.Instance;
                    // the host's mission graph spawns the timer (State_StartTimer); guests never run that node, so the
                    // display had nothing to show and its flap stayed shut: spawn the same prefab here
                    if (cb == null && data.Exists) cb = SpawnGuestTimer();
                    if (cb == null) break;
                    bool wasRunning = cb._running, wasExpired = cb._expired, wasStopped = cb._permanentlyStopped;
                    Coop.Apply("timer", () => cb.RestoreMissionState(data));
                    // Restoring sets the numbers only; the display, its sounds and the end of the timer listen to the
                    // timer's events, which on the host the mission graph's StartTimer etc. fire. Replay them here.
                    if (data.Running && !wasRunning)
                    {
                        var shownEv = _timerShown ? cb.onTimerUnpaused : cb.onTimerStarted;
                        _timerShown = true;
                        Coop.ApplyVisual("timer shown", () => shownEv?.Invoke());
                    }
                    else if (!data.Running && wasRunning && !data.Expired && !data.PermanentlyStopped)
                        Coop.ApplyVisual("timer paused", () => cb.onTimerPaused?.Invoke());
                    if (data.PermanentlyStopped && !wasStopped) Coop.ApplyVisual("timer stopped", () => cb.onTimerPermanentlyStopped?.Invoke());
                    if (data.Expired && !wasExpired) Coop.ApplyVisual("timer ran out", () => cb.onTimerExpired?.Invoke());
                    break;
                }
                case Ev.GenericTimers:
                {
                    int n = r.Count(256, "scout photos");
                    var fm = FireMission.Instance;
                    var timers = fm?.RunningTimers;
                    var seen = new HashSet<string>();
                    // FireMission.Update recounts every timer here from MissionClock - StartedAt: the host's start
                    // time is moved onto this machine's mission clock, or the watch runs seconds off the host's
                    double hostClock = r.F64(), shift = MissionClock.TimeAsDouble - hostClock;
                    if (!double.IsFinite(shift)) shift = 0;
                    for (int i = 0; i < n; i++)
                    {
                        string id = r.Str(256); float init = r.F32(), cur = r.F32(); double started = r.F64() + shift;
                        seen.Add(id);
                        if (timers == null) continue;
                        if (!timers.TryGetValue(id, out var v) || v == null) { v = new FireMission.TimerValue(); timers[id] = v; }
                        v.InitialSeconds = init; v.CurrentSeconds = cur; v.StartedAt = started;
                    }
                    if (timers != null)
                    {
                        var gone = new List<string>();
                        foreach (var kv in timers) if (!seen.Contains(kv.Key)) gone.Add(kv.Key);
                        foreach (var id in gone) timers.Remove(id);
                    }
                    break;
                }
                case Ev.Economy:
                {
                    int rp = (int)r.VarI(), ch = (int)r.VarI();
                    Coop.Apply("economy", () =>
                    {
                        if (rp >= 0 && MissionStatsTracker.Instance != null && MissionStatsTracker.Instance.RequisitionPoints != rp)
                            MissionStatsTracker.Instance.SetRequisitionPoints(rp, false);
                        if (ch >= 0 && PowderChargeInventory.Instance != null && PowderChargeInventory.Instance.CurrentCharges != ch)
                            PowderChargeInventory.Instance.CurrentCharges = ch;
                    });
                    break;
                }
                case Ev.TurretMove:
                {
                    var p = new Vector3(r.F32(), r.F32(), r.F32()); bool dodge = r.Bool();
                    var t = TurretController.Instance;
                    if (t != null) Coop.Apply("turret move", () => t.MoveTurret(p, dodge));
                    break;
                }
                case Ev.TurretPlace:
                {
                    var p = new Vector3(r.F32(), r.F32(), r.F32());
                    var t = TurretController.Instance;
                    if (t != null && (t.turretBase.position - p).sqrMagnitude > 1e-6f) Coop.Apply("turret place", () => t.SetTurretLocation(p));
                    break;
                }
                case Ev.MapObjects: ApplyMapObjects(r); break;
                case Ev.MapArms: ApplyMapArms(r); break;
                case Ev.ArmReset:
                {
                    string path = r.Str(2048); bool hostClaimed = r.Bool();
                    var tt = Util.ScenePath.Find(path)?.GetComponent<TrajectorySystem.TrajectoryTarget>();
                    if (tt == null) break;
                    ArmResetsApplied++;
                    Coop.Apply("map arm reset", () =>
                    {
                        // still held here by a shell the host's arm let go of: the host's arm decides
                        if (tt.IsClaimed && !hostClaimed && tt.CurrentOwner != null) tt.Release(tt.CurrentOwner);
                        tt.RequestResetToDefault();
                    });
                    break;
                }
                case Ev.ScoutPlane:
                {
                    string nodeId = r.Str(128), parentPath = r.Str(2048);
                    var pos = new Vector3(r.F32(), r.F32(), r.F32());
                    var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    var node = Nodes.Find(nodeId)?.TryCast<State_SpawnScoutPlane>();
                    if (node == null || node.PlanePrefab == null) { CoopLog.Warn($"[world] scout plane node {nodeId} not found"); break; }
                    // once per flight (the join snapshot replays this mission's flights)
                    if (_scoutEpoch != MissionFlow.Instance.Epoch) { _scoutEpoch = MissionFlow.Instance.Epoch; _scoutsHere.Clear(); }
                    if (!_scoutsHere.Add($"{nodeId}@{pos.x:F2};{pos.z:F2}")) break;
                    Coop.Apply("scout plane", () =>
                    {
                        // like the game's node: place it first, then switch it on. Its parts set themselves up when
                        // enabled (the photo strip's orientation and image, which units it reveals): enabled at the
                        // prefab's default pose they came out turned and showed other units than on the host
                        var parent = ScenePath.Find(parentPath);
                        var prefab = node.PlanePrefab;
                        bool wasActive = prefab.activeSelf;
                        if (!LaunchArgs.Mutant("scoutactive")) prefab.SetActive(false);   // used by the tests to reproduce the old behavior (enabled at the default pose)
                        GameObject go;
                        try { go = parent != null ? UnityEngine.Object.Instantiate(prefab, parent) : UnityEngine.Object.Instantiate(prefab); }
                        finally { prefab.SetActive(wasActive); }
                        go.transform.SetPositionAndRotation(pos, rot);
                        go.SetActive(true);
                        ImpactMarkerManager.Instance?.RegisterScoutPlane(go, nodeId);
                    });
                    break;
                }
                case Ev.PrefabSpawn:
                {
                    // a scene spawner the mission graph fired (the narrative deliveries through the ceiling hatch)
                    string path = r.Str(2048);
                    var sp = ScenePath.Find(path)?.GetComponent<PrefabSpawner>();
                    if (sp == null) { CoopLog.Warn($"[world] spawner {path} not found"); break; }
                    Coop.Apply("spawn " + path, () => sp.Spawn());
                    break;
                }
                case Ev.ScriptedImpact:
                {
                    string nodeId = r.Str(128);
                    var loc = new Vector2(r.F32(), r.F32());
                    var tracking = (ImpactTrackingType)(int)r.VarI();
                    string shellInstance = r.Str(128);
                    var node = Nodes.Find(nodeId)?.TryCast<State_TriggerImpact>();
                    if (node == null) { CoopLog.Warn($"[world] impact node {nodeId} not found"); break; }
                    Coop.Apply("scripted impact", () => node.SpawnImpactPrefab(loc, tracking, shellInstance));
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ host capture patches

        [HarmonyPatch(typeof(Teleprinter), nameof(Teleprinter.SubmitLines))]
        static class TeleLinesPatch
        {
            /// <summary>
            /// The lines argument is a lazy query (the node filters the token processor's output); enumerating it a
            /// second time throws "Collection was modified" (so for a long time live lines were never sent, only join
            /// snapshots carried text). Never enumerate it: inside a node use the processor's own result, otherwise
            /// copy only a concrete list, by index.
            /// </summary>
            static void Postfix(Teleprinter __instance, string sourceId, Il2CppSystem.Collections.Generic.IEnumerable<string> lines)
            {
                try
                {
                    if (!HostCapture) return;
                    List<string> list = TeleText.Current != null ? TeleText.LastLines : null;
                    if (list == null)
                    {
                        var concrete = lines?.TryCast<Il2CppSystem.Collections.Generic.List<string>>();
                        if (concrete == null) { CoopLog.Debug($"[events] teleprinter lines from '{sourceId}' not capturable"); return; }
                        list = new List<string>();
                        for (int i = 0; i < concrete.Count; i++) list.Add(concrete[i]);
                    }
                    var w = Begin(Ev.TeleLines);
                    w.VarI((int)__instance.TeleprinterType); w.Str(sourceId);
                    w.Str(Localisation.LocalisationManager.Instance?.CurrentLanguage); w.Str(TeleText.LastKey);
                    w.VarU((ulong)list.Count);
                    foreach (var l in list) w.Str(l);
                    var recipe = TeleText.Recipe();
                    var blk = new TeleBlock { Source = sourceId, Key = TeleText.Current != null ? TeleText.LastKey : null, CanRender = recipe != null, Loc = recipe?.loc };
                    if (recipe != null) blk.Repl.AddRange(recipe.Value.repl);
                    HistoryForThisMission();
                    if (!History.TryGetValue((int)__instance.TeleprinterType, out var hist)) History[(int)__instance.TeleprinterType] = hist = new List<TeleBlock>();
                    hist.Add(blk);
                    w.Bool(recipe != null);
                    if (recipe != null)
                    {
                        w.VarU((ulong)recipe.Value.repl.Count);
                        foreach (var (ph, val) in recipe.Value.repl) { w.Str(ph); w.Str(val); }
                        w.Str(recipe.Value.loc);
                    }
                    Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 468", e); }
            }
        }

        /// <summary>
        /// Guest in another language: the same text key in the guest's language, the host's placeholder values
        /// (entity ids) put in, then the game's own token processor (entity names, grid references). Null = use
        /// the host's lines (same language, a text without key, or anything unexpected).
        /// </summary>
        static Il2CppSystem.Collections.Generic.List<string> RenderHere(string hostLang, string key, List<(string ph, string val)> repl, string locJson)
        {
            try
            {
                var lm = Localisation.LocalisationManager.Instance;
                CoopLog.Debug($"[events] render {key} here ({lm?.CurrentLanguage}, host {hostLang}, {repl.Count} values)");
                if (lm == null || string.IsNullOrEmpty(key) || lm.CurrentLanguage == hostLang) return null;
                if (!lm.TryGet(key, out string text) || string.IsNullOrEmpty(text)) { CoopLog.Debug($"[events] no {lm.CurrentLanguage} text for {key}"); return null; }
                foreach (var (ph, val) in repl) if (!string.IsNullOrEmpty(ph)) text = text.Replace(ph, val);
                var loc = string.IsNullOrEmpty(locJson) ? null : GameJson.Read<Il2CppSystem.Collections.Generic.Dictionary<string, GridReference>>(locJson);
                var lines = FireMissionTokenProcessor.ProcessBlock(text, loc);
                return lines != null && lines.Count > 0 ? lines : null;
            }
            catch (System.Exception e) { CoopLog.Debug("[events] teleprinter re-render failed, using the host's text: " + e.Message); return null; }
        }

        public static int Rerendered;
        static string Clip(string t) => t == null ? "" : (t.Length > 80 ? t.Substring(0, 80) : t).Replace('\n', '/');

        /// <summary>Host: the text the node handed to the token processor, and its location context.</summary>
        [HarmonyPatch(typeof(FireMissionTokenProcessor), nameof(FireMissionTokenProcessor.ProcessBlock))]
        static class ProcessBlockPatch
        {
            static void Postfix(Il2CppSystem.Collections.Generic.List<string> __result)
            {
                try
                {
                    if (!Coop.IsHost || TeleText.Current == null) return;
                    var copy = new List<string>();
                    if (__result != null) for (int i = 0; i < __result.Count; i++) if (!string.IsNullOrWhiteSpace(__result[i])) copy.Add(__result[i]);   // the node's own filter
                    TeleText.LastLines = copy;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 529", e); }
            }

            static void Prefix(string text, Il2CppSystem.Collections.Generic.Dictionary<string, GridReference> locationContextKeys)
            {
                if (!Coop.IsHost || TeleText.Current == null) return;
                TeleText.Replaced = text;
                try { TeleText.LocJson = locationContextKeys == null ? null : GameJson.Write(locationContextKeys); }
                catch (System.Exception) { TeleText.LocJson = null; }
            }
        }

        [HarmonyPatch(typeof(Teleprinter), nameof(Teleprinter.ClearAll))]
        static class TeleClearPatch
        {
            static void Postfix(Teleprinter __instance)
            {
                try
                {
                    if (!HostCapture) return;
                    History.Remove((int)__instance.TeleprinterType);
                    var w = Begin(Ev.TeleClear); w.VarI((int)__instance.TeleprinterType); Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 549", e); }
            }
        }

        // Teleprinter.SignalAlarm (66-byte switch) and ClearAlarm (20 bytes) crash the game when detoured
        // (the detour jump overwrites code the method branches back into). Capture at the nodes instead.
        [HarmonyPatch(typeof(State_TeleprinterText), nameof(State_TeleprinterText.OnEnter))]
        static class TeleAlarmPatch
        {
            static void Postfix(State_TeleprinterText __instance)
            {
                try
                {
                    if (!HostCapture || __instance.AlarmState == Teleprinter.TeleprinterAlarmState.None) return;
                    var w = Begin(Ev.TeleAlarm); w.VarI((int)__instance.Printer); w.VarI((int)__instance.AlarmState); Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 562", e); }
            }
        }

        [HarmonyPatch(typeof(State_ClearSignalAlarm), nameof(State_ClearSignalAlarm.OnEnter))]
        static class TeleClearAlarmPatch
        {
            static void Postfix(State_ClearSignalAlarm __instance)
            {
                try
                {
                    // Printers is a flag set {Primary=1, Secondary=2, both=3}; Teleprinter.Teleprinters is {Primary=0, Secondary=1}.
                    if (!HostCapture) return;
                    int flags = (int)__instance.Printer;
                    if ((flags & 1) != 0) { var w = Begin(Ev.TeleClearAlarm); w.VarI((int)Teleprinter.Teleprinters.Primary); Send(w); }
                    if ((flags & 2) != 0) { var w = Begin(Ev.TeleClearAlarm); w.VarI((int)Teleprinter.Teleprinters.Secondary); Send(w); }
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 572", e); }
            }
        }

        /// <summary>Remember which text key the teleprinter node is printing (for guests in another language).</summary>
        static class TeleText
        {
            public static string LastKey, Replaced, LocJson;
            public static List<string> LastLines;
            public static State_TeleprinterText Current;

            [HarmonyPatch(typeof(State_TeleprinterText), nameof(State_TeleprinterText.OnEnter))]
            static class Enter
            {
                static void Prefix(State_TeleprinterText __instance) { try { LastKey = __instance.Text?.Key; Current = __instance; Replaced = null; LocJson = null; LastLines = null; } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Prefix line 612", e); } }
                static void Postfix()
                {
                    try { Current = null; }
                    catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 593", e); }
                }
            }

            /// <summary>
            /// Placeholder values the node used, recovered by matching the host-language template against the text
            /// it handed to the token processor (values are entity ids, the same in every language).
            /// </summary>
            public static (List<(string ph, string val)> repl, string loc)? Recipe()
            {
                var node = Current;
                if (node == null || Replaced == null || string.IsNullOrEmpty(LastKey) || node.Text == null) { CoopLog.Debug($"[events] no recipe: node {node != null}, replaced {Replaced != null}, key '{LastKey}'"); return null; }
                if (!node.Text.TryGet(out string raw) || raw == null) { CoopLog.Debug($"[events] no recipe: key {LastKey} has no text"); return null; }
                var phs = new List<string>();
                if (node.EntityIDToReplace != null)
                    foreach (var sr in node.EntityIDToReplace) if (sr != null && !string.IsNullOrEmpty(sr.Text) && !phs.Contains(sr.Text) && raw.Contains(sr.Text)) phs.Add(sr.Text);
                var result = new List<(string, string)>();
                if (phs.Count == 0)
                {
                    if (raw != Replaced) CoopLog.Debug($"[events] no recipe for {LastKey}: text changed without placeholders ({raw.Length} -> {Replaced.Length}): '{Clip(raw)}' / '{Clip(Replaced)}'");
                    return raw == Replaced ? (result, LocJson) : null;
                }
                string pattern = System.Text.RegularExpressions.Regex.Escape(raw);
                var seen = new HashSet<int>();
                for (int i = 0; i < phs.Count; i++)
                {
                    string esc = System.Text.RegularExpressions.Regex.Escape(phs[i]);
                    int at = pattern.IndexOf(esc, System.StringComparison.Ordinal);
                    if (at < 0) return null;
                    pattern = pattern.Substring(0, at) + $"(?<g{i}>.*?)" + pattern.Substring(at + esc.Length).Replace(esc, $"\\k<g{i}>");
                }
                var m = System.Text.RegularExpressions.Regex.Match(Replaced, "^" + pattern + "$", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (!m.Success) { CoopLog.Debug($"[events] no recipe for {LastKey}: template did not match ({phs.Count} placeholders)"); return null; }
                for (int i = 0; i < phs.Count; i++) result.Add((phs[i], m.Groups["g" + i].Value));
                return (result, LocJson);
            }
        }

        /// <summary>
        /// Capture at the mission node, not at UINotificationManager.ShowNotification: patching that
        /// method (it takes a Nullable&lt;Color&gt; by value) crashed the game natively. The node gives
        /// text keys, so each guest shows the notification in their own language.
        /// </summary>
        [HarmonyPatch(typeof(State_SendUINotification), nameof(State_SendUINotification.OnEnter))]
        static class UiNotePatch
        {
            static void Postfix(State_SendUINotification __instance)
            {
                try
                {
                    if (!HostCapture) return;
                    var w = Begin(Ev.UiNote); w.Str(__instance.NodeID);
                    Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 638", e); }
            }
        }

        [HarmonyPatch(typeof(MissionGraphNotificationListener), nameof(MissionGraphNotificationListener.Trigger))]
        static class SceneNotePatch
        {
            static void Postfix(MissionGraphNotificationListener __instance, string messageID)
            {
                try
                {
                    if (!HostCapture) return;
                    var w = Begin(Ev.SceneNote); w.Str(ScenePath.Of(__instance.transform)); w.Str(messageID);
                    Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 649", e); }
            }
        }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.MarkMissionComplete))]
        static class CompletePatch { static void Postfix() { try { if (HostCapture) Send(Begin(Ev.MissionComplete)); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 690", e); } } }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.MarkMissionFailed))]
        static class FailedPatch { static void Postfix() { try { if (HostCapture) Send(Begin(Ev.MissionFailed)); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 693", e); } } }

        static void TimerChanged() { if (HostCapture) Instance?.SendTimer(); }

        /// <summary>
        /// The host's counter-battery timer length (host rule). The game's times are scaled when the mission sets them
        /// (Off keeps the game's time but holds the timer paused). Changing the rule mid-mission rescales the time left
        /// from the game's own amount (left / old scale * new scale; Off pauses, back on resumes); it never changes how
        /// fast a second runs. Dev shortcuts set exact times (Unscaled).
        /// </summary>
        public static bool TimerUnscaled;
        static bool ScaleTimer => Coop.IsHost && Coop.InSession && !TimerUnscaled;
        static float RuleScale => HostRules.TimerPercent == 0 ? 1f : HostRules.TimerPercent / 100f;
        static float _baked = 1f;        // the scale in the running timer's numbers
        static bool _pausedByRule;

        static void Unscaled(Action a) { TimerUnscaled = true; try { a(); } finally { TimerUnscaled = false; } }

        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.Init))]
        static class TimerLength
        {
            static void Prefix(ref float InitalTime)
            {
                try
                {
                    if (!ScaleTimer) return;
                    InitalTime *= RuleScale; _baked = RuleScale; _pausedByRule = false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Prefix line 682", e); }
            }
        }

        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.StartTimer))]
        static class TimerStartOff
        {
            static void Postfix(CounterBatteryTimer __instance)
            {
                try
                {
                    if (!ScaleTimer || HostRules.TimerPercent != 0) return;
                    Unscaled(() => __instance.PauseTimer());
                    _pausedByRule = true;
                    CoopLog.Info("[world] counter-battery timer held paused (host rule: off)");
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 692", e); }
            }
        }

        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.UnpauseTimer))]
        static class TimerStayOff { static bool Prefix() { try { return !(ScaleTimer && HostRules.TimerPercent == 0); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Prefix line 742", e); return true; } } }

        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.AddTime))]
        static class TimerAdd { static void Prefix(ref float seconds) { try { if (ScaleTimer) seconds *= _baked; } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Prefix line 745", e); } } }

        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.SetTime))]
        static class TimerSet { static void Prefix(ref float seconds) { try { if (ScaleTimer) seconds *= _baked; } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Prefix line 748", e); } } }

        /// <summary>Host: the timer rule changed while a timer may be running.</summary>
        static void OnTimerRule(uint was, uint now)
        {
            if (!Coop.IsHost || HostRules.TimerPercentOf(was) == HostRules.TimerPercentOf(now)) return;
            var t = CounterBatteryTimer.Instance;
            if (t == null || t._expired || t._permanentlyStopped) return;
            int pct = HostRules.TimerPercentOf(now);
            if (!t._running && !_pausedByRule) { if (pct > 0) _baked = pct / 100f; return; }   // not started (or paused by the mission)
            if (pct == 0)
            {
                Unscaled(() => t.PauseTimer()); _pausedByRule = true;
                CoopLog.Info($"[world] counter-battery timer paused (host rule: off), {t.TimeRemaining:F0} s left");
                return;
            }
            float scale = pct / 100f, left = t.TimeRemaining;
            float vanilla = left / Mathf.Max(0.01f, _baked);
            Unscaled(() => t.SetTime(vanilla * scale));
            _baked = scale;
            if (_pausedByRule) { Unscaled(() => t.UnpauseTimer()); _pausedByRule = false; }
            CoopLog.Info($"[world] counter-battery timer rescaled for {pct}%: {left:F0} s -> {t.TimeRemaining:F0} s");
        }

        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.Init))] static class T1 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 772", e); } } }
        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.StartTimer))] static class T2 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 773", e); } } }
        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.PermanentlyStop))] static class T3 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 774", e); } } }
        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.AddTime))] static class T4 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 775", e); } } }
        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.SetTime))] static class T5 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 776", e); } } }
        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.PauseTimer))] static class T6 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 777", e); } } }
        [HarmonyPatch(typeof(CounterBatteryTimer), nameof(CounterBatteryTimer.UnpauseTimer))] static class T7 { static void Postfix() { try { TimerChanged(); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 778", e); } } }

        [HarmonyPatch(typeof(TurretController), nameof(TurretController.MoveTurret))]
        static class TurretMovePatch
        {
            static void Postfix(Vector3 worldPos, bool dodgeCounterBattery)
            { try { if (HostCapture) { var w = Begin(Ev.TurretMove); w.Vec3(worldPos.x, worldPos.y, worldPos.z); w.Bool(dodgeCounterBattery); Send(w); } } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 783", e); } }
        }

        [HarmonyPatch(typeof(TurretController), nameof(TurretController.SetTurretLocation))]
        static class TurretPlacePatch
        {
            static void Postfix(Vector3 worldPos)
            { try { if (HostCapture) { var w = Begin(Ev.TurretPlace); w.Vec3(worldPos.x, worldPos.y, worldPos.z); Send(w); } } catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 790", e); } }
        }

        [HarmonyPatch(typeof(ImpactMarkerManager), nameof(ImpactMarkerManager.RegisterScoutPlane))]
        static class ScoutPatch
        {
            static void Postfix(GameObject scoutPlane, string sourceNodeId)
            {
                try
                {
                    if (!HostCapture || scoutPlane == null) return;
                    var t = scoutPlane.transform;
                    var rec = (MissionFlow.Instance.Epoch, sourceNodeId, ScenePath.Of(t.parent), t.position, t.rotation);
                    Scouts.Add(rec);   // replayed to whoever joins later (the photos and the units they reveal)
                    Send(ScoutMessage(rec));
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 757", e); }
            }
        }

        [HarmonyPatch(typeof(PrefabSpawner), nameof(PrefabSpawner.Spawn))]
        static class PrefabSpawnPatch
        {
            static void Prefix(PrefabSpawner __instance, out HashSet<IntPtr> __state)
            {
                __state = null;
                try
                {
                    if (!Coop.InSession || __instance == null) return;
                    var before = new HashSet<IntPtr>();
                    var t = __instance.transform;
                    for (int i = 0; i < t.childCount; i++) before.Add(t.GetChild(i).Pointer);
                    __state = before;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.PrefabSpawnPatch.Prefix", e); __state = null; }
            }

            static void Postfix(PrefabSpawner __instance, HashSet<IntPtr> __state)
            {
                try
                {
                    if (__state != null)
                    {
                        // name what this delivery brought by spawner + delivery number (same on every machine)
                        string key = ScenePath.Of(__instance.transform);
                        if (_deliveryEpoch != MissionFlow.Instance.Epoch) { _deliveryEpoch = MissionFlow.Instance.Epoch; _deliveries.Clear(); }
                        _deliveries.TryGetValue(key, out int n); _deliveries[key] = n + 1;
                        var t = __instance.transform;
                        for (int i = 0; i < t.childCount; i++)
                        {
                            var c = t.GetChild(i);
                            if (!__state.Contains(c.Pointer)) ItemId.RegisterSpawned(c.gameObject, key + "#" + n);
                        }
                    }
                    if (!HostCapture) return;
                    string path = ScenePath.Of(__instance.transform);
                    Spawns.Add((MissionFlow.Instance.Epoch, path));   // for guests still loading (the snapshot)
                    var w = Begin(Ev.PrefabSpawn);
                    w.Str(path);
                    Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 781", e); }
            }
        }

        static readonly Dictionary<string, int> _deliveries = new();
        static int _deliveryEpoch = -1;

        /// <summary>Spawner calls of this mission (deliveries often come at mission start, before guests are ready).</summary>
        static readonly List<(int Epoch, string Path)> Spawns = new();
        static readonly HashSet<string> _scoutsHere = new();   // guest: flights shown this mission

        // ------------------------------------------------------------------ the map for a rejoining player

        // Shots fired while a player was away (their impacts and crater photos, the correction arrows), shells still in
        // flight: the game's own checkpoint capture of the map (ImpactMarkerManager), restored the way a checkpoint
        // is, impact sounds suppressed. Scout planes are left out: their flights come as ScoutPlane above (with the
        // photo strips). Applied once per mission: a fresh join loads the mission with an empty map.
        static int _mapEpoch = -1;
        public static int MapObjectsRestored;   // read by the tests
        public static string MapObjectKinds = "";

        static NetWriter MapObjectsMessage()
        {
            var imm = ImpactMarkerManager.Instance;
            if (imm == null) return null;
            var all = imm.CaptureMissionObjects();
            if (all == null) return null;
            var keep = new Il2CppSystem.Collections.Generic.List<DynamicMapObjectSaveData>();
            var kinds = new List<string>();
            foreach (var o in all)
            {
                if (o == null) continue;
                kinds.Add(o.Kind);
                if ((o.Kind ?? "").IndexOf("scout", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                keep.Add(o);
            }
            CoopLog.Debug($"[events] map for a joining player: {keep.Count} of {kinds.Count} objects ({string.Join(", ", kinds)})");
            if (keep.Count == 0) return null;
            var w = Begin(Ev.MapObjects); w.Str(GameJson.Write(keep));
            // which parts of each impact show (photo strips, the crater photo): the restore shows them all unless
            // blocked, but an impact off the map's edge (or one hidden later) shows none on the host. Every part of the
            // impact by its path inside it: the game removes parts from a live impact as it plays out, which a restore
            // doesn't, so a part the host no longer has is hidden here too.
            var impacts = new List<ImpactLocation>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ImpactLocation>(), false))
            { var i = o.TryCast<ImpactLocation>(); if (i != null && impacts.Count < 256) impacts.Add(i); }
            w.VarU((ulong)impacts.Count);
            foreach (var i in impacts)
            {
                var p = i.transform.position; w.F32(p.x); w.F32(p.y); w.F32(p.z);
                var parts = ImpactParts(i);
                w.VarU((ulong)parts.Count);
                foreach (var (path, go) in parts) { w.Str(path); w.Bool(go.activeSelf); }
            }
            // then each impact's animators (they show and hide its parts as the impact plays out: a restored impact
            // started its animation over, and kept switching parts the host no longer shows)
            foreach (var i in impacts)
            {
                var anims = ImpactAnimators(i);
                w.VarU((ulong)anims.Count);
                foreach (var (path, a) in anims)
                {
                    var st = a.isActiveAndEnabled && a.runtimeAnimatorController != null ? a.GetCurrentAnimatorStateInfo(0) : default;
                    w.Str(path); w.I32(st.fullPathHash); w.F32(st.normalizedTime);
                }
            }
            // and what is switched on in each part (a picture or text can be off, or faded out, on an active part)
            foreach (var i in impacts)
            {
                var parts = ImpactParts(i);
                w.VarU((ulong)parts.Count);
                foreach (var (path, go) in parts)
                {
                    var (lookOn, lookAll, alpha) = PartLook(go);
                    w.Str(path); w.U8((byte)Mathf.Min(lookOn, 255)); w.U8((byte)Mathf.Min(lookAll, 255)); w.F32(alpha);
                }
            }
            // a shell that left the map leaves a different marker at the edge (an arrow, the distance, the side): the
            // restore turns it into an ordinary impact. Which impacts are such markers, with each part's turn and text.
            foreach (var i in impacts)
            {
                bool edge = i.GetComponentInChildren<OutOfBoundsEffectReceiver>(true) != null;
                w.Bool(edge);
                if (!edge) continue;
                var parts = ImpactParts(i);
                w.VarU((ulong)parts.Count);
                foreach (var (path, go) in parts)
                {
                    var q = go.transform.localRotation;
                    var tmp = go.GetComponent<TMPro.TMP_Text>();
                    w.Str(path); w.F32(q.x); w.F32(q.y); w.F32(q.z); w.F32(q.w); w.Str(tmp != null ? tmp.text ?? "" : "");
                }
            }
            return w;
        }

        void ApplyMapObjects(NetReader r)
        {
            string json = r.Str(4 << 20);
            if (_mapEpoch == MissionFlow.Instance.Epoch) return;
            _mapEpoch = MissionFlow.Instance.Epoch;
            var list = GameJson.Read<Il2CppSystem.Collections.Generic.List<DynamicMapObjectSaveData>>(json);
            var imm = ImpactMarkerManager.Instance;
            if (list == null || imm == null) { CoopLog.Warn("[events] the host's map could not be read here"); return; }
            var kinds = new List<string>();
            foreach (var o in list) if (o != null) kinds.Add(o.Kind);
            MapObjectKinds = string.Join(",", kinds);
            Coop.Apply("host map", () => imm.RestoreMissionObjects(list, MissionManager.Instance._CurrentMission_k__BackingField));
            // the correction arrows as on the host: the restore sets them, then each impact decides again a moment later
            // from this machine's view of the mission (guests run no mission logic: the arrow went away)
            _corrections.Clear();
            foreach (var o in list)
                if (o != null && o.HasImpactVisualCorrections && o.ImpactVisualCorrections != null && o.HasWorldTransform && _corrections.Count < 256)
                    _corrections.Add(((Vector3)o.WorldPosition, o.ImpactVisualCorrections));
            int shown = r.Count(256, "impacts");
            _impactOrder.Clear();
            for (int k = 0; k < shown; k++)
            {
                _impactOrder.Add(null);
                var at = new Vector3(r.F32(), r.F32(), r.F32());
                int count = r.Count(256, "impact parts");
                var hostParts = new Dictionary<string, bool>();
                for (int j = 0; j < count; j++) hostParts[r.Str(512)] = r.Bool();
                ImpactLocation best = null; float bestD = 0.05f;
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ImpactLocation>(), false))
                { var i = o.TryCast<ImpactLocation>(); if (i == null) continue; float dd = (i.transform.position - at).magnitude; if (dd < bestD) { bestD = dd; best = i; } }
                if (best == null) continue;
                _impactOrder[k] = best;
                int hidden = MatchImpactParts(best, hostParts);
                _impactParts.Add((best, hostParts));
                CoopLog.Debug($"[events] impact at {at:F2}: {count} parts on the host, {hidden} hidden here to match");
            }
            // their animations where the host's are
            for (int k = 0; k < shown && r.Remaining > 0; k++)
            {
                int n = r.Count(64, "impact animators");
                var states = new Dictionary<string, (int Hash, float Time)>();
                for (int j = 0; j < n; j++) states[r.Str(512)] = (r.I32(), r.F32());
                if (k >= _impactOrder.Count || _impactOrder[k] == null || LaunchArgs.Mutant("noimpactanims")) continue;
                foreach (var (path, a) in ImpactAnimators(_impactOrder[k]))
                    if (states.TryGetValue(path, out var st) && st.Hash != 0 && float.IsFinite(st.Time) && a.runtimeAnimatorController != null)
                        Coop.ApplyVisual("impact animation", () => { a.Play(st.Hash, 0, st.Time); a.Update(0f); });
            }
            _impactLooks.Clear();
            for (int k = 0; k < shown && r.Remaining > 0; k++)
            {
                int n = r.Count(256, "impact part looks");
                var looks = new Dictionary<string, (int On, int All, float Alpha)>();
                for (int j = 0; j < n; j++) looks[r.Str(512)] = (r.U8(), r.U8(), r.F32());
                if (k < _impactOrder.Count && _impactOrder[k] != null) _impactLooks.Add((_impactOrder[k], looks));
            }
            for (int k = 0; k < shown && r.Remaining > 0; k++)
            {
                if (!r.Bool()) continue;
                int n = r.Count(256, "edge marker parts");
                var turns = new Dictionary<string, (Quaternion Rot, string Text)>();
                for (int j = 0; j < n; j++)
                {
                    string path = r.Str(512);
                    var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    turns[path] = (q, r.Str(256));
                }
                if (k < _impactOrder.Count && _impactOrder[k] != null && !LaunchArgs.Mutant("noedgemarkers")) MakeEdgeMarker(k, turns);
            }
            foreach (var (imp, parts) in _impactParts) if (imp != null) MatchImpactParts(imp, parts);
            foreach (var (imp, looks) in _impactLooks) if (imp != null) MatchImpactLooks(imp, looks);
            // the restored impacts settle over the next frames (the photo strips and the correction arrow are decided
            // again a moment later, from this machine's view of the mission): the host's state again, a few times
            _reapplyParts = 0; _nextPartsReapply = Time.unscaledTime + 0.3f;
            MapObjectsRestored += list.Count;
            CoopLog.Info($"[events] the host's map: {list.Count} objects restored ({MapObjectKinds})");
        }

        static readonly List<(ImpactLocation Impact, Dictionary<string, bool> Parts)> _impactParts = new();
        static readonly List<ImpactLocation> _impactOrder = new();
        static readonly List<(ImpactLocation Impact, Dictionary<string, (int On, int All, float Alpha)> Looks)> _impactLooks = new();
        public static int ImpactLooksSet;   // read by the tests

        /// <summary>A part's own pictures, meshes and texts: how many are switched on, of how many, and its canvas
        /// group's alpha (-1: none).</summary>
        static (int On, int All, float Alpha) PartLook(GameObject go)
        {
            int on = 0, all = 0;
            foreach (var c in go.GetComponents<Renderer>()) { if (c == null) continue; all++; if (c.enabled) on++; }
            foreach (var c in go.GetComponents<UnityEngine.UI.Graphic>()) { if (c == null) continue; all++; if (c.enabled) on++; }
            var cg = go.GetComponent<CanvasGroup>();
            return (on, all, cg != null ? cg.alpha : -1f);
        }

        static ImpactLocation _edgePrefab;
        public static int EdgeMarkersMade;   // read by the tests

        /// <summary>The marker a shell leaves where it flew off the map (the game's own prefab, found once).</summary>
        static ImpactLocation EdgePrefab()
        {
            if (_edgePrefab != null) return _edgePrefab;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<OutOfBoundsEffectReceiver>()))
            {
                var rcv = o.TryCast<OutOfBoundsEffectReceiver>();
                if (rcv == null || rcv.gameObject.scene.IsValid()) continue;   // the prefab, not a copy in the scene
                var il = rcv.GetComponentInParent<ImpactLocation>(true);
                if (il != null) { _edgePrefab = il; break; }
            }
            return _edgePrefab;
        }

        /// <summary>Impact <paramref name="k"/> of the host's map is an edge marker: the ordinary impact the restore
        /// made of it is replaced by the marker, turned and labelled as on the host.</summary>
        void MakeEdgeMarker(int k, Dictionary<string, (Quaternion Rot, string Text)> turns)
        {
            var old = _impactOrder[k];
            if (old.GetComponentInChildren<OutOfBoundsEffectReceiver>(true) != null) { ApplyTurns(old, turns); return; }
            var prefab = EdgePrefab();
            if (prefab == null) { CoopLog.Warn("[events] no edge marker prefab here: a shell that left the host's map shows as an impact"); return; }
            ImpactLocation made = null;
            Coop.ApplyVisual("edge marker", () =>
            {
                bool was = prefab.gameObject.activeSelf;
                prefab.gameObject.SetActive(false);   // made switched off: its sounds silenced before it wakes
                GameObject go;
                try { go = UnityEngine.Object.Instantiate(prefab.gameObject, old.transform.parent, false); }
                finally { prefab.gameObject.SetActive(was); }
                go.transform.localPosition = old.transform.localPosition;
                go.transform.localRotation = old.transform.localRotation;
                go.transform.localScale = old.transform.localScale;
                ImpactLocation.SuppressRestoreAudio(go);
                go.SetActive(true);
                made = go.GetComponent<ImpactLocation>();
                UnityEngine.Object.Destroy(old.gameObject);
            });
            if (made == null) return;
            _impactOrder[k] = made;
            for (int j = 0; j < _impactParts.Count; j++) if (_impactParts[j].Impact == old) _impactParts[j] = (made, _impactParts[j].Parts);
            for (int j = 0; j < _impactLooks.Count; j++) if (_impactLooks[j].Impact == old) _impactLooks[j] = (made, _impactLooks[j].Looks);
            ApplyTurns(made, turns);
            EdgeMarkersMade++;
            CoopLog.Info($"[events] the host's edge marker at {made.transform.position:F2} made here (the restore had made an impact of it)");
        }

        static void ApplyTurns(ImpactLocation i, Dictionary<string, (Quaternion Rot, string Text)> turns)
        {
            foreach (var (path, go) in ImpactParts(i))
            {
                if (!turns.TryGetValue(path, out var t)) continue;
                if (float.IsFinite(t.Rot.x + t.Rot.y + t.Rot.z + t.Rot.w) && Mathf.Abs(t.Rot.x * t.Rot.x + t.Rot.y * t.Rot.y + t.Rot.z * t.Rot.z + t.Rot.w * t.Rot.w - 1f) < 0.01f)
                    go.transform.localRotation = t.Rot;
                var tmp = go.GetComponent<TMPro.TMP_Text>();
                if (tmp != null && !string.IsNullOrEmpty(t.Text) && tmp.text != t.Text) tmp.text = t.Text;
            }
        }

        internal static string DescribeImpact(ImpactLocation i)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"impact {i.transform.position.x:F2};{i.transform.position.z:F2} active {i.gameObject.activeInHierarchy} scale {i.transform.lossyScale.x:F2}: ");
            foreach (var (path, go) in ImpactParts(i))
            {
                var (on, all, alpha) = PartLook(go);
                sb.Append($"[{path} self {go.activeSelf} hier {go.activeInHierarchy} vis {on}/{all} a {alpha:F2} layer {go.layer}] ");
            }
            return sb.ToString();
        }

        static void MatchImpactLooks(ImpactLocation impact, Dictionary<string, (int On, int All, float Alpha)> looks)
        {
            if (LaunchArgs.Mutant("noimpactlooks")) return;
            foreach (var (path, go) in ImpactParts(impact))
            {
                if (!looks.TryGetValue(path, out var host)) continue;
                var mine = PartLook(go);
                if (host.All > 0 && mine.All == host.All && mine.On != host.On && (host.On == 0 || host.On == host.All))
                {
                    bool want = host.On > 0;
                    Coop.ApplyVisual("impact look", () =>
                    {
                        foreach (var c in go.GetComponents<Renderer>()) if (c != null) c.enabled = want;
                        foreach (var c in go.GetComponents<UnityEngine.UI.Graphic>()) if (c != null) c.enabled = want;
                    });
                    ImpactLooksSet++;
                    CoopLog.Debug($"[events] impact part {path}: {(want ? "shown" : "hidden")} as on the host");
                }
                if (host.Alpha >= 0 && mine.Alpha >= 0 && Mathf.Abs(host.Alpha - mine.Alpha) > 0.01f)
                {
                    var cg = go.GetComponent<CanvasGroup>();
                    if (cg != null) { Coop.ApplyVisual("impact look", () => cg.alpha = host.Alpha); ImpactLooksSet++; CoopLog.Debug($"[events] impact part {path}: alpha {mine.Alpha:F2} -> {host.Alpha:F2} as on the host"); }
                }
            }
        }

        /// <summary>The animators in an impact, by their path inside it ("" for one on the impact itself).</summary>
        static List<(string Path, Animator A)> ImpactAnimators(ImpactLocation i)
        {
            var list = new List<(string, Animator)>();
            foreach (var a in i.GetComponentsInChildren<Animator>(true))
            {
                if (a == null || list.Count >= 64) continue;
                var names = new List<string>();
                for (var p = a.transform; p != null && p.Pointer != i.transform.Pointer; p = p.parent) names.Insert(0, p.name);
                list.Add((string.Join("/", names), a));
            }
            return list;
        }
        static int _reapplyParts = 99; static float _nextPartsReapply;
        static readonly float[] PartsReapplyGaps = { 0.7f, 1f, 1.5f, 2f, 2.5f, 3f };   // about 10 s in all
        static readonly List<(Vector3 At, ImpactVisualCorrectionsSaveData Data)> _corrections = new();
        public static int CorrectionsReapplied;   // read by the tests
        public static int ImpactPartsReapplied;   // read by the tests

        /// <summary>Each part of a restored impact shown or hidden as on the host. Returns how many it hid.</summary>
        static int MatchImpactParts(ImpactLocation impact, Dictionary<string, bool> hostParts)
        {
            int hidden = 0;
            foreach (var (path, part) in ImpactParts(impact))
            {
                // the reveal sounds: the restore silences them on purpose (on would play them again); nothing to see
                if (part.name.StartsWith("FMOD", StringComparison.Ordinal)) continue;
                bool on = hostParts.TryGetValue(path, out var hostOn) && hostOn;   // gone on the host: hidden here
                if (part.activeSelf != on) { if (_reapplyParts < 99) CoopLog.Debug($"[events] impact {impact.transform.position.x:F2};{impact.transform.position.z:F2} part {path}: {part.activeSelf} here, {on} on the host (anims {impact.GetComponentsInChildren<Animator>(true).Length})"); Coop.ApplyVisual("impact part", () => part.SetActive(on)); if (!on) hidden++; ImpactPartsReapplied += _reapplyParts < 99 ? 1 : 0; }
            }
            return hidden;
        }

        static void ReapplyImpactParts()
        {
            if (_reapplyParts >= PartsReapplyGaps.Length || Time.unscaledTime < _nextPartsReapply) return;
            if (LaunchArgs.Mutant("noimpactreapply")) { _reapplyParts = 99; return; }
            int changed = ImpactPartsReapplied;
            foreach (var (impact, parts) in _impactParts) if (impact != null) MatchImpactParts(impact, parts);
            foreach (var (impact, looks) in _impactLooks) if (impact != null) MatchImpactLooks(impact, looks);
            if (_corrections.Count > 0 && !LaunchArgs.Mutant("nocorrections"))
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ImpactVisualCorrections>(), false))
                {
                    var c = o.TryCast<ImpactVisualCorrections>();
                    var at = c?.impactLocation != null ? c.impactLocation.transform.position : c != null ? c.transform.position : default;
                    if (c == null) continue;
                    foreach (var (pos, data) in _corrections)
                    {
                        if ((pos - at).sqrMagnitude > 0.05f * 0.05f) continue;
                        bool arrow = c.arrowRoot != null && c.arrowRoot.activeSelf;
                        if (arrow != data.ArrowActive)
                        {
                            Coop.ApplyVisual("correction arrow", () => c.RestoreMissionState(data));
                            CorrectionsReapplied++;
                            CoopLog.Debug($"[events] correction arrow at {at:F2} set back to the host's ({data.ArrowActive})");
                        }
                        break;
                    }
                }
            if (ImpactPartsReapplied != changed) CoopLog.Debug($"[events] restored impacts: {ImpactPartsReapplied - changed} parts set back to the host's state");
            _nextPartsReapply = Time.unscaledTime + PartsReapplyGaps[_reapplyParts];
            if (++_reapplyParts >= PartsReapplyGaps.Length) { _impactParts.Clear(); _corrections.Clear(); _impactLooks.Clear(); }
        }

        /// <summary>Every part under an impact, named by its path inside it (a repeated name gets "#n").</summary>
        static List<(string Path, GameObject Go)> ImpactParts(ImpactLocation i)
        {
            var list = new List<(string, GameObject)>();
            var seen = new Dictionary<string, int>();
            foreach (var t in i.GetComponentsInChildren<Transform>(true))
            {
                if (t == null || t.Pointer == i.transform.Pointer || list.Count >= 256) continue;
                var names = new List<string>();
                for (var p = t; p != null && p.Pointer != i.transform.Pointer; p = p.parent) names.Insert(0, p.name);
                string path = string.Join("/", names);
                int n = seen.TryGetValue(path, out var c) ? c + 1 : 1; seen[path] = n;
                list.Add((n == 1 ? path : path + "#" + n, t.gameObject));
            }
            return list;
        }

        // The map's measuring arms stay where the last shell left them (until the next one resets them).
        static NetWriter MapArmsMessage()
        {
            var arms = new List<(string Path, Vector3 Pos)>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<TrajectorySystem.TrajectoryTarget>(), false))
            {
                var tt = o.TryCast<TrajectorySystem.TrajectoryTarget>();
                if (tt != null && arms.Count < 16) arms.Add((Util.ScenePath.Of(tt.transform), tt.transform.localPosition));
            }
            var w = Begin(Ev.MapArms); w.VarU((ulong)arms.Count);
            foreach (var (path, p) in arms) { w.Str(path); w.F32(p.x); w.F32(p.y); w.F32(p.z); }
            return w;
        }

        public static int ArmResetsSent, ArmResetsApplied;   // read by the tests

        /// <summary>
        /// Clicking one of the map's arms sends it back to its rest (TrajectoryTarget.RequestResetToDefault, from the
        /// arm's own button). That is game logic, which runs on the host only (a guest sees the host's press, not its
        /// effect): the arm stayed out on every guest (players hit this repeatedly). The host passes each reset on.
        /// </summary>
        [HarmonyPatch(typeof(TrajectorySystem.TrajectoryTarget), nameof(TrajectorySystem.TrajectoryTarget.RequestResetToDefault))]
        static class ArmReset
        {
            static void Postfix(TrajectorySystem.TrajectoryTarget __instance)
            {
                try
                {
                    if (!Coop.IsHost || Coop.Applying || LaunchArgs.Mutant("noarmreset")) return;
                    string path = Util.ScenePath.Of(__instance.transform);
                    if (path == null) return;
                    var w = Begin(Ev.ArmReset); w.Str(path); w.Bool(__instance.IsClaimed);
                    Send(w);
                    ArmResetsSent++;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.ArmReset", e); }
            }
        }

        void ApplyMapArms(NetReader r)
        {
            int n = r.Count(16, "map arms");
            for (int i = 0; i < n; i++)
            {
                string path = r.Str(2048);
                var p = new Vector3(r.F32(), r.F32(), r.F32());
                if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z) || p.sqrMagnitude > 1e6f) continue;
                var tt = Util.ScenePath.Find(path)?.GetComponent<TrajectorySystem.TrajectoryTarget>();
                if (tt == null || tt.IsClaimed) continue;   // following a shell here: that shell decides
                Coop.ApplyVisual("map arm", () => { tt.isResetting = false; tt.transform.localPosition = p; });
            }
        }
        static int _scoutEpoch = -1;

        // the scout flights of this mission: a player joining (or joining again) later got none of their photos, nor
        // the enemy markers on them
        static readonly List<(int Epoch, string Node, string Parent, Vector3 Pos, Quaternion Rot)> Scouts = new();

        static NetWriter ScoutMessage((int Epoch, string Node, string Parent, Vector3 Pos, Quaternion Rot) s)
        {
            var w = Begin(Ev.ScoutPlane);
            w.Str(s.Node); w.Str(s.Parent);
            w.Vec3(s.Pos.x, s.Pos.y, s.Pos.z);
            w.Quat(s.Rot.x, s.Rot.y, s.Rot.z, s.Rot.w);
            return w;
        }

        [HarmonyPatch(typeof(State_TriggerImpact), nameof(State_TriggerImpact.SpawnImpactPrefab))]
        static class ScriptedImpactPatch
        {
            static void Postfix(State_TriggerImpact __instance, Vector2 impactLocation, ImpactTrackingType trackingType, string shellInstanceId)
            {
                try
                {
                    if (!HostCapture) return;
                    var w = Begin(Ev.ScriptedImpact);
                    w.Str(__instance.NodeID); w.F32(impactLocation.x); w.F32(impactLocation.y); w.VarI((int)trackingType); w.Str(shellInstanceId);
                    Send(w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldEvents.Postfix line 814", e); }
            }
        }

        /// <summary>Guests never evaluate impacts (damage, tracking, events): results come from the host.</summary>
        [HarmonyPatch(typeof(ImpactTracker), nameof(ImpactTracker.EvaluateImpact))]
        static class EvaluateImpactGate { static bool Prefix() => !GraphGate.Suppress; }
    }

    /// <summary>Find a graph node by its serialized GUID in the running mission's graphs.</summary>
    public static class Nodes
    {
        public static StateNode Find(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId)) return null;
            var mm = MissionManager.Instance;
            var m = mm?.CurrentMission;
            var hit = Search(m, nodeId);
            if (hit != null) return hit;
            if (m?.PassiveGraphs != null)
                foreach (var g in m.PassiveGraphs) { hit = Search(g, nodeId); if (hit != null) return hit; }
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppInterop.Runtime.Il2CppType.Of<StateNode>()))
            {
                var n = o.TryCast<StateNode>();
                if (n != null && n.NodeID == nodeId) return n;
            }
            return null;
        }

        static StateNode Search(NodeGraph g, string id)
        {
            if (g?.nodes == null) return null;
            foreach (var n in g.nodes)
            {
                var s = n?.TryCast<StateNode>();
                if (s != null && s.NodeID == id) return s;
            }
            return null;
        }
    }
}
