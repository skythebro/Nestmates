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
    /// Join in progress: the host plays alone for a while (turret turned, a card printed, a drawing, a token moved,
    /// a record playing), then the guest joins (-coop-join-delay) and must see exactly the host's world.
    /// </summary>
    public static partial class Scenarios
    {
        static string LateState(out string units)
        {
            units = WorldHash(out int n) + "/" + n;
            var parts = new List<string>
            {
                "turret " + Turret._CurrentAngle_k__BackingField.ToString("F1"),
                "cards " + string.Join(",", Cards().Select(c => ItemId.Of(c)).OrderBy(x => x)),
                "markers " + (Placer != null ? MarkerLines(Placer) : "-"),
                "music " + Music.Instance.Line.Split('|').Take(3).Aggregate((a, b) => a + "|" + b),
                "guns " + Gunnery.CurrentDigest,
            };
            return string.Join(" ; ", parts);
        }

        static IEnumerable<object> LateJoinHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost, 30, "hosting")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => Turret != null && Computer != null, 60, "mission ready")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(Coop.Session.PlayerCount == 1, "playing alone before the guest joins");

            // things a player does alone
            var hd = Turret.rotationDial;
            foreach (var x in DragDial(bot, hd, hd.AccumulatedValue + 4f * Mathf.Max(0.01f, Turret.dialDegreesPerTurretDegree))) yield return x;
            foreach (var x in DragDial(bot, Computer.rangeDial, 13f)) yield return x;
            foreach (var x in DragDial(bot, Computer.powderChargeDial, 4f)) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            Computer.calculateButton.OnClickDown();
            foreach (var x in bot.Wait(0.15)) yield return x;
            Computer.calculateButton.OnClickUp();
            foreach (var x in bot.Until(() => Cards().Count > 0, 15, "a card printed")) yield return x;
            if (Placer != null && MarkerPrefab(Placer) != null) bot.Check(Draw(bot, Placer, new Vector2(-30, 20), new Vector2(40, -35)), "host drew before the guest joined");
            var token = Object.FindObjectsOfType(Il2CppType.Of<DraggableItem>()).Select(o => o.TryCast<DraggableItem>())
                .FirstOrDefault(d => d != null && d.gameObject.activeInHierarchy && MissionSaveManager.IsMapToken(d) && d.CurrentLocation == DraggableItem.ItemLocation.Surface);
            if (token != null) Nudge(token);
            var p = RecordPlayer;
            var rec = Records().FirstOrDefault(r => r.gameObject.activeInHierarchy && r.tracks != null && r.tracks.Length > 0);
            if (p != null && rec != null)
            {
                Music.PlaceInto(rec.GetComponent<DraggableItem>() ?? rec.GetComponentInParent<DraggableItem>(), p.slot);
                foreach (var x in bot.Until(() => p.playButton != null && p.playButton.isActive, 15, "play button active")) yield return x;
                foreach (var x in Press(bot, p.playButton)) yield return x;
            }
            foreach (var x in bot.Wait(4)) yield return x;
            bot.Note("host world before the guest joins: " + LateState(out var u0) + " ; units " + u0);

            // the guest joins in progress
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 2, 240, "the guest joined in progress")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "the guest loaded the running mission")) yield return x;
            foreach (var x in bot.Wait(5)) yield return x;
            string state = LateState(out var units);
            bot.Note("host world now: " + state + " ; units " + units);
            bot.Signal("state", state);
            bot.Signal("units", units);
            if (token != null) bot.Signal("token", ItemId.Of(token) + "^" + Where(token));
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 90, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> LateJoinGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 300, "joined late")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Turret != null, 180, "in the running mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("state"), 60, "host state")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            string mine = LateState(out var units);
            var want = bot.SignalValue("state").Split(" ; ");
            var have = mine.Split(" ; ");
            for (int i = 0; i < want.Length; i++)
                bot.Check(i < have.Length && have[i] == want[i], $"late joiner sees the host's {want[i].Split(' ')[0]} ({(i < have.Length ? have[i] : "-")} vs {want[i]})");
            bot.Check(units == bot.SignalValue("units"), $"late joiner sees the host's units ({units} vs {bot.SignalValue("units")})");
            if (bot.HasSignal("token"))
            {
                var t = bot.SignalValue("token").Split('^');
                var d = ItemId.Find<DraggableItem>(t[0]);
                bot.Check(d != null && SamePlace(Where(d), t[1]), $"late joiner sees the moved map token ({Where(d)} vs {t[1]})");
            }
            bot.Note($"music drift {Music.Instance.LastDrift:F2} s");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
