using System;
using IronNestCoop.Core;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// What this player is doing, worked out once for every place that shows it (Steam, Discord): the main menu,
    /// the operations map or a mission (its title and since when), where in the turret (only if the player
    /// turned that on), and the co-op game they are in.
    /// </summary>
    public sealed class PresenceInfo
    {
        public enum Where { Starting, Menu, Map, Mission }

        public Where Phase;
        public string MissionTitle;      // the mission name in this player's language, or its id if there is no translation
        public double MissionSeconds;    // how long the mission has run, from the shared mission clock
        public string Station;           // for example "at the tactical map"; null if the player did not want it shown or is not in a mission
        public bool InCoop, IsHost;
        public int Players;
        public ulong LobbyId;            // Steam lobby id; 0 means none, or a direct connection

        /// <summary>A hash of the lobby id that groups the players of one game for Steam and Discord. The lobby id
        /// itself is not published, because with it anyone could ask to join an invite-only lobby.</summary>
        public string GroupKey
        {
            get
            {
                if (LobbyId == 0) return null;
                var h = System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(LobbyId));
                return "nm" + Convert.ToHexString(h, 0, 10).ToLowerInvariant();
            }
        }

        /// <summary>Returns what the player is doing: "In the main menu", "Choosing a mission on the operations map", or the mission's title.</summary>
        public string Headline => Phase switch
        {
            Where.Mission => MissionTitle ?? "In a mission",
            Where.Map => "Choosing a mission on the operations map",
            Where.Menu => "In the main menu",
            _ => "Starting the game",
        };

        /// <summary>Returns the co-op part of the status, such as "hosting co-op, 2 players" or "in co-op, 3 players". Null when playing alone.</summary>
        public string CoopPart => !InCoop ? null
            : IsHost ? $"hosting co-op, {Players} player{(Players == 1 ? "" : "s")}" : $"in co-op, {Players} players";

        static double _startedAt = double.NaN;   // the mission start as a Unix time, kept steady while the mission runs

        /// <summary>Returns when the mission started (Unix seconds) for a live "elapsed" clock, or 0 when not in a mission.</summary>
        public long MissionStartUnix
        {
            get
            {
                if (Phase != Where.Mission || !double.IsFinite(MissionSeconds) || MissionSeconds < 0) { _startedAt = double.NaN; return 0; }
                double start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - MissionSeconds;
                // The mission clock pauses (shared pause) and is corrected now and then. Only follow it when it is
                // clearly off, so a friend's elapsed time does not jitter.
                if (double.IsNaN(_startedAt) || Math.Abs(start - _startedAt) > 5) _startedAt = start;
                return (long)_startedAt;
            }
        }

        public static PresenceInfo Now()
        {
            var p = new PresenceInfo();
            var mm = MissionManager.Instance;
            if (mm == null) p.Phase = Where.Starting;
            else
            {
                var phase = mm._CurrentPhase_k__BackingField;
                if (phase == MissionManager.GamePhase.MissionActive)
                {
                    p.Phase = Where.Mission;
                    p.MissionTitle = MissionName(mm._CurrentMission_k__BackingField);
                    p.MissionSeconds = MissionClock.TimeAsDouble;
                    if (Settings.StatusStation)
                    {
                        var zone = CameraZoneTrigger.s_currentActiveZone;
                        string label = zone != null && zone.consoleActive ? Sync.Avatars.ConsoleLabel(Util.ScenePath.Of(zone.transform)) : null;
                        p.Station = label ?? "walking around the turret";
                    }
                }
                else p.Phase = phase == MissionManager.GamePhase.BrowsingMap ? Where.Map : Where.Menu;
            }
            if (Coop.InSession)
            {
                p.InCoop = true;
                p.IsHost = Coop.IsHost;
                p.Players = Coop.Session.PlayerCount;
                var lobby = Coop.Lobby;
                p.LobbyId = lobby != null ? lobby.Id : 0;
            }
            return p;
        }

        static string MissionName(SleepyNodes.MissionGraph m)
        {
            if (m == null) return null;
            try
            {
                if (m.MissionName != null && m.MissionName.TryGet(out var text) && !string.IsNullOrWhiteSpace(text)) return Clean(text);
            }
            catch (Exception e) { CoopLog.ErrorOnce("presence-name", "[presence] mission name: " + e.Message); }
            return string.IsNullOrEmpty(m.MissionID) ? null : m.MissionID;
        }

        /// <summary>Strips rich text tags and line breaks, because a status is one plain line.</summary>
        static string Clean(string s)
        {
            s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", "");
            return System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        }
    }
}
