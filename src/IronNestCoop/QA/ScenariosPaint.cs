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
        static readonly Vector2[] PaintSpots = { new(0.30f, 0.40f), new(0.55f, 0.60f), new(0.70f, 0.25f) };

        static PaintableMesh FirstPaintable() =>
            Object.FindObjectsOfType(Il2CppType.Of<PaintableMesh>(), false).Select(o => o.TryCast<PaintableMesh>())
                .Where(m => m != null && m.PaintTexture != null).OrderBy(m => Util.ScenePath.Of(m.transform)).FirstOrDefault();

        /// <summary>The paint texture's colour at these spots (what the shell shows there).</summary>
        static string PaintAt(PaintableMesh m)
        {
            if (m == null || m.PaintTexture == null) return "none";
            var rt = m.PaintTexture;
            var prev = RenderTexture.active;
            var tex = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            var parts = new List<string>();
            try
            {
                RenderTexture.active = rt;
                foreach (var uv in PaintSpots)
                {
                    tex.ReadPixels(new Rect(Mathf.Clamp(uv.x * rt.width, 0, rt.width - 1), Mathf.Clamp(uv.y * rt.height, 0, rt.height - 1), 1, 1), 0, 0);
                    tex.Apply();
                    var c = tex.GetPixel(0, 0);
                    parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}/{1:F2}/{2:F2}", c.r, c.g, c.b));
                }
            }
            finally { RenderTexture.active = prev; Object.Destroy(tex); }
            return string.Join(" ", parts);
        }

        /// <summary>Same paint within blending tolerance (each channel within 0.15 at every spot).</summary>
        static bool PaintClose(string a, string b)
        {
            var x = a.Split(' '); var y = b.Split(' ');
            if (x.Length != y.Length || a == "none" || b == "none") return false;
            for (int i = 0; i < x.Length; i++)
            {
                var p = x[i].Split('/'); var q = y[i].Split('/');
                for (int k = 0; k < 3; k++)
                    if (System.Math.Abs(float.Parse(p[k], System.Globalization.CultureInfo.InvariantCulture) - float.Parse(q[k], System.Globalization.CultureInfo.InvariantCulture)) > 0.15f) return false;
            }
            return true;
        }

        /// <summary>The guest paints a shell in the magazine (the game's own stroke code); the host's shell shows the same paint.</summary>
        static IEnumerable<object> PaintHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Computer != null, 120, "guest mission loaded")) yield return x;
            // fixture: a round in a magazine (the guest gets it through the gun sync)
            var cyl = Object.FindObjectOfType<CylinderShellSelector>();
            ShellDefinition he = MissionSaveManager.FindShell("HE");
            if (he == null) foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>())) { he = o.TryCast<ShellDefinition>(); if (he != null) break; }
            bool inserted = cyl != null && he != null && cyl.TryInsertShellRuntime(he, ShellSlotPool.ShellSource.Mission, out _);
            bot.Note($"host inserted {he?.ShellId} into {cyl?.name}: {inserted}");
            foreach (var x in bot.Until(() => FirstPaintable() != null, 30, null, soft: true)) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;   // the round reaches the guest
            bot.Signal("ready", Util.ScenePath.Of(FirstPaintable()?.transform) ?? "none");
            foreach (var x in bot.Until(() => bot.HasSignal("painted"), 60, "guest painted")) yield return x;
            var m = Util.ScenePath.Find(bot.SignalValue("painted").Split('#')[0])?.GetComponent<PaintableMesh>();
            string want = bot.SignalValue("painted").Split('#')[1];
            foreach (var x in bot.Until(() => PaintClose(PaintAt(m), want), 6, null, soft: true)) yield return x;
            bot.Check(m != null && PaintClose(PaintAt(m), want), $"the guest's paint shows on the host's shell (host {PaintAt(m)}, guest {want}), dabs applied {Paint.Instance.DabsApplied}");
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> PaintGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            string path = bot.SignalValue("ready");
            var m = path == "none" ? null : Util.ScenePath.Find(path)?.GetComponent<PaintableMesh>();
            bot.Check(m != null, $"the host's paintable shell exists here ({path})");
            if (m == null) { bot.Signal("painted", "none#none"); yield break; }
            string before = PaintAt(m);
            m.BrushColor = new Color(0.9f, 0.1f, 0.1f, 1f);
            m.BrushSize = 0.08f;
            bot.Note($"guest shell: texture {(m.PaintTexture != null ? m.PaintTexture.width + "x" + m.PaintTexture.height : "none")}");
            foreach (var uv in PaintSpots)
            {
                try { m.PaintAtUV(uv); }   // the dab a mouse stroke makes (PaintStroke -> PaintAtUV)
                catch (System.Exception e) { bot.Note("painting failed: " + e.Message); }
            }
            foreach (var x in bot.Wait(0.5)) yield return x;
            string after = PaintAt(m);
            bot.Check(after != before, $"the paint shows on my own shell ({before} -> {after})");
            bot.Signal("painted", path + "#" + after);
            foreach (var x in bot.Wait(2)) yield return x;
            // regression: the host passed this player's paint back to them, and it was painted twice
            bot.Check(Paint.Instance.DabsApplied == 0, $"my own paint is not passed back to me ({Paint.Instance.DabsApplied} dabs came back)");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
