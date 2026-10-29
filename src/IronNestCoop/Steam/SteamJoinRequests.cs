using System;
using IronNestCoop.Core;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// Handles "Join game" from the Steam friends list and accepted lobby invites while the game is running. Both
    /// arrive as GameLobbyJoinRequested_t (we set no "connect" rich presence, so Steam uses the lobby). The game's
    /// Steamworks.NET dispatcher owns the callback queue, so the callback is registered with it. A launch with
    /// +connect_lobby is handled by LaunchArgs instead.
    /// </summary>
    static class SteamJoinRequests
    {
        static Steamworks.Callback<Steamworks.GameLobbyJoinRequested_t> _invite;
        static Action<Steamworks.GameLobbyJoinRequested_t> _onInvite;

        public static void Register()
        {
            if (_invite != null) return;
            try
            {
                _onInvite = e => Accept(e.m_steamIDLobby.m_SteamID, "Steam invite");
                _invite = Steamworks.Callback<Steamworks.GameLobbyJoinRequested_t>.Create(_onInvite);
            }
            catch (Exception e) { CoopLog.Warn("Steam invite hookup: " + e.Message); }
            CoopLog.Info($"Steam join requests (invites, friends-list join): {(_invite != null ? "on" : "OFF")}");
        }

        static void Accept(ulong lobby, string via)
        {
            try
            {
                if (lobby == 0) { CoopLog.Warn($"join request from {via} without a lobby"); return; }
                if (Coop.Lobby != null && Coop.Lobby.Id == lobby) return;   // already there
                CoopLog.Info($"joining lobby {lobby} ({via})");
                if (Coop.InSession || Coop.Lobby != null) Coop.Leave();
                Coop.JoinSteam(lobby);
                UI.CoopUI.Show(0);
            }
            catch (Exception e) { CoopLog.Warn($"join from {via} failed: {e.Message}"); }
        }
    }
}
