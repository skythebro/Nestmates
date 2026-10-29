using System;
using IronNestCoop.Core;
using IronNestCoop.QA;
using IronNestCoop.Steam;
using IronNestCoop.UI;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IronNestCoop
{
    /// <summary>
    /// The mod's per-frame driver, living on a DontDestroyOnLoad object created by BepInEx.
    /// Everything that touches the game runs from here on the main thread.
    /// </summary>
    public sealed class CoopRunner : MonoBehaviour
    {
        public CoopRunner(IntPtr ptr) : base(ptr) { }

        public static CoopRunner Instance { get; private set; }

        int _frames;
        float _nextProbe;
        float _nextJoinTry;
        int _joinTries;
        bool _autoStarted;

        void Awake()
        {
            Instance = this;
            Application.runInBackground = true;
            Modules.CreateAll();
            Bot.StartIfRequested();
            CoopLog.Info("CoopRunner active");
        }

        float _nextSteamTry, _nextPresence;
        bool _lobbyArgHandled;

        /// <summary>The game initialises Steam some time after plugins load; keep trying for a minute.</summary>
        void TrySteam()
        {
            if (SteamApi.Available || Time.unscaledTime < _nextSteamTry || Time.unscaledTime > 90f) return;
            _nextSteamTry = Time.unscaledTime + 2f;
            SteamApi.Init();
            if (SteamApi.Available) Steam.SteamJoinRequests.Register();
            if (SteamApi.Available && !_lobbyArgHandled && LaunchArgs.ConnectLobby is ulong lobby)
            {
                _lobbyArgHandled = true;
                Coop.JoinSteam(lobby);
            }
        }

        void Update()
        {
            _frames++;
            try
            {
                if (Sandbox.Active) Sandbox.EnforceWindow();
                TrySteam();
                AutoStartFromArgs();
                Coop.Tick(Time.unscaledDeltaTime);
                Bot.Instance?.Step();
                Dev.DevTools.Update();
                DiscordPresence.TakeJoin();   // Handle a join request from a Discord friend (the pipe thread only records it)
                long ts = Perf.Start(); UI.SkipIntro.Update(); Perf.Stop("skip intro", ts);
                ts = Perf.Start(); UI.GameSettingsTab.Update(); Perf.Stop("settings tab", ts);
                ts = Perf.Start();
                if (Time.unscaledTime >= _nextPresence)
                {
                    _nextPresence = Time.unscaledTime + 2f;
                    var presence = PresenceInfo.Now();
                    SteamPresence.Update(presence);
                    DiscordPresence.Update(presence);
                }
                Perf.Stop("steam + discord status", ts);
                if (Time.unscaledTime >= _nextProbe)
                {
                    _nextProbe = Time.unscaledTime + 30f;
                    Probe();
                }
            }
            catch (Exception e) { CoopLog.ErrorOnce("update:" + e.Message, $"Update: {e}"); }
        }

        void OnGUI()
        {
            if (QA.UiProbe.Enabled) { QA.UiProbe.OnGUI(); return; }
            long tg = Perf.Start();
            try { CoopUI.Draw(); Dev.DevTools.OnGUI(); }
            catch (Exception e) { CoopLog.ErrorOnce("ongui:" + e.Message, $"OnGUI: {e}"); }
            Perf.Stop("window (" + (Event.current?.type.ToString() ?? "?") + ")", tg);
        }

        void OnApplicationQuit()
        {
            Coop.Leave("game closed");
            SteamPresence.Clear();
            DiscordPresence.Shutdown();
        }

        /// <summary>Tests only: a guest that left stays away until this time, so the auto-join waits.</summary>
        internal static float HoldJoinUntil;

        /// <summary>Handles -coop-host and -coop-join for tests and LAN play. The guest retries until the host is up.</summary>
        void AutoStartFromArgs()
        {
            if (LaunchArgs.TcpHostPort is int port && !_autoStarted)
            {
                _autoStarted = true;
                Coop.HostTcp(port, LaunchArgs.TcpKey);
            }
            if (LaunchArgs.TcpJoin != null && Coop.Session == null && !Coop.Connecting && _joinTries < 30 && Time.unscaledTime >= _nextJoinTry && Time.unscaledTime >= LaunchArgs.JoinDelay && Time.unscaledTime >= HoldJoinUntil)
            {
                _joinTries++;
                _nextJoinTry = Time.unscaledTime + 2f;
                Coop.JoinTcp(LaunchArgs.TcpJoin, LaunchArgs.TcpKey);
            }
        }

        void Probe()
        {
            var mm = MissionManager.Instance;
            string phase = mm != null ? mm._CurrentPhase_k__BackingField.ToString() : "no MissionManager";
            string scenes = "";
            for (int i = 0; i < SceneManager.sceneCount; i++) scenes += (i > 0 ? "," : "") + SceneManager.GetSceneAt(i).name;
            var turret = TurretController.Instance;
            string tur = turret != null ? $"{turret.CurrentAngle:F1}" : "none";
            var s = Coop.Session;
            string net = s == null ? "offline" : $"{s.Role} {s.State} players={s.PlayerCount} sent={s.BytesSent} recv={s.BytesReceived}";
            CoopLog.Info($"probe frame={_frames} fps={1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f):F0} phase={phase} scenes=[{scenes}] turret={tur} missionTime={MissionClock.TimeAsDouble:F1} net={net}");
        }
    }
}
