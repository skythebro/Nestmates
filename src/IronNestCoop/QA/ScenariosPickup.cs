using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// F to pick up: the guest takes a record from the bench onto their own clipboard with the game's own pick-up
    /// code (ClipboardPickUpHandler.ExecutePickUp, what the F key runs). The host must see it gone from the bench
    /// (it is in the guest's hands), then back on the bench where the guest put it down.
    /// </summary>
    public static partial class Scenarios
    {
        static DraggableItem RecordOnBench() =>
            Object.FindObjectsOfType(Il2CppType.Of<RecordItem>(), true).Select(o => o.TryCast<RecordItem>())
                .Where(r => r != null).Select(r => r.GetComponentInParent<DraggableItem>())
                .FirstOrDefault(d => d != null && d.CurrentLocation == DraggableItem.ItemLocation.Surface
                                     && !(ScenePath.Of(d.surfaceRef?.transform) ?? "").Contains("Main Camera/"));

        static IEnumerable<object> PickupHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            var d = RecordOnBench();
            bot.Check(d != null, "a record lies on a shared surface");
            if (d == null) yield break;
            if (!d.gameObject.activeInHierarchy) d.gameObject.SetActive(true);   // fixture: locked records are hidden
            string id = ItemId.Of(d);
            bot.Note($"record {id} on {ScenePath.Of(d.surfaceRef?.transform)} at {d.transform.position:F3}");
            bot.Signal("record", id);

            foreach (var x in bot.Until(() => bot.HasSignal("picked"), 60, "guest picked the record up")) yield return x;
            foreach (var x in bot.Until(() => !d.gameObject.activeSelf, 5, null, soft: true)) yield return x;
            bot.Check(!d.gameObject.activeSelf, $"the record left the bench here while the guest holds it (held {Items.Instance.HeldApplied})");
            bot.Signal("seen");

            foreach (var x in bot.Until(() => bot.HasSignal("putback"), 60, "guest put it back")) yield return x;
            var want = bot.SignalValue("putback").Split('^');
            var wantPos = new Vector3(float.Parse(want[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(want[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(want[2], System.Globalization.CultureInfo.InvariantCulture));
            foreach (var x in bot.Until(() => d.gameObject.activeSelf && (d.transform.position - wantPos).magnitude < 0.01f, 8, null, soft: true)) yield return x;
            bot.Check(d.gameObject.activeSelf && (d.transform.position - wantPos).magnitude < 0.01f,
                      $"the record is back on the bench here, where the guest put it ({d.gameObject.activeSelf}, {d.transform.position:F3} vs {wantPos:F3})");
            bot.Signal("verdict");
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> PickupGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("record"), 200, "in mission, host chose a record")) yield return x;
            var d = ItemId.Find<DraggableItem>(bot.SignalValue("record"));
            bot.Check(d != null, "the same record exists here");
            if (d == null) yield break;
            if (!d.gameObject.activeInHierarchy) d.gameObject.SetActive(true);   // fixture, as on the host
            foreach (var x in bot.Wait(1)) yield return x;
            var bench = MissionSaveManager.CaptureDraggableSurfaceState(d);
            var handler = Object.FindObjectOfType<ClipboardPickUpHandler>();
            bot.Check(handler != null, "the F-to-pick-up handler exists");
            if (handler == null) yield break;
            handler.ExecutePickUp(d);
            foreach (var x in bot.Wait(3)) yield return x;   // slide onto the clipboard, settle, send
            bot.Check((ScenePath.Of(d.surfaceRef?.transform) ?? "").Contains("Main Camera/"), $"the record is on my clipboard ({ScenePath.Of(d.surfaceRef?.transform)})");
            bot.Signal("picked");
            foreach (var x in bot.Until(() => bot.HasSignal("seen"), 30, "host checked")) yield return x;

            // put it down again: the game's own placement restore, onto the bench it came from
            var all = Object.FindObjectsOfType(Il2CppType.Of<DragSurface>(), true);
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DragSurface>(all.Length);
            for (int i = 0; i < all.Length; i++) arr[i] = all[i].TryCast<DragSurface>();
            MissionSaveManager.RestoreDraggableSurfaceState(d, bench, arr);
            foreach (var x in bot.Wait(2)) yield return x;
            var p = d.transform.position;
            bot.Note($"put back on {ScenePath.Of(d.surfaceRef?.transform)} at {p:F3}");
            bot.Signal("putback", string.Join("^", new[] { p.x, p.y, p.z }.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture))));
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
