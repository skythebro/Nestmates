using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// Sets the player's Steam status (rich presence) from <see cref="PresenceInfo"/>, for example "Artillery
    /// Introduction · 12 min · at the tactical map · hosting co-op, 2 players".
    ///
    /// Steam shows the "status" text in the friend's game info. The line right under a name in the friends list
    /// needs text the game's developers register with Steam (steam_display tokens), which a mod can't add.
    /// "steam_player_group" puts the players of one co-op game together in their friends' lists.
    /// </summary>
    public static class SteamPresence
    {
        static float _next;
        static string _status, _group, _size;
        static bool _set;

        /// <summary>The status line as last worked out. Used by the tests and shown on the settings tab.</summary>
        public static string Text { get; private set; } = "";

        public static void Update(PresenceInfo info)
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            Text = Settings.SteamStatus ? Describe(info) : "";
            // test copies run under a real Steam account, so they must never set a status
            if (!SteamApi.Available || Sandbox.Active || LaunchArgs.Mutant("nopresence")) return;
            if (!Settings.SteamStatus) { Clear(); return; }
            string group = info.GroupKey ?? "";
            string size = group.Length > 0 ? info.Players.ToString() : "";
            Put("status", Text, ref _status);
            Put("steam_player_group", group, ref _group);
            Put("steam_player_group_size", size, ref _size);
            _set = true;
        }

        /// <summary>Sends only what changed. An empty value removes the key.</summary>
        static void Put(string key, string value, ref string last)
        {
            if (value == last) return;
            last = value;
            if (!SteamApi.SteamAPI_ISteamFriends_SetRichPresence(SteamApi.Friends, key, value))
                CoopLog.Debug($"[presence] Steam refused {key}");
        }

        /// <summary>"Artillery Introduction · 12 min · at the tactical map · hosting co-op, 2 players"</summary>
        public static string Describe(PresenceInfo info)
        {
            string s = info.Headline;
            if (info.Phase == PresenceInfo.Where.Mission)
            {
                double t = info.MissionSeconds;
                if (double.IsFinite(t) && t >= 0) s += t < 60 ? " · just started" : $" · {(int)(t / 60)} min";
                if (info.Station != null) s += " · " + info.Station;
            }
            if (info.CoopPart != null) s += " · " + info.CoopPart;
            return s.Length > 250 ? s.Substring(0, 250) : s;
        }

        public static string Describe() => Describe(PresenceInfo.Now());

        /// <summary>Clears the status when it is turned off or the player leaves the game, so nothing stale stays on the profile.</summary>
        public static void Clear()
        {
            if (!_set || !SteamApi.Available || Sandbox.Active) return;
            SteamApi.SteamAPI_ISteamFriends_ClearRichPresence(SteamApi.Friends);
            _set = false; _status = _group = _size = null;
        }
    }
}
