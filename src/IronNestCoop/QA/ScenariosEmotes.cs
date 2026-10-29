using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>
        /// Press a keyboard key through the Input System, as the hardware would (a state event queued on the
        /// keyboard), and release it a frame later. False when the event could not be built here.
        /// </summary>
        static IEnumerable<object> PressKey(Bot bot, Key key, System.Action<bool> done)
        {
            bool ok = true;
            foreach (bool down in new[] { true, false })
            {
                try
                {
                    var kb = Keyboard.current;
                    if (kb == null) { ok = false; break; }
                    var buf = StateEvent.From(kb, out InputEventPtr ev);
                    try { kb[key].WriteValueIntoEvent(down ? 1f : 0f, ev); InputSystem.QueueEvent(ev); }
                    finally { buf.Dispose(); }
                }
                catch (System.Exception e) { bot.Note("could not queue a key event: " + e.Message); ok = false; break; }
                foreach (var x in bot.Wait(0.15)) yield return x;
            }
            done(ok);
        }

        /// <summary>
        /// The figure's new tricks, seen from the other machine: an emote from the number keys, the operating flag
        /// while holding something, and the figure staying at the tactical map table (in sight) while its player
        /// works the map. Also streamer mode: the codes start hidden.
        /// </summary>
        static IEnumerable<object> EmotesHost(Bot bot)
        {
            bot.Check(Settings.HideJoinCodes && UI.CoopUI.Hidden, "streamer mode is on by default: codes and addresses hidden");
            bot.Check(UI.L.Mask("3388F60A") == "••••••••", "a hidden code shows as one dot per character");
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            if (Coop.IsTcp) { UI.CoopUI.Show(0); foreach (var x in bot.Wait(0.5)) yield return x; foreach (var x in bot.Shot("host-lobby-hidden")) yield return x; }
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Fpc != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Until(() => Avatars.Instance.TryGetAvatar(1, out _, out _), 30, "guest figure built")) yield return x;

            // 1. emote
            int before = Avatars.EmotesPlayed;
            bot.Signal("emote");
            foreach (var x in bot.Until(() => bot.HasSignal("emoted"), 30, "guest pressed 3")) yield return x;
            foreach (var x in bot.Until(() => Avatars.EmotesPlayed > before, 5, "the guest's figure plays an emote", soft: true)) yield return x;
            bot.Check(Avatars.EmotesPlayed == before + 1 && Avatars.LastEmotePlayed == 3, $"the guest's emote 3 played once on the host (played {Avatars.EmotesPlayed - before}, last {Avatars.LastEmotePlayed})");
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(Avatars.EmotesPlayed == before + 1, "the emote does not repeat while the poses keep coming");

            // 2. operating
            bot.Signal("operate");
            foreach (var x in bot.Until(() => Avatars.Instance.TryGetActivity(1, out bool op, out _, out _) && op, 10, "the guest's figure is operating")) yield return x;
            bot.Signal("release");
            foreach (var x in bot.Until(() => Avatars.Instance.TryGetActivity(1, out bool op, out _, out _) && !op, 10, "the guest's figure stops operating")) yield return x;

            // 2b. carrying the cat: it lies in the figure's arms, not in front of its face
            bot.Signal("carry");
            foreach (var x in bot.Until(() => bot.HasSignal("carrying"), 30, "guest carries its cat")) yield return x;
            bool held = false; Vector3 catAt = default;
            foreach (var x in bot.Until(() => Cats.Instance.TryGetPuppet(1, out catAt, out held) && held, 10, "the guest's cat puppet is carried", soft: true)) yield return x;
            foreach (var x in bot.Wait(0.8)) yield return x;
            Cats.Instance.TryGetPuppet(1, out catAt, out held);
            if (Avatars.Instance.TryGetAvatar(1, out var feet, out float fyaw))
            {
                var rel = catAt - feet;
                var fwd = Quaternion.Euler(0, fyaw, 0) * Vector3.forward;
                float up = rel.y, ahead = Vector3.Dot(new Vector3(rel.x, 0, rel.z), fwd), side = Vector3.Dot(new Vector3(rel.x, 0, rel.z), Quaternion.Euler(0, fyaw, 0) * Vector3.right);
                bot.Note($"carried cat {up:F2} m up, {ahead:F2} m ahead, {side:F2} m aside; shown parts: {Cats.Instance.PuppetParts(1)}");
                bool cradle = Avatars.Instance.TryGetHoldPoint(1, out var hold);
                float off = cradle ? Vector3.Distance(catAt, hold) : 99f;
                bot.Check(held && cradle && off < 0.05f && up > 0.7f && up < 1.5f && ahead > 0.1f && ahead < 0.8f && Mathf.Abs(side) < 0.35f,
                    $"the carried cat lies in the figure's arms ({up:F2} up, {ahead:F2} ahead, {side:F2} aside; {off:F2} m from the cradle)");
                // a picture of it, from the front
                var go = new GameObject("qa-cat-camera");
                var qcam = go.AddComponent<Camera>();
                qcam.depth = 100; qcam.nearClipPlane = 0.05f; qcam.fieldOfView = 50;
                var eye = feet + fwd * 2.2f + Vector3.up * 1.5f;
                go.transform.SetPositionAndRotation(eye, Quaternion.LookRotation(feet + Vector3.up * 1.1f - eye));
                foreach (var x in bot.Wait(0.3)) yield return x;
                foreach (var x in bot.Shot("host-sees-guest-carry-cat")) yield return x;
                Object.Destroy(go);
            }
            bot.Signal("drop");
            foreach (var x in bot.Until(() => Cats.Instance.TryGetPuppet(1, out _, out bool h) && !h, 10, "the guest's cat is put down again")) yield return x;

            // 3. at the tactical map
            bot.Signal("map");
            foreach (var x in bot.Until(() => bot.HasSignal("atmap"), 40, "guest at the tactical map")) yield return x;
            float from = -1;
            foreach (var x in bot.Until(() => Avatars.Instance.TryGetActivity(1, out _, out bool m, out from) && m && from >= 0 && from < 1.2f, 10, "the guest's figure at the map table", soft: true)) yield return x;
            Avatars.Instance.TryGetActivity(1, out _, out bool atMap, out from);
            bot.Note($"table: {Avatars.Table()}; sides with floor: {Avatars.TableSides}");
            bot.Check(atMap && from >= 0 && from < 1.2f, $"the figure stands at the table edge while its player works the map ({from:F2} m from the table)");
            // pointing all over the table: the figure stays on its side (it used to jump to whichever side was nearest
            // the pointer) and keeps its distance from the edge
            bot.Signal("sweep");
            var sides = new List<int>(); var dists = new List<float>();
            for (int i = 0; i < 5; i++)
            {
                foreach (var x in bot.Until(() => bot.HasSignal("pt" + i), 20, "guest points at spot " + i)) yield return x;
                foreach (var x in bot.Wait(1.0)) yield return x;
                if (Avatars.Instance.TryGetAvatar(1, out var fp, out _) && Avatars.Instance.TryGetActivity(1, out _, out _, out float fd))
                { sides.Add(Avatars.NearestSide(fp)); dists.Add(fd); }
                bot.Signal("seenpt" + i);
            }
            bot.Note($"sides while pointing: {string.Join(",", sides)}; distances from the table: {string.Join(", ", dists.Select(d => d.ToString("F2")))}");
            bot.Check(sides.Count == 5 && sides.TrueForAll(x => x == sides[0]), $"pointing anywhere on the table, the figure stays on one side ({string.Join(",", sides)})");
            bot.Check(dists.Count == 5 && dists.TrueForAll(d => d > 0.6f && d < 0.9f), $"it stands about 0.75 m from the table edge ({string.Join(", ", dists.Select(d => d.ToString("F2")))})");
            // look at them with a camera of our own (the player rig cannot be walked there reliably: the walls around
            // the table have no colliders): across the table, and from the side along its edge
            if (UI.CoopUI.IsOpen) UI.CoopUI.Toggle();
            if (Avatars.Instance.TryGetAvatar(1, out var gp, out _) && Avatars.Table() is Bounds tb)
            {
                var outward = gp - tb.center; outward.y = 0; outward.Normalize();
                var along = Vector3.Cross(Vector3.up, outward);
                var chest = gp + Vector3.up * 1.1f;
                float half = Mathf.Abs(Vector3.Dot(tb.extents, new Vector3(Mathf.Abs(outward.x), 0, Mathf.Abs(outward.z))));
                var views = new (string Name, Vector3 Pos)[]
                {
                    ("host-sees-guest-at-map", new Vector3(tb.center.x, tb.min.y + 1.9f, tb.center.z) - outward * (half + 0.6f) + (gp - tb.center - outward * Vector3.Dot(gp - tb.center, outward)) ),
                    ("host-sees-guest-at-map-side", gp + along * 2.2f + outward * 0.4f + Vector3.up * 1.7f),
                };
                var go = new GameObject("qa-avatar-camera");
                var qcam = go.AddComponent<Camera>();
                qcam.depth = 100; qcam.nearClipPlane = 0.05f; qcam.fieldOfView = 55;
                bool seen = false;
                foreach (var v in views)
                {
                    go.transform.position = v.Pos;
                    go.transform.rotation = Quaternion.LookRotation(chest - v.Pos);
                    foreach (var x in bot.Wait(0.4)) yield return x;
                    bool vis = Avatars.Instance.IsVisible(1, out var diag);
                    seen |= vis;
                    bot.Note($"{v.Name}: camera at {v.Pos:F2}, figure rendered {vis}");
                    foreach (var x in bot.Shot(v.Name)) yield return x;
                    if (v.Name == "host-sees-guest-at-map")
                    {
                        bot.Signal("emote2");
                        foreach (var x in bot.Wait(0.8)) yield return x;
                        foreach (var x in bot.Shot("host-sees-guest-emote-at-map")) yield return x;
                    }
                }
                Object.Destroy(go);
                bot.Check(seen, "the figure at the map is rendered (not hidden)");
            }
            else bot.Check(false, "the guest's figure and the map table exist");

            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> EmotesGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Fpc != null, 180, "in mission with a player")) yield return x;
            if (!UI.CoopUI.IsOpen) UI.CoopUI.Show(0);   // holds the player still (no field focused: the keys work)

            foreach (var x in bot.Until(() => bot.HasSignal("emote"), 120, "host asks for an emote")) yield return x;
            int sent = Avatars.EmotesSent;
            bool queued = false;
            foreach (var x in PressKey(bot, Key.Digit3, ok => queued = ok)) yield return x;
            foreach (var x in bot.Wait(0.3)) yield return x;
            if (queued) bot.Check(Avatars.EmotesSent == sent + 1, $"key 3 sends one emote (sent {Avatars.EmotesSent - sent})");
            else { bot.Note("no key event here: emote sent directly"); Avatars.PlayEmote(3); }
            bot.Check(UI.Toasts.Recent.Count > 0 && UI.Toasts.Recent[UI.Toasts.Recent.Count - 1].StartsWith("You: "), "the player sees which emote they did");
            // with Shift held (the dev shortcuts) the digits do nothing: not testable without holding a key; skipped
            bot.Signal("emoted");

            foreach (var x in bot.Until(() => bot.HasSignal("operate"), 30, "host asks to operate")) yield return x;
            var f = Fpc;
            Avatars.TestReach = Avatars.Feet(f) + f.transform.forward * 0.6f + Vector3.up * 1.1f;
            Avatars.TestOperating = true;
            foreach (var x in bot.Until(() => bot.HasSignal("release"), 30, "host saw it")) yield return x;
            Avatars.TestOperating = false;

            foreach (var x in bot.Until(() => bot.HasSignal("carry"), 30, "host asks to carry the cat")) yield return x;
            var ctl = Object.FindObjectOfType<CatCustomizationController>();
            var myCat = ctl != null ? ctl.currentCatController : null;
            var pickup = Object.FindObjectOfType<CatPickUpHandler>();
            bool real = false;
            if (myCat != null && pickup != null)
            {
                try { pickup.ExecutePickUp(myCat); real = pickup.heldCat != null; }
                catch (System.Exception e) { bot.Note("pick up: " + e.Message); }
            }
            if (!real) { bot.Note("the cat could not be picked up here: carried by the test hook"); Cats.TestCarried = true; }
            else bot.Note("the guest picked up its cat the game's way");
            bot.Signal("carrying");
            foreach (var x in bot.Until(() => bot.HasSignal("drop"), 30, "host saw it")) yield return x;
            if (real) { try { pickup.ExecuteExternalDrop(); } catch (System.Exception e) { bot.Note("drop: " + e.Message); } }
            Cats.TestCarried = false;

            foreach (var x in bot.Until(() => bot.HasSignal("map"), 30, "host asks for the map")) yield return x;
            CameraZoneTrigger zone = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CameraZoneTrigger>()))
            {
                var z = o.TryCast<CameraZoneTrigger>();
                if (z != null && (Util.ScenePath.Of(z.transform) ?? "").Contains("Tactical Map")) { zone = z; break; }
            }
            bot.Check(zone != null, "found the tactical map's camera zone");
            if (zone != null)
            {
                // stand in the zone (as walking up to the table does), then use it
                var c = zone.GetComponent<Collider>();
                if (c != null)
                {
                    var spot = c.bounds.center;
                    if (Physics.Raycast(spot + Vector3.up * 1f, Vector3.down, out var hit, 4f, ~0, QueryTriggerInteraction.Ignore)) spot = hit.point;
                    // beside the table on its floor, not on it
                    if (Avatars.Table() is Bounds table)
                        foreach (var side in new[] { new Vector3(table.min.x - 0.5f, 0, table.center.z), new Vector3(table.max.x + 0.5f, 0, table.center.z), new Vector3(table.center.x, 0, table.min.z - 0.5f), new Vector3(table.center.x, 0, table.max.z + 0.5f) })
                            if (Physics.Raycast(new Vector3(side.x, table.max.y + 1f, side.z), Vector3.down, out var h2, 4f, ~0, QueryTriggerInteraction.Ignore) && Mathf.Abs(h2.point.y - table.min.y) < 0.3f)
                            { spot = h2.point; break; }
                    Teleport(f, spot + Vector3.up * 0.05f, f.transform.eulerAngles.y);
                    foreach (var x in Landed(bot, f)) yield return x;
                }
                if (UI.CoopUI.IsOpen) UI.CoopUI.Toggle();
                zone.ForceActivate();
                foreach (var x in bot.Until(() => CameraZoneTrigger.s_currentActiveZone == zone && zone.consoleActive, 10, "the map view is on", soft: true)) yield return x;
                foreach (var x in bot.Wait(1)) yield return x;
                foreach (var x in bot.Shot("guest-at-map")) yield return x;
            }
            bot.Signal("atmap");
            foreach (var x in bot.Until(() => bot.HasSignal("sweep"), 30, "host asks to point around")) yield return x;
            if (Avatars.Table() is Bounds tb)
            {
                var spots = new[] { new Vector3(tb.min.x + 0.1f, 0, tb.min.z + 0.1f), new Vector3(tb.max.x - 0.1f, 0, tb.min.z + 0.1f), new Vector3(tb.max.x - 0.1f, 0, tb.max.z - 0.1f), new Vector3(tb.min.x + 0.1f, 0, tb.max.z - 0.1f), tb.center };
                for (int i = 0; i < spots.Length; i++)
                {
                    Avatars.TestPoint = new Vector3(spots[i].x, tb.max.y, spots[i].z); Avatars.TestPointOn = true;
                    bot.Signal("pt" + i);
                    foreach (var x in bot.Until(() => bot.HasSignal("seenpt" + i), 20, "host saw spot " + i)) yield return x;
                }
            }
            Avatars.TestPointOn = false;
            foreach (var x in bot.Until(() => bot.HasSignal("emote2"), 60, "host asks for a point")) yield return x;
            Avatars.PlayEmote(2);
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
            if (zone != null) zone.ForceDeactivate();
        }
    }
}
