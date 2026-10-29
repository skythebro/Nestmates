using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static FirstPersonController Fpc => Object.FindObjectOfType<FirstPersonController>();

        /// <summary>The body with the CharacterController (mainGameObjectTransform is the Cinemachine camera rig).</summary>
        static Transform PlayerBody(FirstPersonController f) => f.transform;

        static void Teleport(FirstPersonController f, Vector3 pos, float yaw)
        {
            var c = f.controller;
            if (c != null) c.enabled = false;
            PlayerBody(f).position = pos;
            Face(f, yaw, 0);
            if (c != null) c.enabled = true;
        }

        /// <summary>Wait until a teleported player has come to rest (it drops a little onto the floor), at most 2 s.</summary>
        static IEnumerable<object> Landed(Bot bot, FirstPersonController f)
        {
            float last = float.NaN, until = Time.unscaledTime + 2f;
            foreach (var x in bot.Wait(0.3)) yield return x;
            while (Time.unscaledTime < until)
            {
                float y = Avatars.Feet(f).y;
                if (!float.IsNaN(last) && Mathf.Abs(y - last) < 0.005f) yield break;
                last = y;
                foreach (var x in bot.Wait(0.15)) yield return x;
            }
        }

        /// <summary>Turn the rendered body and camera (the controller's yaw/pitch fields alone do not).</summary>
        static void Face(FirstPersonController f, float yaw, float pitch)
        {
            f.yaw = yaw; f.pitch = pitch;
            PlayerBody(f).rotation = Quaternion.Euler(0, yaw, 0);
            if (f.cameraRoot != null) f.cameraRoot.localRotation = Quaternion.Euler(pitch, 0, 0);
        }

        static IEnumerable<object> AvatarsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Fpc != null, 120, "guest mission loaded")) yield return x;
            if (!UI.CoopUI.IsOpen) UI.CoopUI.Show(0);   // hold this player still too (see the guest)
            for (int step = 0; step < 3; step++)
            {
                foreach (var x in bot.Until(() => bot.HasSignal($"at{step}"), 60, $"guest at waypoint {step}")) yield return x;
                foreach (var x in bot.Wait(0.8)) yield return x;
                var parts = bot.SignalValue($"at{step}").Split(';');
                var want = new Vector3(float.Parse(parts[0]), float.Parse(parts[1]), float.Parse(parts[2]));
                float wantYaw = float.Parse(parts[3]);
                bool ok = Avatars.Instance.TryGetAvatar(1, out var pos, out var yaw);
                float err = ok ? Vector3.Distance(pos, want) : 999;
                bot.Check(ok && err < 0.3f, $"avatar of guest within 30 cm of waypoint {step} (error {err:F2} m)");
                bot.Check(ok && Mathf.Abs(Mathf.DeltaAngle(yaw, wantYaw)) < 5, $"avatar faces the guest's direction at waypoint {step} ({yaw:F0} vs {wantYaw:F0})");
                bot.Signal($"seen{step}");
            }
            // Ask the guest to stand 2.5 m in front of us, facing us, then check we really see them.
            var f = Fpc;
            var me = Avatars.Feet(f);
            var fwd = PlayerBody(f).forward; fwd.y = 0; fwd.Normalize();
            // a spot with floor under it (in front of some consoles there is none: the guest fell through to y -45)
            Vector3 spot = me + fwd * 2.5f;
            foreach (float dist in new[] { 2.5f, 2f, 1.5f, 3f })
                foreach (float turn in new[] { 0f, 25f, -25f, 50f, -50f })
                {
                    var dir = Quaternion.Euler(0, turn, 0) * fwd;
                    var probe = me + dir * dist;
                    if (Physics.Raycast(probe + Vector3.up * 1.2f, Vector3.down, out var hit, 2f, ~0, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.point.y - me.y) < 0.3f
                        && !Physics.Raycast(me + Vector3.up * 1f, dir, dist, ~0, QueryTriggerInteraction.Ignore))
                    { spot = hit.point; fwd = dir; goto found; }
                }
            bot.Note("no clear floor in front: using 2.5 m ahead");
            found:
            float faceYaw = Mathf.Atan2(-fwd.x, -fwd.z) * Mathf.Rad2Deg;
            bot.Signal("stand", $"{spot.x};{spot.y};{spot.z};{faceYaw}");
            foreach (var x in bot.Until(() => bot.HasSignal("standing"), 30, "guest standing in front")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            Face(f, PlayerBody(f).eulerAngles.y, 8);
            if (UI.CoopUI.IsOpen) UI.CoopUI.Toggle();   // closed: the window must not be what we look at
            foreach (var x in bot.Wait(0.2)) yield return x;
            bool vis = Avatars.Instance.IsVisible(1, out var diag);
            bot.Note("avatar render diagnostics: " + diag);
            bot.Check(vis, "guest avatar is actually rendered on the host's screen");
            if (Avatars.Instance.TryGetAvatar(1, out var gp, out var gyaw))
            {
                var toMe = Avatars.Feet(f) - gp; toMe.y = 0;
                float want = Mathf.Atan2(toMe.x, toMe.z) * Mathf.Rad2Deg;
                bot.Check(Mathf.Abs(Mathf.DeltaAngle(gyaw, want)) < 20, $"guest avatar faces the host as the guest does ({gyaw:F0} vs {want:F0})");
            }
            foreach (var x in bot.Shot("host-sees-guest")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> AvatarsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Fpc != null, 180, "in mission with a player")) yield return x;
            // the open co-op window holds the player still (the teleports below must not fall or slide; the window
            // used to start open, which is what this test was written with)
            if (!UI.CoopUI.IsOpen) UI.CoopUI.Show(0);
            foreach (var x in bot.Wait(3)) yield return x;
            var f = Fpc;
            var start = PlayerBody(f).position;
            bot.Note($"player rig: fpc='{f.name}' at {f.transform.position:F2}; actualMain='{f.actualMainGameObject?.name}' at {f.actualMainGameObject?.transform.position:F2}; mainT='{f.mainGameObjectTransform?.name}' at {f.mainGameObjectTransform?.position:F2}; controller on '{f.controller?.name}' at {f.controller?.transform.position:F2} parent '{f.controller?.transform.parent?.name}'; feet {Avatars.Feet(f):F2}");
            var offsets = new[] { new Vector3(0.8f, 0, 0), new Vector3(0.8f, 0, 0.8f), new Vector3(0, 0, 0.8f) };
            var yaws = new[] { 90f, 180f, 270f };
            for (int step = 0; step < 3; step++)
            {
                var before = Avatars.Feet(f);
                Teleport(f, start + offsets[step], yaws[step]);
                foreach (var x in Landed(bot, f)) yield return x;
                var after = Avatars.Feet(f);
                bot.Check((after - before).magnitude > 0.5f, $"teleport moved the player ({before:F2} -> {after:F2})");
                var p = Avatars.Feet(f);
                bot.Signal($"at{step}", $"{p.x};{p.y};{p.z};{f.transform.eulerAngles.y}");
                foreach (var x in bot.Until(() => bot.HasSignal($"seen{step}"), 30, $"host checked waypoint {step}")) yield return x;
            }
            foreach (var x in bot.Until(() => bot.HasSignal("stand"), 60, "host asks to stand in front")) yield return x;
            var q = bot.SignalValue("stand").Split(';');
            Teleport(f, new Vector3(float.Parse(q[0]), float.Parse(q[1]) + 0.05f, float.Parse(q[2])), float.Parse(q[3]));
            foreach (var x in Landed(bot, f)) yield return x;
            // the floor there may be a step lower than the host's: only falling out of the world is wrong
            bot.Check(Avatars.Feet(f).y > float.Parse(q[1]) - 3f, $"standing near the spot the host picked, not fallen through (feet at {Avatars.Feet(f).y:F2}, spot {q[1]})");
            bot.Signal("standing");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
