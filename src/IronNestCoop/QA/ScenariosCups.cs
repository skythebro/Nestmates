using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// Regression test: the host moved the coffee things and the guest saw only one cup move. The base's items
    /// are where each player's own game left them (the host's cup in the machine, the guest's on the counter), and
    /// their ids were their paths: the other cups and the can were "unknown" on the other machine.
    /// Here the guest moves a cup elsewhere before the session; then both must name every coffee item the same,
    /// the guest's cup must go where the host's is, and a move by the host must show on the guest.
    /// </summary>
    public static partial class Scenarios
    {
        static List<DraggableItem> CoffeeItems() =>
            Object.FindObjectsOfType<DraggableItem>()
                .Where(d => d != null && (ScenePath.Of(d.transform) ?? "").Contains("Expresso Machine/")
                            && (d.GetComponentInChildren<EspressoCup>() != null || d.GetComponentInChildren<CoffeeGroundsCan>() != null))
                .ToList();

        static string CoffeeIds() => string.Join("\n", CoffeeItems().Select(d => ItemId.Of(d)).OrderBy(s => s));

        static string PosLine(Vector3 p) => $"{p.x:R}|{p.y:R}|{p.z:R}";
        static Vector3 ParsePos(string s) { var a = s.Split('|'); return new Vector3(float.Parse(a[0]), float.Parse(a[1]), float.Parse(a[2])); }

        static IEnumerable<object> CupsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && EspressoMachine != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Note($"coffee items here: {string.Join(" | ", CoffeeItems().Select(d => ScenePath.Of(d.transform)))}");
            bot.Signal("hostids", CoffeeIds());
            foreach (var x in bot.Until(() => bot.HasSignal("moved"), 60, "guest says which cup it had moved")) yield return x;
            var cup = CoffeeItems().FirstOrDefault(d => d.name == bot.SignalValue("moved"));
            bot.Check(cup != null, $"the guest's moved cup exists here ({bot.SignalValue("moved")})");
            if (cup != null) bot.Signal("where", PosLine(cup.transform.position));
            foreach (var x in bot.Until(() => bot.HasSignal("wherechecked"), 30, "guest compared the cup")) yield return x;
            if (cup != null)
            {
                // the host moves it along its counter, as a hand would put it down a little further on
                var surface = cup.GetComponentInParent<DragSurface>();
                cup.transform.position += (surface != null ? surface.transform.right : Vector3.right) * 0.06f;
                foreach (var x in bot.Wait(1.5)) yield return x;   // the move settles and is sent
                bot.Signal("nudged", PosLine(cup.transform.position));
            }
            else bot.Signal("nudged", "none");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> CupsGuest(Bot bot)
        {
            // before the session: this player's own game has a cup somewhere else than the host's
            foreach (var x in bot.Until(() => EspressoMachine != null && CoffeeItems().Count > 0, 120, "the base is loaded")) yield return x;
            var moved = CoffeeItems().FirstOrDefault(d => d.GetComponentInChildren<EspressoCup>() != null && (ScenePath.Of(d.transform) ?? "").Contains("/Drag Surface/"))
                        ?? CoffeeItems().FirstOrDefault(d => d.GetComponentInChildren<EspressoCup>() != null);
            bot.Check(moved != null, "a cup to move");
            if (moved == null) yield break;
            string before = ScenePath.Of(moved.transform);
            moved.transform.SetParent(moved.transform.parent.parent, true);
            bot.Note($"guest moved {before} -> {ScenePath.Of(moved.transform)}");

            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && EspressoMachine != null && bot.HasSignal("hostids"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            string mine = CoffeeIds(), host = bot.SignalValue("hostids");
            bot.Note($"coffee items here: {string.Join(" | ", CoffeeItems().Select(d => ScenePath.Of(d.transform)))}");
            bot.Check(mine == host, $"every coffee item has the same name here and on the host (here [{mine.Replace("\n", ", ")}] / host [{host.Replace("\n", ", ")}])");
            bot.Signal("moved", moved.name);

            foreach (var x in bot.Until(() => bot.HasSignal("where"), 30, "host's cup position")) yield return x;
            var hostAt = ParsePos(bot.SignalValue("where"));
            foreach (var x in bot.Until(() => moved != null && (moved.transform.position - hostAt).magnitude < 0.02f, 5, null, soft: true)) yield return x;
            bot.Check(moved != null && (moved.transform.position - hostAt).magnitude < 0.02f, $"the cup this player had moved is where the host's is ({moved?.transform.position:F3} vs {hostAt:F3})");
            bot.Signal("wherechecked");

            foreach (var x in bot.Until(() => bot.HasSignal("nudged"), 30, "host moved the cup")) yield return x;
            if (bot.SignalValue("nudged") != "none")
            {
                var to = ParsePos(bot.SignalValue("nudged"));
                foreach (var x in bot.Until(() => moved != null && (moved.transform.position - to).magnitude < 0.02f, 5, null, soft: true)) yield return x;
                bot.Check(moved != null && (moved.transform.position - to).magnitude < 0.02f, $"the host's move shows here ({moved?.transform.position:F3} vs {to:F3})");
            }
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
