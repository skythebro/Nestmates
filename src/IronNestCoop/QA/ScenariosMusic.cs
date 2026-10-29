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
        static RecordPlayerController RecordPlayer => Object.FindObjectOfType<RecordPlayerController>();

        static AudioSource ActiveSrc(RecordPlayerController p) => p._useAAsActive ? p.audioSourceA : p.audioSourceB;

        static List<RecordItem> Records() =>
            Object.FindObjectsOfType(Il2CppType.Of<RecordItem>(), true).Select(o => o.TryCast<RecordItem>()).Where(r => r != null).ToList();

        static string SlotDiag(RecordPlayerController p)
        {
            var it = p.slot.CurrentItem;
            var box = p.slot.boxCol;
            string cols = it == null ? "-" : string.Join(",", it.GetComponentsInChildren<Collider>(true).Select(c => $"{c.GetIl2CppType().Name}{(c.enabled ? "" : "(off)")}{(c.isTrigger ? "T" : "")}{(c.gameObject.activeInHierarchy ? "" : "(inactive)")}"));
            return $"slot {(it == null ? "empty" : it.name)} overlaps {(it == null ? "-" : p.slot.Overlaps(it).ToString())} box {(box == null ? "none" : $"{(box.enabled ? "on" : "off")} {box.bounds.center:F3} size {box.bounds.size:F3}")} item at {(it == null ? "-" : it.transform.position.ToString("F3"))} colliders {cols}";
        }

        static IEnumerable<object> MusicHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && RecordPlayer != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var p = RecordPlayer;
            bot.Note($"records in scene: {string.Join(", ", Records().Select(r => $"{r.name}({(r.gameObject.activeInHierarchy ? "unlocked" : "locked")})"))}; on player: {p._currentRecord?.name ?? "none"}; playing {p._isPlaying}");
            bot.Signal("ready");

            // 1. the guest puts a record on (if none) and presses play: both happen here, on the host
            foreach (var x in bot.Until(() => bot.HasSignal("unlock") || bot.HasSignal("pressed") || p.slot.CurrentItem != null, 30, "guest chose a record", soft: true)) yield return x;
            if (bot.HasSignal("unlock")) { var ur = Util.ItemId.Find<RecordItem>(bot.SignalValue("unlock")); bot.Note($"fixture: unlocking {ur?.name} here too"); ur?.gameObject.SetActive(true); }
            foreach (var x in bot.Until(() => p.slot.CurrentItem != null || bot.HasSignal("pressed"), 30, "a record on the host's player", soft: true)) yield return x;
            bot.Note("host slot: " + SlotDiag(p));
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Note("host slot 1 s later: " + SlotDiag(p));
            foreach (var x in bot.Until(() => bot.HasSignal("pressed"), 90, "guest pressed play")) yield return x;
            foreach (var x in bot.Until(() => p._isPlaying && ActiveSrc(p).isPlaying, 20, "host record playing")) yield return x;
            bot.Note($"host playing: {Music.Instance.Line}");
            foreach (var x in bot.Wait(3)) yield return x;
            var hostRec = p._currentRecord == null ? null : (p._currentRecord.GetComponent<DraggableItem>() ?? p._currentRecord.GetComponentInParent<DraggableItem>());
            var hr = hostRec != null ? Music.LocalRotation(hostRec, p.slot) : Quaternion.identity;
            bot.Signal("check1", string.Join("^", new[] { hr.x, hr.y, hr.z, hr.w }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            foreach (var x in bot.Until(() => bot.HasSignal("checked1"), 30, "guest checked playback")) yield return x;

            // 2. the host skips a track and jumps 20 s ahead: the guest must follow both
            if (p._currentRecord.tracks.Length > 1) p.AdvanceTrack(true);
            foreach (var x in bot.Wait(2.5)) yield return x;
            var src = ActiveSrc(p);
            if (src.clip != null && src.clip.length > 40) src.time = Mathf.Min(src.clip.length - 10, src.time + 20);
            bot.Note($"host after skip + jump: {Music.Instance.Line}");
            foreach (var x in bot.Wait(2.5)) yield return x;
            bot.Signal("check2", p._trackIndex.ToString());
            foreach (var x in bot.Until(() => bot.HasSignal("checked2"), 30, "guest checked track and position")) yield return x;

            // 3. the guest presses stop
            foreach (var x in bot.Until(() => bot.HasSignal("stopped"), 30, "guest pressed stop")) yield return x;
            foreach (var x in bot.Until(() => !p._isPlaying, 15, "host stopped")) yield return x;
            bot.Signal("check3");

            // 4. a record the host's world doesn't have (seen when the guest's own unlock reveal at the printers added one)
            var extra = Object.FindObjectsOfType(Il2CppType.Of<RecordItem>(), true).Select(o => o.TryCast<RecordItem>())
                .Where(rc => rc != null && rc.gameObject.activeInHierarchy && !string.IsNullOrEmpty(rc.gameObject.scene.name) && rc != p._currentRecord)
                .OrderBy(rc => Util.ItemId.Of(rc)).FirstOrDefault();
            bot.Check(extra != null, "a record lying in the turret");
            if (extra != null)
            {
                string extraId = Util.ItemId.Of(extra);
                foreach (var x in bot.Until(() => bot.HasSignal("checked3"), 30, "guest checked the stop")) yield return x;
                extra.gameObject.SetActive(false);   // gone from the host's world (not through the sync: as if it never was)
                bot.Signal("gone", extraId);
                foreach (var x in bot.Until(() => bot.HasSignal("gonechecked"), 30, "guest checked the record is gone")) yield return x;
                extra.gameObject.SetActive(true);
                bot.Signal("back");
            }
            else { bot.Signal("gone", "skip"); }
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> MusicGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && RecordPlayer != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            var p = RecordPlayer;
            if (p._currentRecord == null)
            {
                var rec = Records().FirstOrDefault(r => r.gameObject.activeInHierarchy && r.tracks != null && r.tracks.Length > 0);
                if (rec == null)
                {
                    // fresh test profile: nothing unlocked yet. Fixture: both players unlock the same record.
                    rec = Records().FirstOrDefault(r => r.tracks != null && r.tracks.Length > 0 && r.transform.parent != null && r.transform.parent.gameObject.activeInHierarchy);
                    bot.Check(rec != null, "a record exists");
                    if (rec == null) yield break;
                    bot.Note($"no record unlocked in the test profile: unlocking {rec.name} on both sides");
                    bot.Signal("unlock", Util.ItemId.Of(rec));
                    rec.gameObject.SetActive(true);
                    foreach (var x in bot.Wait(0.5)) yield return x;
                }
                bot.Note($"guest puts {rec.name} on the player");
                var dropped = rec.GetComponent<DraggableItem>() ?? rec.GetComponentInParent<DraggableItem>() ?? rec.GetComponentInChildren<DraggableItem>();
                // a hand drops it at some angle: the other side must show that angle, not its own
                dropped.transform.rotation = Quaternion.AngleAxis(70f, Music.Anchor(p.slot).up) * dropped.transform.rotation;
                Music.PlaceInto(rec.GetComponent<DraggableItem>() ?? rec.GetComponentInParent<DraggableItem>() ?? rec.GetComponentInChildren<DraggableItem>(), p.slot);   // as a drop at the slot
                bot.Note($"right after place: slot item {p.slot.CurrentItem?.name ?? "none"}, hasItem {p.slot.HasItem}, current record {p._currentRecord?.name ?? "none"}");
                var di = rec.GetComponent<DraggableItem>() ?? rec.GetComponentInParent<DraggableItem>() ?? rec.GetComponentInChildren<DraggableItem>();
                var anchor = p.slot.itemAnchor != null ? p.slot.itemAnchor : p.slot.transform;
                for (int f = 0; f < 8; f++)
                {
                    if (f == 0) bot.Note("guest slot: " + SlotDiag(p));
                    bot.Note($"  frame {f}: item at {Vector3.Distance(di.transform.position, anchor.position):F3} m from anchor, overlaps {p.slot.Overlaps(di)}, slot {(p.slot.CurrentItem == null ? "empty" : "full")}, parent {di.transform.parent?.name}, loc {di.CurrentLocation}");
                    foreach (var x in bot.Wait(f < 4 ? 0.0 : 0.4)) yield return x;
                }
                bot.Note($"2 s after place: slot item {p.slot.CurrentItem?.name ?? "none"}, current record {p._currentRecord?.name ?? "none"}, button active {p.playButton?.isActive}");
            }
            foreach (var x in bot.Until(() => p.playButton != null && p.playButton.isActive, 15, "play button active")) yield return x;
            foreach (var x in Press(bot, p.playButton)) yield return x;
            bot.Signal("pressed");

            foreach (var x in bot.Until(() => bot.HasSignal("check1"), 60, "host playing for a while")) yield return x;
            foreach (var x in bot.Wait(1.2)) yield return x;   // at least one position sample after settling
            bot.Note($"guest: {Music.Instance.Line}, drift {Music.Instance.LastDrift:F2} s, corrections {Music.Instance.Corrections}");
            bot.Check(p._isPlaying && ActiveSrc(p).isPlaying, "guest record playing");
            bot.Check(Mathf.Abs(Music.Instance.LastDrift) < 0.3f, $"guest playback within 0.3 s of the host ({Music.Instance.LastDrift:F2} s)");
            var hv = bot.SignalValue("check1").Split('^').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            var mineRec = p._currentRecord == null ? null : (p._currentRecord.GetComponent<DraggableItem>() ?? p._currentRecord.GetComponentInParent<DraggableItem>());
            float angle = mineRec == null ? 999 : Quaternion.Angle(Music.LocalRotation(mineRec, p.slot), new Quaternion(hv[0], hv[1], hv[2], hv[3]));
            // a few degrees come from the slot still settling when the two sides sample (1 s apart); the bug this
            // guards against was a record standing on edge (90 degrees)
            bot.Check(angle < 5f, $"the record lies in the player the same way on both ({angle:F1} degrees apart)");
            bot.Signal("checked1");

            foreach (var x in bot.Until(() => bot.HasSignal("check2"), 60, "host skipped and jumped")) yield return x;
            foreach (var x in bot.Wait(1.2)) yield return x;
            bot.Note($"guest after skip + jump: {Music.Instance.Line}, drift {Music.Instance.LastDrift:F2} s");
            bot.Check(p._trackIndex.ToString() == bot.SignalValue("check2"), $"guest on the host's track ({p._trackIndex} vs {bot.SignalValue("check2")})");
            bot.Check(Mathf.Abs(Music.Instance.LastDrift) < 0.3f, $"guest followed the host's jump ({Music.Instance.LastDrift:F2} s)");
            bot.Signal("checked2");

            foreach (var x in Press(bot, p.playButton)) yield return x;
            bot.Signal("stopped");
            foreach (var x in bot.Until(() => bot.HasSignal("check3"), 30, "host stopped")) yield return x;
            foreach (var x in bot.Until(() => !p._isPlaying, 10, "guest stopped too")) yield return x;
            foreach (var x in bot.Shot("guest-music")) yield return x;
            bot.Signal("checked3");
            foreach (var x in bot.Until(() => bot.HasSignal("gone"), 30, "host took a record out of its world")) yield return x;
            string goneId = bot.SignalValue("gone");
            if (goneId != "skip")
            {
                var rec = Util.ItemId.Find<RecordItem>(goneId);
                bot.Check(rec != null, $"the record {goneId} is known here");
                foreach (var x in bot.Until(() => rec == null || !rec.gameObject.activeInHierarchy, 6, null, soft: true)) yield return x;
                bot.Check(rec != null && !rec.gameObject.activeInHierarchy, $"a record the host's world doesn't have is hidden here ({goneId})");
                bot.Signal("gonechecked");
                foreach (var x in bot.Until(() => bot.HasSignal("back"), 30, "host has it again")) yield return x;
                foreach (var x in bot.Until(() => rec == null || rec.gameObject.activeInHierarchy, 6, null, soft: true)) yield return x;
                bot.Check(rec != null && rec.gameObject.activeInHierarchy, "once the host has it again, it is shown here again");
            }
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
