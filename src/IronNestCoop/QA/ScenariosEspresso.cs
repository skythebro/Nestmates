using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static EspressoBrewingController EspressoMachine => Object.FindObjectOfType<EspressoBrewingController>();

        static string CupLine(EspressoCup c) => c == null ? "none" : $"{(c.isFull ? "full" : "empty")}|{c.quality:F3}";

        static IEnumerable<object> EspressoHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && EspressoMachine != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var m = EspressoMachine;
            bot.Note($"host machine: {Espresso.Instance.Line}, can {m._loadedCan?.name}, cup {m._loadedCup?.name}, ideal {m.idealBrewSeconds:F0} s");
            bot.Signal("ready");
            foreach (var x in bot.Until(() => bot.HasSignal("brewing"), 60, "guest started a brew")) yield return x;
            foreach (var x in bot.Until(() => m.currentState == EspressoBrewingController.BrewState.Brewing, 10, "the host's machine is brewing")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("line", Espresso.Instance.Line);
            foreach (var x in bot.Until(() => bot.HasSignal("stopped"), 60, "guest finished the brew")) yield return x;
            foreach (var x in bot.Until(() => m.currentState == EspressoBrewingController.BrewState.Complete, 10, "the host's brew completed")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            var cup = m._loadedCup;
            bot.Note($"host cup: {CupLine(cup)}");
            bot.Signal("cup", ItemId.Of(cup) + "^" + CupLine(cup));
            foreach (var x in bot.Until(() => bot.HasSignal("drank"), 60, "guest drank")) yield return x;
            foreach (var x in bot.Until(() => cup != null && !cup.isFull, 5, "the cup is empty here too")) yield return x;
            // regression: the host should hear the guest's slurp
            var cupDrinker = cup == null ? null : (cup.GetComponentInParent<EspressoCupDrinker>() ?? cup.GetComponentInChildren<EspressoCupDrinker>());
            bot.Note($"drink events: started {cupDrinker?.OnDrinkStarted?.GetPersistentEventCount()} emptied {cupDrinker?.OnDrinkEmptied?.GetPersistentEventCount()}" +
                     (cupDrinker == null ? "" : " | " + string.Join(", ", Enumerable.Range(0, cupDrinker.OnDrinkStarted.GetPersistentEventCount()).Select(i => $"{cupDrinker.OnDrinkStarted.GetPersistentTarget(i)?.GetIl2CppType().Name}.{cupDrinker.OnDrinkStarted.GetPersistentMethodName(i)}"))));
            bot.Check(Espresso.Instance.DrinksHeard > 0, $"the guest's drink was heard here ({Espresso.Instance.DrinksHeard})");
            bot.Check(Espresso.Instance.DrinksShown > 0, $"the guest's drink was shown at their figure here ({Espresso.Instance.DrinksShown})");
            static string Listeners(UnityEngine.Events.UnityEventBase ev) => ev == null ? "-" : string.Join(", ", Enumerable.Range(0, ev.GetPersistentEventCount()).Select(i => $"{ev.GetPersistentTarget(i)?.name}:{ev.GetPersistentTarget(i)?.GetIl2CppType().Name}.{ev.GetPersistentMethodName(i)}"));
            bot.Note($"brew events: started [{Listeners(m.OnBrewStarted)}] complete [{Listeners(m.OnBrewComplete)}] cup filled [{Listeners(cup?.OnCupFilled)}]");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> EspressoGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && EspressoMachine != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            var m = EspressoMachine;
            var can = Object.FindObjectsOfType<CoffeeGroundsCan>().FirstOrDefault(cn => cn.remainingUses > 0);
            var cup = Object.FindObjectsOfType<EspressoCup>().FirstOrDefault(cp => !cp.isFull);
            bot.Check(can != null && cup != null, $"a can and an empty cup exist ({can?.name}, {cup?.name})");
            if (can == null || cup == null) yield break;
            if (m._loadedCan == null) Music.PlaceInto(can.GetComponent<DraggableItem>(), m.groundsSlot);
            if (m._loadedCup == null) Music.PlaceInto(cup.GetComponent<DraggableItem>(), m.cupSlot);
            foreach (var x in bot.Wait(3)) yield return x;   // the items reach the host's machine
            bot.Note($"guest machine after loading: {Espresso.Instance.Line}");
            foreach (var x in Press(bot, m.brewButton)) yield return x;
            bot.Signal("brewing");
            foreach (var x in bot.Until(() => bot.HasSignal("line"), 30, "host brewing")) yield return x;
            foreach (var x in bot.Wait(0.4)) yield return x;
            var h = bot.SignalValue("line").Split('|'); var g = Espresso.Instance.Line.Split('|');
            bot.Check(g[0] == h[0], $"the guest's machine brews too ({Espresso.Instance.Line} vs {bot.SignalValue("line")})");
            bot.Check(System.Math.Abs(float.Parse(g[1]) - float.Parse(h[1])) < 3f, $"same temperature on the gauge ({g[1]} vs {h[1]})");
            foreach (var x in bot.Wait(Mathf.Max(1f, m.idealBrewSeconds - 2.5f))) yield return x;
            foreach (var x in Press(bot, m.brewButton)) yield return x;
            bot.Signal("stopped");
            foreach (var x in bot.Until(() => bot.HasSignal("cup"), 30, "host's cup")) yield return x;
            var hc = bot.SignalValue("cup").Split('^');
            var mine = ItemId.Find<EspressoCup>(hc[0]);
            foreach (var x in bot.Until(() => mine != null && CupLine(mine) == hc[1], 5, null, soft: true)) yield return x;
            bot.Check(CupLine(mine) == hc[1], $"the same coffee here ({CupLine(mine)} vs {hc[1]}), fills {Espresso.Instance.Fills}");
            // regression: no steam appeared over the cup on the guest (the brew's start/finish effects only fire on the host)
            bot.Check(Espresso.Instance.EffectsReplayed >= 2, $"the brew's start and finish effects played here too ({Espresso.Instance.EffectsReplayed})");
            // the guest drinks it the game's way (the drink animation to the camera, then the cup is empty)
            var drinker = mine == null ? null : (mine.GetComponent<EspressoCupDrinker>() ?? mine.GetComponentInParent<EspressoCupDrinker>() ?? mine.GetComponentInChildren<EspressoCupDrinker>());
            bot.Check(drinker != null, "the cup can be drunk (EspressoCupDrinker)");
            if (drinker != null) drinker.DrinkCoffee(); else mine?.MarkEmpty();
            foreach (var x in bot.Until(() => mine == null || !mine.isFull, 15, "the cup is empty after drinking it here")) yield return x;
            bot.Signal("drank");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
