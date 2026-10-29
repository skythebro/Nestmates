using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static MapMarkerPlacer Placer =>
            Object.FindObjectsOfType(Il2CppType.Of<MapMarkerPlacer>(), true).Select(o => o.TryCast<MapMarkerPlacer>()).FirstOrDefault(p => p != null);

        static string PlacerDiag() => string.Join("; ", Object.FindObjectsOfType(Il2CppType.Of<MapMarkerPlacer>(), true).Select(o => o.TryCast<MapMarkerPlacer>()).Where(p => p != null)
            .Select(p => $"{Util.ScenePath.Of(p.transform)} active {p.isActiveAndEnabled} prefabs {p.markerPrefabs?.Count} known {p.knownMarkerPrefabs?.Count} active-prefab {p.activeMarkerPrefab?.name} placed {p.placedMarkers?.Count}"));

        static GameObject MarkerPrefab(MapMarkerPlacer p) =>
            p.activeMarkerPrefab ?? (p.markerPrefabs != null && p.markerPrefabs.Count > 0 ? p.markerPrefabs[0] : (p.knownMarkerPrefabs != null && p.knownMarkerPrefabs.Count > 0 ? p.knownMarkerPrefabs[0] : null));

        static string MarkerLine(MapMarkerLineUI m) =>
            m == null ? "-" : $"{m.gameObject.name.Replace("(Clone)", "")}:{m._OriginLocal_k__BackingField.x:F1};{m._OriginLocal_k__BackingField.y:F1}>{m._TipLocalPosition_k__BackingField.x:F1};{m._TipLocalPosition_k__BackingField.y:F1} labels {Labels(m)}";

        /// <summary>Which of the marker's labels show (1/0 each), e.g. a circle hides its distance number.</summary>
        static string Labels(MapMarkerLineUI m)
        {
            try { var st = m.CaptureLabelStates(); var sb = new System.Text.StringBuilder(); if (st != null) foreach (var l in st) sb.Append(l.Active ? '1' : '0'); return sb.Length == 0 ? "-" : sb.ToString(); }
            catch { return "?"; }
        }

        static int LiveMarkers(MapMarkerPlacer p) { int n = 0; foreach (var m in p.placedMarkers) if (m != null) n++; return n; }

        static string MarkerLines(MapMarkerPlacer p) => string.Join("|", Enumerable.Range(0, p.placedMarkers.Count).Select(i => MarkerLine(p.placedMarkers[i])));

        static Camera _drawCam;

        /// <summary>
        /// Draw the way a player does: the game's virtual cursor on the map, the placer's own HandlePrimaryPressed
        /// (creates the marker; our hook counts here), the tip dragged to the end point, HandlePrimaryReleased
        /// (finalizes; our hook sends). The bot's camera looks straight at the map so the placer's raycast hits it.
        /// Returns false (with a note) if the game did not create a marker.
        /// </summary>
        static bool Draw(Bot bot, MapMarkerPlacer p, Vector2 from, Vector2 to)
        {
            var rt = p.GetComponent<RectTransform>() ?? p.transform.Cast<RectTransform>();
            var canvas = p.GetComponentInParent<Canvas>();
            if (_drawCam == null)
            {
                var go = new GameObject("Coop QA draw camera");
                Object.DontDestroyOnLoad(go);
                _drawCam = go.AddComponent<Camera>();
                _drawCam.enabled = false;   // used for raycasts only, never renders
            }
            var center = rt.TransformPoint(rt.rect.center);
            _drawCam.transform.position = center + rt.forward * -1.5f;
            _drawCam.transform.rotation = Quaternion.LookRotation(rt.forward, rt.up);
            _drawCam.fieldOfView = 60;
            var oldMain = p.mainCamera; var oldCanvasCam = canvas != null ? canvas.worldCamera : null;
            p.mainCamera = _drawCam;
            if (canvas != null) canvas.worldCamera = _drawCam;
            int before = p.placedMarkers.Count;
            try
            {
                Vector2 Screen(Vector2 local) { var w = rt.TransformPoint(new Vector3(local.x, local.y, 0)); var sp = _drawCam.WorldToScreenPoint(w); return new Vector2(sp.x, sp.y); }
                p.virtualCursor?.WarpTo(Screen(from));
                p.HandlePrimaryPressed();
                if (p.placedMarkers.Count == before) { bot.Note($"the placer made no marker at {from} (cursor {p.virtualCursor?.ScreenPosition}, placers: {PlacerDiag()})"); p.HandlePrimaryReleased(); return false; }
                var m = p.placedMarkers[p.placedMarkers.Count - 1];
                p.virtualCursor?.WarpTo(Screen(to));
                m._TipLocalPosition_k__BackingField = m._OriginLocal_k__BackingField + (to - from);   // the drag (Update moves the tip only while the button is held)
                p.HandlePrimaryReleased();
                return p.placedMarkers.Count > before;
            }
            finally
            {
                p.mainCamera = oldMain;
                if (canvas != null) canvas.worldCamera = oldCanvasCam;
            }
        }

        /// <summary>Erase like the placer does on a secondary click on a marker.</summary>
        static void Erase(MapMarkerPlacer p, int index)
        {
            var before = new List<MapMarkerLineUI>();
            foreach (var m in p.placedMarkers) before.Add(m);
            var gone = p.placedMarkers[index];
            p.placedMarkers.RemoveAt(index);
            Object.Destroy(gone.gameObject);
            Markers.Instance.AfterErase(p, before);
        }

        static IEnumerable<object> MarkersHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Note("placers: " + PlacerDiag());
            var p = Placer;
            bot.Check(p != null && MarkerPrefab(p) != null, "a map marker placer with a marker type exists");
            if (p == null || MarkerPrefab(p) == null) yield break;
            int start = p.placedMarkers.Count;
            bot.Check(Draw(bot, p, new Vector2(-40, 30), new Vector2(60, -20)), "host drew a line through the game's own drawing handlers");
            // the second one with a circle / radius tool, if the map has one (its distance number stays hidden)
            GameObject circle = null;
            var types = new List<GameObject>();
            if (p.markerPrefabs != null) foreach (var pf in p.markerPrefabs) if (pf != null) types.Add(pf);
            if (p.knownMarkerPrefabs != null) foreach (var pf in p.knownMarkerPrefabs) if (pf != null) types.Add(pf);
            foreach (var pf in types) if (pf.name.Contains("Circle") || pf.name.Contains("Radius") || pf.name.Contains("Range") || pf.name.Contains("Disc")) circle = pf;
            bot.Note($"marker types: {string.Join(", ", types.Select(x => x.name))}; circle {circle?.name ?? "none"}");
            var keepPrefab = p.activeMarkerPrefab;
            if (circle != null) p.activeMarkerPrefab = circle;
            bot.Check(Draw(bot, p, new Vector2(10, 10), new Vector2(-50, 80)), "host drew a second line");
            p.activeMarkerPrefab = keepPrefab;
            bot.Signal("drawn", MarkerLines(p));
            foreach (var x in bot.Until(() => bot.HasSignal("guestdrew"), 60, "guest drew")) yield return x;
            foreach (var x in bot.Until(() => p.placedMarkers.Count == start + 3, 10, "the guest's drawing is here")) yield return x;
            bot.Check(MarkerLine(p.placedMarkers[p.placedMarkers.Count - 1]) == bot.SignalValue("guestdrew"),
                      $"guest drawing identical ({MarkerLine(p.placedMarkers[p.placedMarkers.Count - 1])} vs {bot.SignalValue("guestdrew")})");
            Erase(p, start);
            bot.Signal("erased", MarkerLines(p));
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 90, "guest verdict")) yield return x;
            if (bot.HasSignal("cleared")) bot.Check(LiveMarkers(p) == 0, $"the guest's delete-all cleared the lines on the host ({LiveMarkers(p)} left)");
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> MarkersGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Placer != null && bot.HasSignal("drawn"), 180, "in mission, host drew")) yield return x;
            var p = Placer;
            string want = bot.SignalValue("drawn");
            foreach (var x in bot.Until(() => MarkerLines(p) == want, 10, null, soft: true)) yield return x;
            bot.Check(MarkerLines(p) == want, $"the host's drawings are here, identical ({MarkerLines(p)} vs {want})");
            bot.Check(Draw(bot, p, new Vector2(-70, -60), new Vector2(30, 45)), "guest drew a line through the game's own drawing handlers");
            bot.Signal("guestdrew", MarkerLine(p.placedMarkers[p.placedMarkers.Count - 1]));
            foreach (var x in bot.Until(() => bot.HasSignal("erased"), 60, "host erased one")) yield return x;
            string left = bot.SignalValue("erased");
            foreach (var x in bot.Until(() => MarkerLines(p) == left, 10, "the same drawing was erased here")) yield return x;
            bot.Note($"markers here: {MarkerLines(p)}; added {Markers.Instance.Added}, removed {Markers.Instance.Removed}");

            // the map's "delete all" button, pressed here: it runs on the host, and every line must go everywhere
            LookAtTarget clear = null;
            var hits = Wiring.Callers("DeleteAllChildren");
            foreach (var h in hits) clear ??= h.Owner.TryCast<LookAtTarget>() ?? h.Owner.GetComponent<LookAtTarget>();
            bot.Note("DeleteAllChildren is wired from: " + Wiring.Describe(hits));
            bot.Check(clear != null, "the map's delete-all button exists");
            if (clear != null)
            {
                foreach (var x in Press(bot, clear)) yield return x;
                foreach (var x in bot.Until(() => LiveMarkers(p) == 0, 8, null, soft: true)) yield return x;
                bot.Check(LiveMarkers(p) == 0, $"the guest's delete-all cleared the lines here ({LiveMarkers(p)} left)");
                bot.Signal("cleared");
            }
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
