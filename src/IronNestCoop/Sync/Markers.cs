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
    /// Drawings on the tactical map (lines, compass arcs, pointers). Anyone can draw or erase; a finished drawing
    /// goes to everyone in the game's own save format (MapMarkerSaveData via the placer's Capture/RestoreMarker),
    /// with a network id ("slot:n") so an erase on one machine removes the same drawing everywhere.
    /// </summary>
    public sealed class Markers : SyncModule
    {
        public override string Name => "markers";
        public static Markers Instance { get; private set; }

        enum Ev : byte { Add = 1, Remove = 2 }

        readonly Dictionary<IntPtr, string> _idOf = new();
        readonly Dictionary<string, MapMarkerLineUI> _byId = new();
        readonly Dictionary<string, int> _epochOf = new();   // the mission each tracked line belongs to
        int _seq;

        public int Added, Removed;

        public Markers() { Instance = this; HostRules.Changed += OnRulesChanged; }

        public override void Register(Session s) => s.On((ushort)Msg.MapMarker, OnEvent);
        public override void OnSessionStart() { _idOf.Clear(); _byId.Clear(); _epochOf.Clear(); _seq = 0; }

        static bool Shared => HostRules.On(Rule.SharedMapDrawings);

        /// <summary>
        /// Handles the switch between per-player and shared drawings. When drawings become each player's own,
        /// lines already drawn stay where they are but are no longer linked, so erasing one only affects you.
        /// When they become shared, everyone sends the others the lines they currently have.
        /// </summary>
        void OnRulesChanged(uint was, uint now)
        {
            if (!HostRules.Flipped(was, now, Rule.SharedMapDrawings) || !Coop.InSession) return;
            if (!Shared) { _idOf.Clear(); _byId.Clear(); _epochOf.Clear(); CoopLog.Info("[markers] map drawings are each player's own now"); return; }
            if (!(WorldReady.LocalReady || Coop.IsHost)) return;
            int n = 0;
            foreach (var p in Placers())
                for (int i = 0; i < p.placedMarkers.Count; i++)
                {
                    var m = p.placedMarkers[i];
                    if (m == null || _idOf.ContainsKey(m.Pointer)) continue;
                    SendAdd(p, i, null); n++;
                }
            CoopLog.Info($"[markers] map drawings are shared now; showed the others {n} line(s) of mine");
        }

        float _nextGoneCheck;

        /// <summary>
        /// Lines can vanish without an erase click: the map's "delete all" button destroys the marker objects
        /// directly (a guest's press runs on the host). Any tracked marker that is gone here, and was not removed by
        /// a message from another player, is announced as removed.
        /// </summary>
        public override void Tick(float dt)
        {
            if (!Coop.InSession || !Shared || Time.unscaledTime < _nextGoneCheck) return;
            _nextGoneCheck = Time.unscaledTime + 0.5f;
            if (!(WorldReady.LocalReady || Coop.IsHost) || Coop.Applying) return;
            int epoch = MissionFlow.Instance.Epoch;
            List<string> gone = null, old = null;
            foreach (var kv in _byId)
            {
                if (!_epochOf.TryGetValue(kv.Key, out var e) || e != epoch) (old ??= new()).Add(kv.Key);   // a line from an earlier mission, so remove it everywhere
                else if (kv.Value == null) (gone ??= new()).Add(kv.Key);
            }
            if (old != null) foreach (var id in old) { _byId.Remove(id); _epochOf.Remove(id); }
            if (gone == null) { if (old != null) PruneIds(); return; }
            foreach (var id in gone)
            {
                _byId.Remove(id); _epochOf.Remove(id);
                var w = Begin(Ev.Remove); w.Str(id); Coop.Send(Msg.MapMarker, w);
                Removed++;
            }
            PruneIds();
            CoopLog.Debug($"[markers] {gone.Count} line(s) deleted here; told the others");
        }

        void PruneIds()
        {
            foreach (var k in new List<IntPtr>(_idOf.Keys)) if (!_byId.ContainsKey(_idOf[k])) _idOf.Remove(k);
        }

        static NetWriter Begin(Ev ev) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); return w; }

        static List<MapMarkerPlacer> Placers()
        {
            var l = new List<MapMarkerPlacer>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MapMarkerPlacer>(), true)) { var p = o.TryCast<MapMarkerPlacer>(); if (p != null) l.Add(p); }
            return l;
        }

        string Track(MapMarkerLineUI m, string id = null)
        {
            if (_idOf.TryGetValue(m.Pointer, out var have)) return have;
            id ??= $"{Coop.Session?.LocalSlot ?? 0}:{++_seq}";
            _idOf[m.Pointer] = id; _byId[id] = m; _epochOf[id] = MissionFlow.Instance.Epoch;
            return id;
        }

        /// <summary>
        /// The game's save record for one marker, built from the marker itself (the placer's CaptureMarkers is not
        /// index-aligned with placedMarkers). Target = origin + tip (the tip is relative to the origin).
        /// </summary>
        static MapMarkerSaveData Capture(MapMarkerPlacer placer, int index)
        {
            var m = index >= 0 && index < placer.placedMarkers.Count ? placer.placedMarkers[index] : null;
            if (m == null) return null;
            string prefab = m.gameObject.name.Replace("(Clone)", "").Trim();
            var o = m._OriginLocal_k__BackingField; var t = m._TipLocalPosition_k__BackingField;
            var data = new MapMarkerSaveData { PlacerIndex = 0, PrefabName = prefab };
            data.Origin = new SaveVector2(o.x, o.y);
            data.Target = new SaveVector2(o.x + t.x, o.y + t.y);
            // Record which labels are shown (a radius circle hides its distance number). Without this the receiver would show them all.
            try { data.LabelsVisible = m.LabelsVisible; data.LabelStates = m.CaptureLabelStates(); }
            catch (System.Exception e) { CoopLog.ErrorOnce("markers-labels", "[markers] reading label states failed: " + e.Message); }
            return data;
        }

        void SendAdd(MapMarkerPlacer placer, int index, byte? slot)
        {
            var m = placer.placedMarkers[index];
            var data = Capture(placer, index);
            if (m == null || data == null) { CoopLog.Debug($"[markers] could not capture marker {index} of {placer.name} (captured {(data == null ? "none" : "ok")})"); return; }
            CoopLog.Debug($"[markers] sending {Track(m)} ({data.PrefabName})");
            var w = Begin(Ev.Add); w.Str(Track(m)); w.Str(ScenePath.Of(placer.transform)); w.Str(GameJson.Write(data));
            if (slot is byte one) Coop.Session.Send((ushort)Msg.MapMarker, w, Delivery.Reliable, one);
            else Coop.Send(Msg.MapMarker, w);
        }

        public override void SendSnapshot(byte slot)
        {
            if (!Shared) return;
            foreach (var p in Placers())
                for (int i = 0; i < p.placedMarkers.Count; i++) SendAdd(p, i, slot);
        }

        void OnEvent(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            if (!(WorldReady.LocalReady || Coop.IsHost) || !Shared) return;
            switch ((Ev)r.U8())
            {
                case Ev.Add:
                {
                    string id = r.Str(64), placerPath = r.Str(2048), json = r.Str(1 << 20);
                    if (_byId.TryGetValue(id, out var have) && have != null) return;   // already shown
                    var placer = ScenePath.Find(placerPath)?.GetComponent<MapMarkerPlacer>();
                    var data = GameJson.Read<MapMarkerSaveData>(json);
                    if (placer == null || data == null) { CoopLog.Debug($"[markers] cannot show {id} (placer {placerPath})"); return; }
                    int before = placer.placedMarkers.Count;
                    Coop.Apply("marker " + id, () => placer.RestoreMarker(data));
                    if (placer.placedMarkers.Count > before)
                    {
                        var shown = placer.placedMarkers[placer.placedMarkers.Count - 1];
                        Track(shown, id); Added++;
                        if (HostRules.On(Rule.SharedMeasureNotes)) WriteNote(placer, shown);
                    }
                    CoopLog.Debug($"[markers] showing {id} from slot {c.FromSlot}: placer had {before}, now {placer.placedMarkers.Count}");
                    break;
                }
                case Ev.Remove:
                {
                    string id = r.Str(64);
                    if (!_byId.TryGetValue(id, out var m) || m == null) return;
                    foreach (var p in Placers()) p.placedMarkers.Remove(m);
                    _byId.Remove(id); _idOf.Remove(m.Pointer);
                    Coop.Apply("erase " + id, () => UnityEngine.Object.Destroy(m.gameObject));
                    Removed++;
                    break;
                }
            }
        }

        public int NotesWritten;

        /// <summary>
        /// Host rule "share map measurement notes": a measurement line another player finished writes its bearing and
        /// distance on this player's notepad too. The game's own note writer (MarkerNoteLogger) does it, the same way it
        /// does for the drawer, so the note looks the same.
        /// </summary>
        void WriteNote(MapMarkerPlacer placer, MapMarkerLineUI marker)
        {
            try
            {
                if (marker == null || string.IsNullOrEmpty(marker.DistanceLabelText)) return;   // not a measurement
                if (LaunchArgs.Mutant("nomeasurenotes")) return;   // used by the tests to prove the note depends on this
                // the note writer sits on this player's notepad and listens to every placer's finished lines
                foreach (var logger in UnityEngine.Object.FindObjectsOfType<MarkerNoteLogger>())
                {
                    if (logger == null || !logger.isActiveAndEnabled) continue;
                    Coop.Apply("measurement note", () => logger.LogMarkerData(marker));
                    NotesWritten++;
                    return;
                }
                CoopLog.Debug("[markers] no note writer on this notepad: no note");
            }
            catch (System.Exception e) { CoopLog.ErrorOnce("markers-note", "[markers] writing a shared measurement note failed: " + e.Message); }
        }

        /// <summary>
        /// After a local draw: every marker nobody has seen yet goes to everyone. This used to send only the markers
        /// past the count at the press, but if another player erased a marker during the drag the count shrank and the
        /// new line was never sent. Markers shown from others, and ones already sent, are tracked, so they are skipped.
        /// </summary>
        public void AfterDraw(MapMarkerPlacer placer, int countBefore)
        {
            if (!Coop.InSession || Coop.Applying || !Shared) return;
            int n = placer.placedMarkers?.Count ?? 0;
            bool old = LaunchArgs.Mutant("markercount");
            for (int i = old ? countBefore : 0; i < n; i++)
            {
                var m = placer.placedMarkers[i];
                if (m != null && (old || !_idOf.ContainsKey(m.Pointer))) SendAdd(placer, i, null);
            }
        }

        /// <summary>After a local erase: tracked markers of <paramref name="before"/> that are gone.</summary>
        public void AfterErase(MapMarkerPlacer placer, List<MapMarkerLineUI> before)
        {
            if (!Coop.InSession || Coop.Applying || !Shared) return;
            foreach (var m in before)
            {
                if (m == null || placer.placedMarkers.Contains(m) || !_idOf.TryGetValue(m.Pointer, out var id)) continue;   // never shared: nothing to remove elsewhere
                _byId.Remove(id); _idOf.Remove(m.Pointer);
                var w = Begin(Ev.Remove); w.Str(id); Coop.Send(Msg.MapMarker, w);
            }
        }

        // ------------------------------------------------------------------ patches

        // The game creates the new marker when the button goes DOWN (HandlePrimaryPressed) and finalizes its
        // shape on release, so the count before the press is what tells new from old.
        static readonly Dictionary<System.IntPtr, int> _countAtPress = new();

        [HarmonyPatch(typeof(MapMarkerPlacer), "HandlePrimaryPressed")]
        static class Pressed
        {
            static void Prefix(MapMarkerPlacer __instance)
            {
                try { _countAtPress[__instance.Pointer] = __instance.placedMarkers?.Count ?? 0; }
                catch (System.Exception e) { CoopLog.PatchFailed("Markers.Prefix line 212", e); }
            }
        }

        /// <summary>A drawing finished here: markers added since the press go to everyone, in their final shape.</summary>
        [HarmonyPatch(typeof(MapMarkerPlacer), "HandlePrimaryReleased")]
        static class Released
        {
            static void Postfix(MapMarkerPlacer __instance)
            {
                if (!_countAtPress.Remove(__instance.Pointer, out int before)) return;
                try { Instance?.AfterDraw(__instance, before); }
                catch (System.Exception e) { CoopLog.ErrorOnce("markers-draw", "[markers] sending a drawing failed: " + e); }
            }
        }

        /// <summary>An erase here: whichever tracked markers disappeared go out as removals.</summary>
        [HarmonyPatch(typeof(MapMarkerPlacer), "HandleSecondaryPressed")]
        static class Erased
        {
            static void Prefix(MapMarkerPlacer __instance, out List<MapMarkerLineUI> __state)
            {
                __state = new List<MapMarkerLineUI>();
                try { if (__instance.placedMarkers != null) foreach (var m in __instance.placedMarkers) __state.Add(m); }
                catch (System.Exception e) { CoopLog.PatchFailed("Markers.Erased.Prefix", e); }
            }

            static void Postfix(MapMarkerPlacer __instance, List<MapMarkerLineUI> __state)
            {
                try { Instance?.AfterErase(__instance, __state); }
                catch (System.Exception e) { CoopLog.PatchFailed("Markers.Postfix line 237", e); }
            }
        }
    }
}
