using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static string Tail(string t) => (t ?? "").Length > 120 ? t.Substring(t.Length - 120).Replace("\n", " / ") : (t ?? "").Replace("\n", " / ");

        static string AllNotes() => string.Join("\n", Resources.FindObjectsOfTypeAll<NotepadSection>()
            .Where(n => n != null && n.TargetText != null && !string.IsNullOrEmpty(n.gameObject.scene.name)).Select(n => n.TargetText.text ?? ""));

        /// <summary>
        /// Host rule "share map measurement notes" (1.0.2, off by default). With it on, a measurement line the guest
        /// finishes writes its bearing and distance on the host's notepad too, the same note the guest's own notepad gets
        /// (twin: --host-extra=-coop-mutant=nomeasurenotes).
        /// </summary>
        static IEnumerable<object> MeasureNotesHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            Settings.SharedMapDrawings = true; Settings.SharedMeasureNotes = true;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(HostRules.On(Rule.SharedMeasureNotes), "the host turned on shared measurement notes");
            bot.Signal("draw", AllNotes().Length.ToString());
            foreach (var x in bot.Until(() => bot.HasSignal("note"), 60, "guest drew a measurement line")) yield return x;
            // the guest's line, as it arrived here: with the rule on, its bearing and distance note is on this notepad
            // (the bot's drag doesn't make the guest's own notepad write one, but the shared path doesn't need that)
            var pl = Placer;
            var line = pl != null && pl.placedMarkers.Count > 0 ? pl.placedMarkers[pl.placedMarkers.Count - 1] : null;
            string dist = line?.DistanceLabelText ?? "";
            foreach (var x in bot.Until(() => dist.Length > 0 && AllNotes().Contains(dist), 8, null, soft: true)) yield return x;
            bot.Check(dist.Length > 0 && AllNotes().Contains(dist),
                $"the guest's measurement line wrote its note on the host's notepad ({line?.name} {dist}; notes '{Tail(AllNotes())}')");
            Settings.SharedMeasureNotes = false;
            bot.Signal("done");
            foreach (var x in bot.Wait(2)) yield return x;
        }

        static IEnumerable<object> MeasureNotesGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Placer != null && bot.HasSignal("draw") && HostRules.On(Rule.SharedMeasureNotes), 180, "in mission, rule on")) yield return x;
            var p = Placer;
            string before = AllNotes();
            foreach (var lg in Resources.FindObjectsOfTypeAll<MarkerNoteLogger>().Where(l => l != null && !string.IsNullOrEmpty(l.gameObject.scene.name)))
            {
                var own = lg.GetComponent<MapMarkerPlacer>() ?? lg.GetComponentInParent<MapMarkerPlacer>() ?? lg.GetComponentInChildren<MapMarkerPlacer>();
                bot.Note($"note writer {Util.ScenePath.Of(lg.transform)} active {lg.isActiveAndEnabled}, its placer {(own == null ? "none" : own.name)}, section {(lg.targetSection == null ? "none" : lg.targetSection.name)}");
            }
            // the pencils pick the pen; the red line is the measuring one
            var redPen = Resources.FindObjectsOfTypeAll<GameObject>().FirstOrDefault(g => g != null && g.name == "MapMarkerRED" && string.IsNullOrEmpty(g.scene.name));
            bot.Note($"placer in use: {Util.ScenePath.Of(p.transform)}; red pen prefab found {redPen != null}");
            if (redPen != null) p.SetActiveMarkerPrefab(redPen);
            bot.Check(Draw(bot, p, new Vector2(-40, 30), new Vector2(60, -20)), "guest drew a line through the game's own drawing handlers");
            foreach (var x in bot.Wait(6)) yield return x;   // the note is typed out, not written at once
            var mine = p.placedMarkers.Count > 0 ? p.placedMarkers[p.placedMarkers.Count - 1] : null;
            string dist = mine?.DistanceLabelText ?? "";
            string after = AllNotes();
            bool wrote = after.Length > before.Length && dist.Length > 0 && after.Contains(dist);
            bot.Note($"guest line {mine?.name}: distance label '{dist}', own note written {wrote}; notes before '{Tail(before)}', after '{Tail(after)}'");
            bot.Signal("note", wrote ? dist : "-");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host checked")) yield return x;
        }
    }
}
