using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static PauseManager Pauser => Object.FindObjectOfType<PauseManager>();

        /// <summary>Open the pause menu the way the pause key does, hold it, report what time did meanwhile.</summary>
        static IEnumerable<object> PauseFor(Bot bot, double seconds, string who)
        {
            var pm = Pauser;
            bot.Check(pm != null, $"{who}: the game's pause manager exists");
            if (pm == null) yield break;
            double t0 = MissionClock.TimeAsDouble;
            float step = Time.fixedDeltaTime;
            pm.TogglePause();
            bot.Note($"{who}: paused (game says paused {PauseManager.IsPaused}), time scale {Time.timeScale}, physics step {Time.fixedDeltaTime} (was {step})");
            int f0 = Time.frameCount; float r0 = Time.realtimeSinceStartup;
            foreach (var x in bot.Wait(seconds)) yield return x;
            double advanced = MissionClock.TimeAsDouble - t0;
            float fps = (Time.frameCount - f0) / Mathf.Max(0.01f, Time.realtimeSinceStartup - r0);
            bot.Check(Mathf.Abs(Time.fixedDeltaTime - step) < 1e-6f, $"{who}: the physics step is unchanged while the menu is open ({Time.fixedDeltaTime} vs {step})");
            bot.Check(fps > 15, $"{who}: frame rate stays normal while the menu is open ({fps:F0} fps; test copies are capped at 30)");
            bot.Check(Time.timeScale == 1f, $"{who}: time scale stays 1 while the menu is open ({Time.timeScale})");
            bot.Check(advanced > seconds * 0.8, $"{who}: the mission clock kept running ({advanced:F1} s in {seconds} s)");
            pm.TogglePause();
            foreach (var x in bot.Wait(0.5)) yield return x;
        }

        static IEnumerable<object> PauseHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            foreach (var x in PauseFor(bot, 4, "host")) yield return x;
            bot.Signal("hostdone", MissionClock.TimeAsDouble.ToString("R"));
            foreach (var x in bot.Until(() => bot.HasSignal("guestpaused"), 60, "guest paused and resumed")) yield return x;
            bot.Signal("clock", MissionClock.TimeAsDouble.ToString("R"));
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> PauseGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("hostdone"), 180, "host paused and resumed")) yield return x;
            foreach (var x in PauseFor(bot, 4, "guest")) yield return x;
            bot.Signal("guestpaused");
            foreach (var x in bot.Until(() => bot.HasSignal("clock"), 30, "host clock")) yield return x;
            double host = double.Parse(bot.SignalValue("clock"));
            double err = MissionClock.TimeAsDouble - host - (Coop.Session.GetPlayer(0)?.RttMs ?? 0) / 2000.0;
            bot.Check(System.Math.Abs(err) < 0.25, $"mission clocks still agree after both paused ({err * 1000:F0} ms)");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
