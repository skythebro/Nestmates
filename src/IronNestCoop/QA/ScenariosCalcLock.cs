using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static GameObject CalcBrokenState() => Resources.FindObjectsOfTypeAll<Transform>()
            .FirstOrDefault(x => x != null && x.name == "Broken state" && x.parent != null && x.parent.name == "Balistic Calculator Breaker"
                                 && !string.IsNullOrEmpty(x.gameObject.scene.name))?.gameObject;

        /// <summary>
        /// A mission can break the ballistic calculator (The Gorge's "BrakeCalculator"). The host can't use it then, and
        /// a guest's press must not print cards through the host either. First a normal guest press must print one
        /// (twin: --host-extra=-coop-mutant=nobrokengate).
        /// </summary>
        static IEnumerable<object> CalcLockHost(Bot bot)
        {
            bool late = LaunchArgs.Filter == "late";   // -coop-filter=late: break it before the guest joins (join replay)
            if (!late) foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            // which campaign missions really break a console (script step names only)
            var op = MissionFlow.FindOperation("Base_Campaign");
            if (op != null)
            {
                int i = 0;
                foreach (var n in op.Missions.ToArray())
                {
                    if (n?.Mission == null) continue;
                    i++;
                    var sends = new List<string>();
                    foreach (var node in n.Mission.nodes)
                        if (node?.TryCast<SleepyNodes.State_SendSceneNotification>() is SleepyNodes.State_SendSceneNotification sn && sn.MessageID != null && sn.MessageID.StartsWith("Brake"))
                            sends.Add(sn.MessageID);
                    if (sends.Count > 0) bot.Note($"campaign mission {i} ({n.Mission.MissionID}) sends {string.Join(", ", sends)}");
                }
            }
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "Artillery Introduction")) yield return x;
            if (!late)
            {
                foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
                foreach (var x in bot.Wait(2)) yield return x;
                int c0 = Cards().Count;
                bot.Signal("press1");
                foreach (var x in bot.Until(() => bot.HasSignal("pressed1"), 60, "guest pressed Calculate")) yield return x;
                foreach (var x in bot.Until(() => Cards().Count > c0, 10, null, soft: true)) yield return x;
                bot.Check(Cards().Count == c0 + 1, $"a guest's Calculate press prints a card while the calculator works ({c0} -> {Cards().Count})");
            }

            // break it the way the mission does
            var breaker = Resources.FindObjectsOfTypeAll<MissionGraphNotificationListener>()
                .FirstOrDefault(l => l != null && l.MessageID == "BrakeCalculator" && !string.IsNullOrEmpty(l.gameObject.scene.name));
            bot.Check(breaker != null, "the calculator breaker exists");
            if (breaker != null) breaker.Trigger("BrakeCalculator");
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(CalcBrokenState() != null && CalcBrokenState().activeInHierarchy, "the calculator is broken on the host");
            if (late)
            {
                foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 240, "the guest joined after the break")) yield return x;
                foreach (var x in bot.Wait(3)) yield return x;
            }

            int c1 = Cards().Count;
            bot.Signal("press2");
            foreach (var x in bot.Until(() => bot.HasSignal("pressed2"), 60, "guest pressed the broken Calculate")) yield return x;
            foreach (var x in bot.Wait(6)) yield return x;
            bot.Check(Cards().Count == c1, $"a guest's press on the broken calculator prints no card ({c1} -> {Cards().Count})");
            bot.Signal("done");
            foreach (var x in bot.Until(() => bot.HasSignal("guestdone"), 30, "guest done")) yield return x;
        }

        static IEnumerable<object> CalcLockGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Computer != null, 180, "in mission")) yield return x;
            var c = Computer;

            foreach (var x in bot.Until(() => bot.HasSignal("press1") || bot.HasSignal("press2"), 240, "host ready")) yield return x;
            if (!bot.HasSignal("press2"))   // joined late: the calculator is already broken, no first press
            {
                foreach (var x in DragDial(bot, c.rangeDial, 13f)) yield return x;
                foreach (var x in DragDial(bot, c.powderChargeDial, 4f)) yield return x;
                foreach (var x in bot.Wait(0.5)) yield return x;
                foreach (var x in Press(bot, c.calculateButton)) yield return x;
                bot.Signal("pressed1");
            }
            else foreach (var x in DragDial(bot, c.powderChargeDial, 4f)) yield return x;

            foreach (var x in bot.Until(() => bot.HasSignal("press2"), 60, "host broke the calculator")) yield return x;
            bot.Check(CalcBrokenState() != null && CalcBrokenState().activeInHierarchy, "the calculator is broken for the guest too");
            foreach (var x in DragDial(bot, c.rangeDial, 15f)) yield return x;   // a new range, so the button is live again
            foreach (var x in bot.Wait(0.5)) yield return x;
            foreach (var x in Press(bot, c.calculateButton)) yield return x;
            bot.Signal("pressed2");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host checked")) yield return x;
            bot.Signal("guestdone");
        }
    }
}
