using System.Collections.Generic;
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
    /// The record player. The host's player is the source of truth for which record is on it, whether it is
    /// playing, which track is selected and where in the track it is. Guests follow and keep their audio within
    /// 0.25 s of the host's position (each sample carries the host's clock, so latency is taken out). The play
    /// button is an ordinary forwarded button. When a guest puts a record on or takes it off, that happens on
    /// the host and comes back to the guest as state.
    /// The volume dial is a host rule (Controls: shared, or each player's own). Newspaper music is per player.
    /// </summary>
    public sealed class Music : SyncModule
    {
        public override string Name => "music";
        public static Music Instance { get; private set; }

        enum Ev : byte { State = 1, PlaceRecord = 2, RemoveRecord = 3, Records = 4 }

        RecordPlayerController _player;
        int _sceneSig;
        float _nextSend;
        string _lastRecord; bool _lastPlaying; int _lastTrack = -1;

        // guest: latest host state
        float _localChangeUntil;   // guest: own record change sent, wait for the host's word before reconciling
        bool _have; string _hRecord; bool _hPlaying; int _hTrack; float _hPos; double _hAt; Quaternion? _hRot;

        public int Corrections, StateApplied;
        public float LastDrift;

        public Music() { Instance = this; }
        static readonly bool NoPos = LaunchArgs.Mutant("nomusicpos");   // used by the tests to prove that position correction matters

        public override void Register(Session s) => s.On((ushort)Msg.Music, OnEvent);
        int _haveEpoch = -1;
        public override void OnSessionStart() { _recordsSent = null; _nextRecords = 0; _hiddenRecords.Clear(); _have = false; _haveEpoch = -1; _lastTrack = -1; _lastRecord = null; _lastPlaying = false; _localChangeUntil = 0; _nextFollow = _nextFix = 0; }
        public override void OnSessionEnd()
        {
            // records hidden because the host lacked them are this player's own again
            foreach (var go in _hiddenRecords.Values) if (go != null && !go.activeSelf) go.SetActive(true);
            OnSessionStart();
        }

        float _retryFind;

        RecordPlayerController Player()
        {
            int sig = UnityEngine.SceneManagement.SceneManager.sceneCount;
            // a scene search only when the scenes changed, or every 2 s while it is missing (not every frame)
            if (sig != _sceneSig || (_player == null && Time.unscaledTime >= _retryFind))
            { _sceneSig = sig; _player = Object.FindObjectOfType<RecordPlayerController>(); _retryFind = Time.unscaledTime + 2f; }
            return _player;
        }

        static string RecordId(RecordItem r) => ItemId.Of(r);

        static NetWriter Begin(Ev ev) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); return w; }

        /// <summary>Test hook: "record|playing|track|position".</summary>
        public string Line
        {
            get
            {
                var p = Player();
                if (p == null) return "no record player";
                var src = p._useAAsActive ? p.audioSourceA : p.audioSourceB;
                return $"{RecordId(p._currentRecord) ?? "-"}|{p._isPlaying}|{p._trackIndex}|{(src != null && src.isPlaying ? src.time : -1):F1}";
            }
        }

        static float Position(RecordPlayerController p)
        {
            var src = p._useAAsActive ? p.audioSourceA : p.audioSourceB;
            return src != null && src.clip != null && src.isPlaying ? src.time : 0f;
        }

        // ------------------------------------------------------------------ host

        public override void Tick(float dt)
        {
            if (WorldReady.LocalReady || Coop.IsHost) ItemId.EnsureScanned();   // origin ids as soon as the scene is in
            if (Coop.IsHost && Time.unscaledTime >= _nextRecords) { _nextRecords = Time.unscaledTime + 2f; SendRecords(null); }
            if (Coop.IsHost) HostTick();
            else if (Coop.IsGuest) GuestTick();
        }

        void HostTick()
        {
            var p = Player();
            if (p == null || Time.unscaledTime < _nextSend) return;
            string rec = RecordId(p._currentRecord);
            bool changed = rec != _lastRecord || p._isPlaying != _lastPlaying || p._trackIndex != _lastTrack;
            if (!changed && Time.unscaledTime < _nextSend + 0.8f) return;   // position refresh once a second
            _nextSend = Time.unscaledTime + 0.2f;
            _lastRecord = rec; _lastPlaying = p._isPlaying; _lastTrack = p._trackIndex;
            SendState(p, changed ? Delivery.Reliable : Delivery.Unreliable, null);
        }

        void SendState(RecordPlayerController p, Delivery d, byte? slot)
        {
            var w = Begin(Ev.State);
            w.Str(RecordId(p._currentRecord)); w.Bool(p._isPlaying); w.VarI(p._trackIndex); w.F32(Position(p)); w.F64(Coop.Session.HostTime);
            var held = ItemOf(p._currentRecord);
            WriteRot(w, held != null && p.slot != null ? LocalRotation(held, p.slot) : new Quaternion(0, 0, 0, 0));
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Music, w, d, one);
            else WorldReady.SendToReady(Msg.Music, w, d);
        }

        public override void SendSnapshot(byte slot)
        {
            var p = Player();
            if (p != null) SendState(p, Delivery.Reliable, slot);
            SendRecords(slot);
        }

        // ------------------------------------------------------------------ which records exist

        // A record a player unlocks is laid out at the printers by their OWN save. In a session the world is the
        // host's: a guest showed a record there that the host has on the record player's table (two copies here,
        // one of them nowhere on the host). The host says which records exist; a guest hides any other it shows.
        float _nextRecords; string _recordsSent;
        readonly Dictionary<System.IntPtr, GameObject> _hiddenRecords = new();
        public int RecordsHidden;   // read by the tests

        static IEnumerable<RecordItem> SceneRecords()
        {
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<RecordItem>(), true))
            {
                var rec = o.TryCast<RecordItem>();
                if (rec != null && !string.IsNullOrEmpty(rec.gameObject.scene.name)) yield return rec;
            }
        }

        void SendRecords(byte? slot)
        {
            var ids = new List<string>();
            foreach (var rec in SceneRecords())
                if (rec.gameObject.activeInHierarchy || Items.Instance?.HeldByRemote(ItemOf(rec)) == true) ids.Add(RecordId(rec));
            ids.Sort();
            string key = string.Join("\n", ids);
            if (slot == null && key == _recordsSent) return;
            if (slot == null) _recordsSent = key;
            var w = Begin(Ev.Records); w.VarU((ulong)ids.Count); foreach (var id in ids) w.Str(id);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Music, w, Delivery.Reliable, one);
            else WorldReady.SendToReady(Msg.Music, w);
        }

        void OnRecords(NetReader r)
        {
            int n = r.Count(512, "records");
            var host = new HashSet<string>();
            for (int i = 0; i < n; i++) host.Add(r.Str(2048));
            foreach (var rec in SceneRecords())
            {
                string id = RecordId(rec);
                var go = rec.gameObject;
                if (host.Contains(id))
                {
                    if (_hiddenRecords.Remove(go.Pointer) && !go.activeSelf) { Coop.ApplyVisual("record exists", () => go.SetActive(true)); CoopLog.Debug($"[music] {id} exists on the host again: shown"); }
                    continue;
                }
                if (!go.activeInHierarchy || id == null || !id.StartsWith("origin:")) continue;   // only scene records the host lacks
                var item = ItemOf(rec);
                if (item != null && item.IsDragging) continue;                               // in this player's hand right now
                if (Util.ScenePath.Of(go.transform).Contains(":Main Camera/")) continue;      // on this player's own clipboard
                _hiddenRecords[go.Pointer] = go; RecordsHidden++;
                Coop.ApplyVisual("record not in the host's world", () => go.SetActive(false));
                CoopLog.Info($"[music] {id} is not in the host's world: hidden here");
            }
        }

        void OnEvent(in MsgContext c, NetReader r)
        {
            if ((int)r.VarU() != MissionFlow.Instance.Epoch) return;
            var ev = (Ev)r.U8();
            if (Coop.IsHost)
            {
                // a guest put a record on or took it off
                var p = Player();
                if (p == null || p.slot == null) return;
                if (ev == Ev.PlaceRecord)
                {
                    string id = r.Str(2048);
                    var rot = ReadRot(r);
                    var item = ItemOf(FindRecord(id));
                    CoopLog.Debug($"[music] guest {c.FromSlot} puts {id} on the player ({(item == null ? "NOT FOUND" : "ok")})");
                    if (item != null) Coop.Apply("place record", () => PlaceInto(item, p.slot, rot));
                }
                else if (ev == Ev.RemoveRecord && p._currentRecord != null)
                {
                    var item = ItemOf(p._currentRecord);
                    CoopLog.Debug($"[music] guest {c.FromSlot} takes the record off");
                    if (item != null) Coop.Apply("remove record", () => p.slot.RemoveItem(item, true));
                }
                return;
            }
            if (c.FromHost && ev == Ev.Records && WorldReady.LocalReady) { OnRecords(r); return; }
            if (!c.FromHost || ev != Ev.State || !WorldReady.LocalReady) return;
            _hRecord = r.Str(2048); _hPlaying = r.Bool(); _hTrack = (int)r.VarI(); _hPos = r.F32(); _hAt = r.F64();
            _hRot = ReadRot(r);
            _have = true; _haveEpoch = MissionFlow.Instance.Epoch; StateApplied++;
            GuestTick();
        }

        /// <summary>
        /// Put an item into a slot the way a hand does: at the slot first. ItemSlot.Update clears itself every
        /// frame unless the item's collider overlaps the slot, so a placement from elsewhere would not stick.
        /// The game's PlaceItem parents the item to the anchor and keeps its rotation (a dropped item arrives lying
        /// flat from the drag), so the orientation is the sender's, relative to the anchor; without one, the item
        /// keeps its own (never the anchor's: that stood records on edge).
        /// </summary>
        internal static void PlaceInto(DraggableItem item, ItemSlot slot, Quaternion? localRot = null)
        {
            var a = Anchor(slot);
            item.transform.SetPositionAndRotation(a.position, localRot is Quaternion q ? a.rotation * q : item.transform.rotation);
            item.MoveToSlot(slot);
            if (localRot is Quaternion lr) item.transform.rotation = a.rotation * lr;
        }

        internal static Transform Anchor(ItemSlot slot) => slot.itemAnchor != null ? slot.itemAnchor : slot.transform;

        /// <summary>The item's rotation relative to the slot's anchor (what PlaceInto takes).</summary>
        internal static Quaternion LocalRotation(DraggableItem item, ItemSlot slot) => Quaternion.Inverse(Anchor(slot).rotation) * item.transform.rotation;

        internal static void WriteRot(NetWriter w, Quaternion q) => w.Quat(q.x, q.y, q.z, q.w);

        internal static Quaternion? ReadRot(NetReader r)
        {
            var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            float n = q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w;
            return float.IsNaN(n) || n < 0.5f || n > 1.5f ? null : q;   // no rotation / garbage: keep the item's own
        }

        static DraggableItem ItemOf(RecordItem r) =>
            r == null ? null : (r.GetComponent<DraggableItem>() ?? r.GetComponentInParent<DraggableItem>() ?? r.GetComponentInChildren<DraggableItem>());

        static RecordItem FindRecord(string id) => ItemId.Find<RecordItem>(id);

        // ------------------------------------------------------------------ guest

        float _nextFollow, _nextFix;

        void GuestTick()
        {
            var p = Player();
            // only the host's word from this mission, once this mission is loaded here
            if (!_have || p == null || _haveEpoch != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            // 1. the record on the player
            string mine = RecordId(p._currentRecord);
            if (mine == _hRecord) _localChangeUntil = 0;
            else if (Time.unscaledTime < _localChangeUntil) return;   // our own change is on its way to the host
            else if (Time.unscaledTime < _nextFollow) return;          // a failed try: again in a second, not every frame
            else if (p.slot != null)
            {
                _nextFollow = Time.unscaledTime + 1f;
                if (p._currentRecord != null)
                {
                    var cur = ItemOf(p._currentRecord);
                    if (cur != null) Coop.Apply("music: take record off", () => p.slot.RemoveItem(cur, false));
                }
                CoopLog.Debug($"[music] host has {_hRecord ?? "no record"}, here {mine ?? "none"} (slot {p.slot.CurrentItem?.name ?? "empty"}, window {_localChangeUntil - Time.unscaledTime:F1}): following");
                var want = ItemOf(FindRecord(_hRecord));
                if (want != null) Coop.Apply("music: put record on", () => PlaceInto(want, p.slot, _hRot));
                if (RecordId(p._currentRecord) != _hRecord) return;   // try again next tick
            }
            // 2. playing or not
            if (Time.unscaledTime < _nextFix) return;
            if (_hPlaying != p._isPlaying)
            {
                _nextFix = Time.unscaledTime + 0.5f;
                Coop.Apply("music: play/stop", () => { if (_hPlaying) p.StartPlayback(); else p.StopPlayback(); });
                return;   // playback starts after the game's start delay; position is fixed on a later tick
            }
            if (!_hPlaying || p._currentRecord == null) return;
            // 3. track
            var tracks = p._currentRecord.tracks;
            if (tracks == null || tracks.Length == 0) return;
            if (p._trackIndex != _hTrack && _hTrack >= 0 && _hTrack < tracks.Length)
            {
                _nextFix = Time.unscaledTime + 0.5f;
                Coop.Apply("music: track", () => { p._trackIndex = (_hTrack - 1 + tracks.Length) % tracks.Length; p.AdvanceTrack(false); });
                Corrections++;
                return;
            }
            // 4. position (host sample + time since, in the host's clock)
            var src = p._useAAsActive ? p.audioSourceA : p.audioSourceB;
            if (src == null || src.clip == null || !src.isPlaying) return;
            float expected = _hPos + (float)(Coop.Session.HostTime - _hAt);
            if (expected >= src.clip.length - 0.3f) return;   // the track change is the host's call
            LastDrift = src.time - expected;
            if (Mathf.Abs(LastDrift) > 0.25f && !NoPos) { src.time = Mathf.Max(0, expected); Corrections++; }
        }

        // ------------------------------------------------------------------ patches

        /// <summary>A guest putting a record on (or taking it off) the player: done on the host.</summary>
        [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.PlaceItem))]
        static class Place
        {
            static bool Prefix(ItemSlot __instance, DraggableItem item)
            {
                try
                {
                    if (!GraphGate.Suppress || Instance == null) return true;
                    var p = Instance.Player();
                    if (p == null || p.slot == null || p.slot.Pointer != __instance.Pointer) return true;
                    var rec = item == null ? null : (item.GetComponent<RecordItem>() ?? item.GetComponentInChildren<RecordItem>());
                    if (rec == null) return true;
                    var w = Begin(Ev.PlaceRecord); w.Str(RecordId(rec));
                    WriteRot(w, LocalRotation(item, p.slot));   // the drop's rotation; PlaceItem keeps it
                    Coop.SendToHost(Msg.Music, w);
                    Instance._localChangeUntil = Time.unscaledTime + 2f;
                    return true;   // let it sit in the slot locally; the host's state confirms or corrects it
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Music.Prefix line 242", e); return true; }
            }
        }

        [HarmonyPatch(typeof(ItemSlot), nameof(ItemSlot.RemoveItem))]
        static class Remove
        {
            static void Prefix(ItemSlot __instance)
            {
                try
                {
                    if (Instance == null) return;
                    var p = Instance.Player();
                    if (p == null || p.slot == null || p.slot.Pointer != __instance.Pointer) return;
                    CoopLog.Debug($"[music] record taken off the player (applying {Coop.Applying})");
                    if (!GraphGate.Suppress) return;
                    Coop.SendToHost(Msg.Music, Begin(Ev.RemoveRecord));
                    Instance._localChangeUntil = Time.unscaledTime + 2f;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Music.Prefix line 260", e); }
            }
        }

        /// <summary>Guests never decide to start/stop or change tracks themselves.</summary>
        [HarmonyPatch(typeof(RecordPlayerController), "AdvanceTrack")]
        static class Advance { static bool Prefix() => !GraphGate.Suppress; }

        // TogglePlayStop itself is 0x1D bytes (too short to detour); gate what it calls instead.
        [HarmonyPatch(typeof(RecordPlayerController), "StartPlayback")]
        static class StartGate { static bool Prefix() => !GraphGate.Suppress; }

        [HarmonyPatch(typeof(RecordPlayerController), "StopPlayback")]
        static class StopGate { static bool Prefix() => !GraphGate.Suppress; }
    }
}
