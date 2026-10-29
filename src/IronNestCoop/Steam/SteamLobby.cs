using System;
using System.Collections.Generic;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// A Steam lobby used only for discovery and membership. Friends see "Join game" on the host, invites work
    /// through the overlay or our own friend list, and lobby members are the only peers the transport accepts.
    ///
    /// About call results: the game runs Steamworks.NET in manual-dispatch mode, so its dispatcher takes every
    /// finished call result off the queue, and ISteamUtils.GetAPICallResult then fails with "Steam call failed".
    /// So each call is registered with the game's dispatcher (Steamworks.CallResult), which hands the result to us.
    /// Polling stays as a fallback for builds without manual dispatch.
    /// </summary>
    public sealed unsafe class SteamLobby
    {
        public enum Visibility { InviteOnly = 0, FriendsOnly = 1, Public = 2 }

        public const string KeyMod = "inc_mod", ModTag = "Nestmates", KeyVersion = "inc_ver", KeyHost = "inc_host", KeyState = "inc_state";

        const int LobbyCreatedId = 513, LobbyEnterId = 504;

        ulong _call;
        Action<bool> _pending;
        // Keep these referenced while a call is pending: the dispatcher holds the Il2Cpp side, and we hold the managed delegates.
        Steamworks.CallResult<Steamworks.LobbyCreated_t> _created;
        Steamworks.CallResult<Steamworks.LobbyEnter_t> _entered;
        Action<Steamworks.LobbyCreated_t, bool> _onCreated;
        Action<Steamworks.LobbyEnter_t, bool> _onEntered;
        int _expected;
        double _deadline;
        readonly List<ulong> _members = new();
        float _nextMemberPoll;

        public ulong Id { get; private set; }
        public bool IsOwner { get; private set; }
        public string Error { get; private set; }
        public string ModVersion { get; private set; }
        public ulong Owner => Id != 0 ? SteamApi.SteamAPI_ISteamMatchmaking_GetLobbyOwner(SteamApi.Matchmaking, Id) : 0;

        /// <summary>Returns a short code friends can type: the low 32 bits of the lobby id in hex.</summary>
        public string Code => Id == 0 ? "" : (Id & 0xFFFFFFFF).ToString("X8");

        public static ulong FromCode(string code)
        {
            code = code.Trim().Replace(" ", "").Replace("-", "");
            if (code.Length > 8 && ulong.TryParse(code, out var full)) return full;
            if (!uint.TryParse(code, System.Globalization.NumberStyles.HexNumber, null, out var low)) return 0;
            return 0x0186000000000000UL | low; // the universe and type prefix of a public chat-room lobby id
        }

        public IEnumerable<ulong> MemberIds()
        {
            return _members;
        }

        public void Create(Visibility vis, int max, string version, Action<bool> done)
        {
            _call = SteamApi.SteamAPI_ISteamMatchmaking_CreateLobby(SteamApi.Matchmaking, (int)vis, max);
            if (_call != 0 && !LaunchArgs.Mutant("nosteamcallresult"))
                try
                {
                    _onCreated = (r, ioFailure) => Finish(ioFailure, () => Created((int)r.m_eResult, r.m_ulSteamIDLobby));
                    _created = Steamworks.CallResult<Steamworks.LobbyCreated_t>.Create(_onCreated);
                    _created.Set(new Steamworks.SteamAPICall_t(_call), _onCreated);
                }
                catch (Exception e) { CoopLog.Warn("Steam call result hookup (create): " + e.Message); }
            Await(LobbyCreatedId, ok =>
            {
                if (ok)
                {
                    IsOwner = true;
                    ModVersion = version;
                    var mm = SteamApi.Matchmaking;
                    SteamApi.SteamAPI_ISteamMatchmaking_SetLobbyData(mm, Id, KeyMod, ModTag);
                    SteamApi.SteamAPI_ISteamMatchmaking_SetLobbyData(mm, Id, KeyVersion, version);
                    SteamApi.SteamAPI_ISteamMatchmaking_SetLobbyData(mm, Id, KeyHost, SteamApi.PersonaName() ?? "");
                    // We set no "connect" key, so the friends list's "Join game" uses the lobby itself (an in-game join
                    // request, or a launch with +connect_lobby). That works while the game is running too.
                    CoopLog.Info($"Steam lobby {Id} created (code {Code}, {vis})");
                }
                done(ok);
            }, "creating lobby");
        }

        public void Join(ulong lobby, Action<bool> done)
        {
            _call = SteamApi.SteamAPI_ISteamMatchmaking_JoinLobby(SteamApi.Matchmaking, lobby);
            if (_call != 0)
                try
                {
                    _onEntered = (r, ioFailure) => Finish(ioFailure, () => Entered(r.m_ulSteamIDLobby, r.m_EChatRoomEnterResponse));
                    _entered = Steamworks.CallResult<Steamworks.LobbyEnter_t>.Create(_onEntered);
                    _entered.Set(new Steamworks.SteamAPICall_t(_call), _onEntered);
                }
                catch (Exception e) { CoopLog.Warn("Steam call result hookup (join): " + e.Message); }
            Await(LobbyEnterId, ok =>
            {
                if (ok)
                {
                    string tag = Data(KeyMod);
                    if (tag != ModTag) { Error = "that lobby is not a Nestmates game"; Leave(); ok = false; }
                    else
                    {
                        ModVersion = Data(KeyVersion);
                        CoopLog.Info($"joined Steam lobby {Id}, host {SteamApi.FriendName(Owner)} runs {ModVersion}");
                    }
                }
                done(ok);
            }, "joining lobby");
        }

        public string Data(string key) => Id == 0 ? null : SteamApi.Utf8(SteamApi.SteamAPI_ISteamMatchmaking_GetLobbyData(SteamApi.Matchmaking, Id, key));

        public void SetData(string key, string value)
        {
            if (Id != 0 && IsOwner) SteamApi.SteamAPI_ISteamMatchmaking_SetLobbyData(SteamApi.Matchmaking, Id, key, value);
        }

        public void SetJoinable(bool joinable)
        {
            if (Id != 0 && IsOwner) SteamApi.SteamAPI_ISteamMatchmaking_SetLobbyJoinable(SteamApi.Matchmaking, Id, joinable);
        }

        public void OpenInviteOverlay()
        {
            if (Id != 0) SteamApi.SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(SteamApi.Friends, Id);
        }

        public bool Invite(ulong friend) => Id != 0 && SteamApi.SteamAPI_ISteamMatchmaking_InviteUserToLobby(SteamApi.Matchmaking, Id, friend);

        public void Leave()
        {
            if (Id == 0) return;
            SteamApi.SteamAPI_ISteamMatchmaking_LeaveLobby(SteamApi.Matchmaking, Id);
            CoopLog.Info($"left Steam lobby {Id}");   // the Steam status updates on its own (see SteamPresence)
            Id = 0;
            _members.Clear();
        }

        void Await(int callbackId, Action<bool> done, string what)
        {
            if (_call == 0) { Error = "Steam refused the request"; done(false); return; }
            _expected = callbackId;
            _pending = done;
            _deadline = UnityEngine.Time.realtimeSinceStartupAsDouble + 20;
            CoopLog.Debug($"steam call {_call} pending ({what})");
        }

        public void Tick()
        {
            if (_pending != null) PollCall();
            if (Id != 0 && UnityEngine.Time.unscaledTime >= _nextMemberPoll)
            {
                _nextMemberPoll = UnityEngine.Time.unscaledTime + 0.5f;
                _members.Clear();
                int n = SteamApi.SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(SteamApi.Matchmaking, Id);
                for (int i = 0; i < n; i++) _members.Add(SteamApi.SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(SteamApi.Matchmaking, Id, i));
            }
        }

        void PollCall()
        {
            var done = _pending;
            if (UnityEngine.Time.realtimeSinceStartupAsDouble > _deadline)
            {
                _pending = null; Error = "Steam did not answer in time"; done(false); return;
            }
            // Fallback for when the game does not dispatch call results itself. Under manual dispatch the result is
            // already gone (GetAPICallResult fails), so keep waiting for the dispatcher to deliver it instead.
            if (!SteamApi.SteamAPI_ISteamUtils_IsAPICallCompleted(SteamApi.Utils, _call, out bool failed)) return;
            byte* buf = stackalloc byte[64];
            if (failed || !SteamApi.SteamAPI_ISteamUtils_GetAPICallResult(SteamApi.Utils, _call, buf, _expected == LobbyCreatedId ? 16 : 24, _expected, out failed) || failed)
                return;
            if (_expected == LobbyCreatedId) Finish(false, () => Created(*(int*)buf, *(ulong*)(buf + 8)));
            else Finish(false, () => Entered(*(ulong*)buf, *(uint*)(buf + 16)));
        }

        /// <summary>Accepts one result per call, from whichever path delivers first.</summary>
        void Finish(bool ioFailure, Func<bool> apply)
        {
            var done = _pending;
            if (done == null) return;
            _pending = null;
            _created = null; _entered = null;
            if (ioFailure) { Error = "Steam call failed (network)"; done(false); return; }
            if (!apply()) { done(false); return; }
            _nextMemberPoll = 0;
            Tick();
            done(true);
        }

        bool Created(int result, ulong id)
        {
            if (result != 1 || id == 0) { Error = $"Steam could not create a lobby (EResult {result})"; return false; }
            Id = id;
            return true;
        }

        bool Entered(ulong id, uint response)
        {
            if (response != 1) { Error = response switch { 2 => "lobby does not exist (wrong code, or the host left)", 3 => "not allowed to join", 4 => "lobby is full", 7 => "you are banned from this lobby", 8 => "limited Steam account", _ => $"Steam refused (code {response})" }; return false; }
            Id = id;
            return true;
        }

        /// <summary>Lists friends currently in a Nestmates lobby of this game, for a one-click join list.</summary>
        public static List<(ulong friend, string name, ulong lobby)> FriendsHosting()
        {
            var list = new List<(ulong, string, ulong)>();
            if (!SteamApi.Available) return list;
            uint app = SteamApi.SteamAPI_ISteamUtils_GetAppID(SteamApi.Utils);
            int n = SteamApi.SteamAPI_ISteamFriends_GetFriendCount(SteamApi.Friends, 4);
            for (int i = 0; i < n; i++)
            {
                ulong f = SteamApi.SteamAPI_ISteamFriends_GetFriendByIndex(SteamApi.Friends, i, 4);
                if (!SteamApi.SteamAPI_ISteamFriends_GetFriendGamePlayed(SteamApi.Friends, f, out var gi)) continue;
                if ((gi.GameId & 0xFFFFFF) != app || gi.LobbyId == 0) continue;
                list.Add((f, SteamApi.FriendName(f), gi.LobbyId));
            }
            return list;
        }

        /// <summary>Lists online friends for the in-game invite list. This works even when the Steam overlay does not open.</summary>
        public static List<(ulong friend, string name)> OnlineFriends()
        {
            var list = new List<(ulong, string)>();
            if (!SteamApi.Available) return list;
            int n = SteamApi.SteamAPI_ISteamFriends_GetFriendCount(SteamApi.Friends, 4);
            for (int i = 0; i < n; i++)
            {
                ulong f = SteamApi.SteamAPI_ISteamFriends_GetFriendByIndex(SteamApi.Friends, i, 4);
                if (SteamApi.SteamAPI_ISteamFriends_GetFriendPersonaState(SteamApi.Friends, f) != 0) list.Add((f, SteamApi.FriendName(f)));
            }
            list.Sort((a, b) => string.Compare(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase));
            return list;
        }
    }
}
