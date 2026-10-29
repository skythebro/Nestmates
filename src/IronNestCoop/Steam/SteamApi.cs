using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// Minimal Steamworks flat-API bindings against the steam_api64.dll the game already loaded
    /// (the game initialises Steam; we never call SteamAPI_Init). The struct layouts below are for x64:
    /// SteamNetworkingMessage_t has data at offset 0, size at 8 and the sender identity at 16.
    /// </summary>
    public static unsafe class SteamApi
    {
        const string Dll = "steam_api64";

        [StructLayout(LayoutKind.Sequential, Size = 136)]
        public struct Identity { fixed byte _b[136]; }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct FriendGameInfo
        {
            public ulong GameId;
            public uint GameIp;
            public ushort GamePort;
            public ushort QueryPort;
            public ulong LobbyId;
        }

        public const int SendUnreliable = 0, SendNoNagle = 1, SendReliable = 8, SendAutoRestartBrokenSession = 32;

        static bool _resolverSet;
        public static bool Available { get; private set; }
        public static string Error { get; private set; }
        public static IntPtr User, Friends, Matchmaking, Utils, NetMessages, NetUtils;

        public static void Init()
        {
            if (Available) return;
            try
            {
                if (!_resolverSet)
                {
                    NativeLibrary.SetDllImportResolver(typeof(SteamApi).Assembly, Resolve);
                    _resolverSet = true;
                }
                User = SteamAPI_SteamUser_v023();
                Friends = SteamAPI_SteamFriends_v018();
                Matchmaking = SteamAPI_SteamMatchmaking_v009();
                Utils = SteamAPI_SteamUtils_v010();
                NetMessages = SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
                NetUtils = SteamAPI_SteamNetworkingUtils_SteamAPI_v004();
                if (User == IntPtr.Zero || Matchmaking == IntPtr.Zero || NetMessages == IntPtr.Zero)
                    throw new InvalidOperationException("Steam interfaces unavailable (is Steam running and the game started from Steam?)");
                if (!SteamAPI_ISteamUser_BLoggedOn(User)) throw new InvalidOperationException("not logged on to Steam");
                // Snapshots can be large, so raise the buffer sizes.
                const int SendBufferSize = 9, RecvBufferSize = 47, RecvMaxMessageSize = 49; // these are ESteamNetworkingConfigValue ids
                SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(NetUtils, SendBufferSize, 32 << 20);
                SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(NetUtils, RecvBufferSize, 32 << 20);
                SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(NetUtils, RecvMaxMessageSize, 1 << 20);   // our largest message is 512 KB
                SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(NetUtils);
                Available = true;
                CoopLog.Info($"Steam ready: {PersonaName()} ({LocalId}), app {SteamAPI_ISteamUtils_GetAppID(Utils)}");
            }
            catch (Exception e)
            {
                if (Error != e.Message) CoopLog.Warn("Steam not ready yet: " + e.Message);
                Error = e.Message;
            }
        }

        static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
        {
            if (name != Dll) return IntPtr.Zero;
            var h = GetModuleHandleW("steam_api64.dll"); // normally the game has already loaded it
            if (h != IntPtr.Zero) return h;
            var p = System.IO.Path.Combine(UnityEngine.Application.dataPath, "Plugins", "x86_64", "steam_api64.dll");
            return System.IO.File.Exists(p) && NativeLibrary.TryLoad(p, out h) ? h : IntPtr.Zero;
        }

        [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

        public static ulong LocalId => Available ? SteamAPI_ISteamUser_GetSteamID(User) : 0;

        public static string PersonaName()
        {
            try { return Available ? Utf8(SteamAPI_ISteamFriends_GetPersonaName(Friends)) : null; }
            catch { return null; }
        }

        public static string FriendName(ulong id) => Available ? Utf8(SteamAPI_ISteamFriends_GetFriendPersonaName(Friends, id)) : id.ToString();

        public static string Utf8(IntPtr p)
        {
            if (p == IntPtr.Zero) return null;
            int n = 0;
            while (((byte*)p)[n] != 0) n++;
            return Encoding.UTF8.GetString((byte*)p, n);
        }

        public static Identity IdentityOf(ulong steamId)
        {
            var id = new Identity();
            SteamAPI_SteamNetworkingIdentity_SetSteamID64(ref id, steamId);
            return id;
        }

        // ---------------------------------------------------------------- exports
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_SteamUser_v023();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_SteamFriends_v018();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_SteamMatchmaking_v009();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_SteamUtils_v010();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_SteamNetworkingMessages_SteamAPI_v002();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_SteamNetworkingUtils_SteamAPI_v004();

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUser_BLoggedOn(IntPtr self);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SteamAPI_ISteamFriends_GetFriendPersonaState(IntPtr self, ulong id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong id, out FriendGameInfo info);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SteamAPI_ISteamFriends_ClearRichPresence(IntPtr self);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog(IntPtr self, ulong lobby);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_ISteamMatchmaking_CreateLobby(IntPtr self, int type, int maxMembers);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_ISteamMatchmaking_JoinLobby(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SteamAPI_ISteamMatchmaking_LeaveLobby(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamMatchmaking_SetLobbyData(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string key, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr SteamAPI_ISteamMatchmaking_GetLobbyData(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyOwner(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SteamAPI_ISteamMatchmaking_GetNumLobbyMembers(IntPtr self, ulong lobby);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex(IntPtr self, ulong lobby, int index);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamMatchmaking_SetLobbyJoinable(IntPtr self, ulong lobby, [MarshalAs(UnmanagedType.I1)] bool joinable);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamMatchmaking_InviteUserToLobby(IntPtr self, ulong lobby, ulong user);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_IsAPICallCompleted(IntPtr self, ulong call, [MarshalAs(UnmanagedType.I1)] out bool failed);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamUtils_GetAPICallResult(IntPtr self, ulong call, void* buf, int size, int expectedCallback, [MarshalAs(UnmanagedType.I1)] out bool failed);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern uint SteamAPI_ISteamUtils_GetAppID(IntPtr self);

        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SteamAPI_ISteamNetworkingMessages_SendMessageToUser(IntPtr self, ref Identity to, void* data, uint size, int flags, int channel);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern int SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(IntPtr self, int channel, IntPtr* messages, int max);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(IntPtr self, ref Identity remote);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(IntPtr self, ref Identity remote);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SteamAPI_SteamNetworkingMessage_t_Release(IntPtr msg);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SteamAPI_SteamNetworkingIdentity_SetSteamID64(ref Identity id, ulong steamId);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern ulong SteamAPI_SteamNetworkingIdentity_GetSteamID64(IntPtr id);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] public static extern bool SteamAPI_ISteamNetworkingUtils_SetGlobalConfigValueInt32(IntPtr self, int key, int value);
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] public static extern void SteamAPI_ISteamNetworkingUtils_InitRelayNetworkAccess(IntPtr self);
    }
}
