using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>Everything the host-only mission logic produces that players can see, as one comparable string.</summary>
        static string WorldEventsState(out string detail)
        {
            var sb = new StringBuilder();
            foreach (Teleprinter.Teleprinters t in Enum.GetValues(typeof(Teleprinter.Teleprinters)))
            {
                var tp = Teleprinter.GetTeleprinter(t);
                if (tp == null) { sb.Append($"tp{t}=none;"); continue; }
                var d = tp.CaptureMissionState();
                var text = (d.CurrentFullRich ?? "");
                int pending = d.PendingJobs?.Count ?? 0;
                sb.Append($"tp{t}={text.Length}:{Hash(text)}+{pending};");
            }
            var rp = MissionStatsTracker.Instance?.RequisitionPoints ?? -1;
            var ch = PowderChargeInventory.Instance?.CurrentCharges ?? -1;
            sb.Append($"rp={rp};ch={ch};");
            var cb = CounterBatteryTimer.Instance;
            if (cb != null) sb.Append($"cb={(cb._running ? "run" : "stop")}:{Mathf.RoundToInt(cb._remainingSeconds)};");
            var tu = TurretController.Instance;
            if (tu != null) { var p = tu.turretBase.position; sb.Append($"turret={p.x:F1},{p.y:F1}"); }
            detail = sb.ToString();
            return Hash(detail);
        }

        static string Hash(string s)
        {
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s)))[..10];
        }

        /// <summary>Waits until the teleprinters have no pending jobs (text fully queued) on this machine.</summary>
        static bool TeleprintersIdle()
        {
            foreach (Teleprinter.Teleprinters t in Enum.GetValues(typeof(Teleprinter.Teleprinters)))
            {
                var tp = Teleprinter.GetTeleprinter(t);
                if (tp == null) continue;
                var d = tp.CaptureMissionState();
                if ((d.PendingJobs?.Count ?? 0) > 0 || d.CurrentJob != null && !d.CurrentJob.Complete) return false;
            }
            return true;
        }

        static IEnumerable<object> WorldHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(10)) yield return x;   // let the mission print its opening orders

            MissionStatsTracker.Instance?.AddRequisitionPoints(55, "qa");
            PowderChargeInventory.Instance?.AddCharges(3);
            var cb = CounterBatteryTimer.Instance;
            if (cb != null) { cb.Init(120); cb.StartTimer(); }
            var tu = TurretController.Instance;
            if (tu != null) tu.SetTurretLocation(tu.turretBase.position + new Vector3(0.6f, 0.4f, 0));
            bot.Note("host changed economy, timer and turret location");

            // The mission keeps printing on its own schedule, so publish the state each second while idle; the guest
            // compares against the latest one until both machines agree (a single snapshot races new orders).
            string last = null; double nextPub = 0;
            foreach (var x in bot.Until(() =>
            {
                if (bot.HasSignal("verdict")) return true;
                if (Time.unscaledTime < nextPub || !TeleprintersIdle()) return false;
                nextPub = Time.unscaledTime + 1;
                var st = WorldEventsState(out var detail);
                if (st != last) { bot.Note("host state: " + detail); last = st; }
                bot.Signal("state", st);
                return false;
            }, 240, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> WorldGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "followed host into mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("state"), 240, "host state")) yield return x;
            string mine = null, detail = null;
            foreach (var x in bot.Until(() => TeleprintersIdle() && (mine = WorldEventsState(out detail)) == bot.SignalValue("state"), 180, "guest state equals the host's", soft: true)) yield return x;
            bot.Note("guest state: " + detail);
            bot.Check(mine == bot.SignalValue("state"), $"teleprinters, economy, timer and turret match the host ({mine} vs {bot.SignalValue("state")})");
            bot.Check(WorldEvents.Instance.Applied > 0, $"guest applied host events ({WorldEvents.Instance.Applied})");
            bot.Check(GraphGate.Skipped > 0, "guest mission logic suppressed (mode check)");
            foreach (var x in bot.Shot("guest-world")) yield return x;
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
