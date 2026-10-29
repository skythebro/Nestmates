using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>
        /// Checks for three 1.0.1 fixes. A unit icon the host shows or hides on the tactical map does the same on
        /// the guest (twin: --host-extra=-coop-mutant=noreveal). A teleprinter paper the host moves without a drag
        /// (as when the guest missed the drag while paused) catches up on the guest (twin: noscrollsync). The guest's
        /// stopwatch shows what the host's shows.
        /// </summary>
        static IEnumerable<object> Fixes101Host(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(4)) yield return x;

            // a unit icon: flip it on the host
            EntityLocation loc = null;
            foreach (var e in WorldEntities.All()) if (e.Location != null && e.Location.VisualRoot != null) { loc = e.Location; break; }
            bot.Check(loc != null, "the mission has a unit with a map icon");
            if (loc != null)
            {
                bool show = !WorldEntities.IconShown(loc);
                loc.RestoreVisualState(show, WorldEntities.IconPos(loc));
                bot.Check(WorldEntities.IconShown(loc) == show, $"the host's icon for {loc.Entity.ID} is now {(show ? "shown" : "hidden")}");
                bot.Signal("icon", loc.Entity.ID + "^" + (show ? "1" : "0"));
            }

            // the teleprinter paper: moved on the host without a drag
            var scrollers = PrinterAlerts.Scrollers();
            bot.Check(scrollers.Count > 0, "the turret has a teleprinter paper");
            foreach (var x in bot.Until(() => bot.HasSignal("paused"), 30, "guest opened its pause menu")) yield return x;
            if (scrollers.Count > 0)
            {
                var s = scrollers[0];
                float target = s.scrollT < 0.5f ? 0.8f : 0.1f;
                s.RestoreScroll(target);
                foreach (var x in bot.Until(() => Mathf.Abs(s.scrollT - target) < 0.03f, 5, "the host's paper moved", soft: true)) yield return x;
                bot.Signal("scroll", s.scrollT.ToString("R", CultureInfo.InvariantCulture));
            }

            // the stopwatch: what the host's shows now
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Signal("watch", Gunnery.WatchSeconds(0).ToString("R", CultureInfo.InvariantCulture));
            foreach (var x in bot.Until(() => bot.HasSignal("guestdone"), 60, "guest checked")) yield return x;
            bot.Signal("done");
        }

        static IEnumerable<object> Fixes101Guest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "in mission")) yield return x;

            foreach (var x in bot.Until(() => bot.HasSignal("icon"), 90, "host flipped an icon")) yield return x;
            var p = bot.SignalValue("icon").Split('^');
            bool want = p[1] == "1";
            EntityLocation loc = null;
            foreach (var e in WorldEntities.All()) if (e.ID == p[0]) loc = e.Location;
            bot.Check(loc != null, $"the guest has unit {p[0]}");
            if (loc != null)
                foreach (var x in bot.Until(() => WorldEntities.IconShown(loc) == want, 5, $"the guest's icon for {p[0]} is {(want ? "shown" : "hidden")} like the host's")) yield return x;

            // like the report: the guest has its pause menu open while the host scrolls the paper
            var menu = EscMenu();
            menu?.ForceOpen(true);
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Signal("paused");
            foreach (var x in bot.Until(() => bot.HasSignal("scroll"), 30, "host moved its paper")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            menu?.ForceClose(true);
            float hostT = float.Parse(bot.SignalValue("scroll"), CultureInfo.InvariantCulture);
            var scrollers = PrinterAlerts.Scrollers();
            if (scrollers.Count > 0)
            {
                var s = scrollers[0];
                foreach (var x in bot.Until(() => Mathf.Abs(s.scrollT - hostT) < 0.03f, 6, $"the guest's paper catches up with the host's ({hostT:F2})")) yield return x;
            }

            foreach (var x in bot.Until(() => bot.HasSignal("watch"), 30, "host read its stopwatch")) yield return x;
            float hostW = float.Parse(bot.SignalValue("watch"), CultureInfo.InvariantCulture);
            float mine = Gunnery.WatchSeconds(0);
            bot.Check(hostW < 0 || Mathf.Abs(mine - hostW) < 0.3f, $"the guest's stopwatch shows what the host's shows (host {hostW:F2}, guest {mine:F2})");
            bot.Signal("guestdone");
            foreach (var x in bot.Wait(2)) yield return x;
        }
    }
}
