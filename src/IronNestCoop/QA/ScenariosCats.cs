using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static string MyCatLine()
        {
            var c = Cats.Instance.LocalCat;
            var i = PlayerInfo.Local;
            return c == null ? "none" : $"{c.transform.position.x};{c.transform.position.y};{c.transform.position.z};{(i.CatIsKitten ? "kitten" : "cat")}/body{i.CatBody}/eyes{i.CatEyes}/hat{i.CatHat}";
        }

        /// <summary>Waits until a puppet of the other player's cat stands within 1 m of where they say it is and looks right.</summary>
        static IEnumerable<object> CheckPuppet(Bot bot, byte slot, string signal, string who)
        {
            foreach (var x in bot.Until(() => bot.HasSignal(signal), 60, $"{who} cat position")) yield return x;
            var q = bot.SignalValue(signal).Split(';');
            bot.Check(q.Length == 4, $"{who} has a cat ({bot.SignalValue(signal)})");
            if (q.Length != 4) yield break;
            var want = new Vector3(float.Parse(q[0]), float.Parse(q[1]), float.Parse(q[2]));
            Vector3 pos = default; string look = null; bool vis = false;
            foreach (var x in bot.Until(() => Cats.Instance.TryGetPuppet(slot, out pos, out look, out vis) && look == q[3], 20, $"puppet of the {who} cat with its look")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            Cats.Instance.TryGetPuppet(slot, out pos, out look, out vis);
            bot.Check(vis, $"the {who} cat is shown here");
            bot.Check(look == q[3], $"the {who} cat looks the same ({look} vs {q[3]})");
            bot.Check(Vector3.Distance(pos, want) < 1.0f, $"the {who} cat is where they see it ({Vector3.Distance(pos, want):F2} m off)");
        }

        static IEnumerable<object> CatsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Cats.Instance.LocalCat != null, 120, "guest loaded, my cat spawned")) yield return x;
            var mine = Cats.Instance.LocalCat.Pointer;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("hostcat", MyCatLine());
            foreach (var x in CheckPuppet(bot, 1, "guestcat", "guest's")) yield return x;
            bot.Check(Cats.Instance.LocalCat != null && Cats.Instance.LocalCat.Pointer == mine, "my own cat is untouched by the puppet");
            bot.Check(Object.FindObjectsOfType<CatController>().Length == 1, $"exactly one live cat AI here, mine ({Object.FindObjectsOfType<CatController>().Length})");

            // the guest re-styles its cat: the puppet here must follow
            bot.Signal("restyle");
            foreach (var x in CheckPuppet(bot, 1, "guestcat2", "guest's restyled")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("guestshapes"), 30, "guest's shapes")) yield return x;
            foreach (var x in bot.Until(() => Cats.Instance.PuppetShapes(1) == bot.SignalValue("guestshapes"), 6, null, soft: true)) yield return x;
            bot.Check(Cats.Instance.PuppetShapes(1) == bot.SignalValue("guestshapes"), $"the guest's cat here has its hat and shape sliders (here {Cats.Instance.PuppetShapes(1)} / guest {bot.SignalValue("guestshapes")})");
            foreach (var x in bot.Until(() => MissionClock.TimeAsDouble > 30, 60, "mission intro over")) yield return x;
            if (Cats.Instance.TryGetPuppet(1, out var pp, out _, out _))
            {
                var f = Fpc;
                // test-only camera in front of the puppet (the cats roam different levels; moving the player is unreliable)
                var camGo = new GameObject("QA cat camera");
                var cam = camGo.AddComponent<Camera>();
                cam.depth = 100; cam.nearClipPlane = 0.05f; cam.fieldOfView = 50;
                Cats.Instance.TryGetPuppet(1, out pp, out _, out _);
                camGo.transform.position = pp + new Vector3(0.9f, 0.55f, 0.9f);
                camGo.transform.LookAt(pp + Vector3.up * 0.2f);
                UI.CoopUI.Toggle();
                foreach (var x in bot.Wait(0.5)) yield return x;
                foreach (var x in bot.Shot("host-sees-guest-cat")) yield return x;
                Object.Destroy(camGo);
            }
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> CatsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Cats.Instance.LocalCat != null, 180, "in mission, my cat spawned")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("guestcat", MyCatLine());
            foreach (var x in CheckPuppet(bot, 0, "hostcat", "host's")) yield return x;
            bot.Check(Object.FindObjectsOfType<CatController>().Length == 1, $"exactly one live cat AI here, mine ({Object.FindObjectsOfType<CatController>().Length})");

            foreach (var x in bot.Until(() => bot.HasSignal("restyle"), 60, "host asks for a restyle")) yield return x;
            var ctl = Object.FindObjectOfType<CatCustomizationController>();
            // a known problem case: the last hat ("General hat" 5) and shape sliders moved
            var cc = ctl.currentCatCustomization;
            int body = (ctl.bodyColor + 1) % Mathf.Max(1, ctl.BodyMaterials.Count), hat = cc?.hatObjects?.Count ?? 2;
            ctl.ChangeBodyColor(body);
            ctl.ChangeHatState(hat);
            int n = 0;
            foreach (var kv in ctl.blendShapeKeys)
            {
                var k = kv.Value;
                if (k == null) continue;
                ctl.SetBlendShapeValue(n == 0 ? 77 : n == 1 ? 33 : 100, k.eyes, k.body, k.fur, k.whiskers, k.blendShapeIndex);
                if (++n == 3) break;
            }
            bot.Note($"guest cat restyled: body {ctl.bodyColor}, hat {ctl.hatType}");
            foreach (var x in bot.Wait(2.5)) yield return x;   // the look is published every 2 s
            bot.Signal("guestcat2", MyCatLine());
            bot.Note("my cat: " + Cats.MyShapes());
            bot.Signal("guestshapes", Cats.MyShapes());
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 150, "host done")) yield return x;
        }
    }
}
