using System;
using System.Collections.Generic;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Steam;

namespace IronNestCoop.Core
{
    /// <summary>Every game message type. Never renumber existing values; only append new ones.
    /// Bump Protocol.Version whenever a message layout changes.</summary>
    public enum Msg : ushort
    {
        BotSignal = 1,
        ClockSync = 10,
        MissionStart = 20,
        MissionTransition = 21,
        MissionSnapshot = 22,
        WorldReady = 23,
        AvatarState = 30,
        ControlValue = 40,
        ControlClick = 41,
        ControlLease = 42,
        EntitySpawn = 50,
        EntityMove = 51,
        EntityState = 52,
        EntityDigest = 53,
        EntityResync = 54,
        ShotFired = 60,
        ImpactVisual = 61,
        TurretState = 62,
        ReloadState = 63,
        Teleprinter = 70,
        Notification = 71,
        MapMarker = 80,
        MapMarkerRemove = 81,
        Card = 82,
        Notepad = 83,
        Requisition = 90,
        Economy = 91,
        Music = 100,
        CatState = 110,
        Settings = 120,
        Machinery = 130,
        Unlocks = 131,
        Toggles = 132,
        Espresso = 133,
        ControlHover = 134,
        Animators = 135,
        Visibility = 136,
        Paint = 137,
        DeckOrder = 138,
        MenuState = 139,
        PrinterAlerts = 140,
        EntityReveal = 141,   // added in 1.0.1: which units the map shows, and where
        TeleprinterScroll = 142,   // added in 1.0.1: how far each teleprinter's paper is scrolled
    }

    /// <summary>One feature's sync logic. Modules are created once and live for the whole game.</summary>
    public abstract class SyncModule
    {
        public abstract string Name { get; }
        /// <summary>Register message handlers on a fresh session.</summary>
        public virtual void Register(Session s) { }
        public virtual void OnSessionStart() { }
        public virtual void OnSessionEnd() { }
        public virtual void OnPlayerJoined(Player p) { }
        public virtual void OnPlayerLeft(Player p) { }
        /// <summary>Host only: sends everything this module owns to one guest whose mission just loaded.</summary>
        public virtual void SendSnapshot(byte slot) { }
        /// <summary>Every frame while a session is connected.</summary>
        public virtual void Tick(float dt) { }
        /// <summary>Returns a short status line for the debug panel.</summary>
        public virtual string Status => null;
    }

    /// <summary>Static entry point that holds the current session, answers role checks for patches, and wires up the modules.</summary>
    public static class Coop
    {
        public static Session Session { get; private set; }
        public static SteamLobby Lobby { get; private set; }
        static readonly List<SyncModule> Modules = new();
        static readonly NetWriter Scratch = new(1024);

        public static bool InSession => Session != null && Session.IsConnected;
        public static bool IsHost => InSession && Session.IsHost;
        public static bool IsGuest => InSession && !Session.IsHost;
        /// <summary>True on the host, or when not in a session at all. This machine decides game outcomes.</summary>
        public static bool IsAuthority => !InSession || Session.IsHost;

        /// <summary>
        /// True while a module applies state that came from the network. Patches that capture
        /// local changes must ignore calls made while this is set, or every applied value echoes back.
        /// </summary>
        public static bool Applying { get; internal set; }

        /// <summary>
        /// The host broadcasts the results of everything that happens in its world. On the host, Applying means
        /// it is executing a guest's request (a forwarded button press, a dial a guest turned): the results of
        /// that must reach every guest too, so host capture never looks at Applying.
        /// </summary>
        public static bool HostEmits => IsHost;

        public static string LastError { get; private set; }

        public static event Action Started;
        public static event Action<string> Ended;

        public static T Add<T>(T m) where T : SyncModule { Modules.Add(m); return m; }
        public static IEnumerable<SyncModule> AllModules => Modules;

        public static T Get<T>() where T : SyncModule
        {
            foreach (var m in Modules) if (m is T t) return t;
            return null;
        }

        /// <summary>Runs <paramref name="apply"/> with <see cref="Applying"/> set. Exceptions are logged, not thrown.</summary>
        /// <summary>
        /// Applies a purely visual replay (such as a hover effect). Works like <see cref="Apply"/>, but a guest's mission
        /// logic stays blocked, in case the effect's listeners also notify the mission graph.
        /// </summary>
        public static void ApplyVisual(string what, Action apply)
        {
            bool was = VisualOnly;
            VisualOnly = true;
            try { Apply(what, apply); }
            finally { VisualOnly = was; }
        }

        public static bool VisualOnly { get; private set; }

        public static void Apply(string what, Action apply)
        {
            bool was = Applying;
            Applying = true;
            try { apply(); }
            catch (Exception e) { CoopLog.Error($"applying {what}: {e}"); }
            finally { Applying = was; }
        }

        public static NetWriter Writer() { Scratch.Reset(); return Scratch; }

        public static void Send(Msg type, NetWriter w, Delivery d = Delivery.Reliable, byte target = Protocol.TargetAll)
        {
            if (InSession) Session.Send((ushort)type, w, d, target);
        }

        public static void SendToHost(Msg type, NetWriter w, Delivery d = Delivery.Reliable)
        {
            if (InSession) Session.SendToHost((ushort)type, w, d);
        }

        // -------------------------------------------------------------- starting and stopping

        public static string LocalName =>
            LaunchArgs.Name ?? SteamApi.PersonaName() ?? Environment.UserName ?? "Gunner";

        /// <summary>The join key of the direct connection this machine hosts. Empty for a loopback-only test host.</summary>
        public static string TcpKey { get; private set; } = "";
        /// <summary>True when the session runs over a direct connection. Guests choose their own peer ids there, so bans cannot last.</summary>
        public static bool IsTcp { get; private set; }

        /// <summary>
        /// Starts hosting a direct connection (no Steam). With a key, the host listens on every network of this PC and
        /// admits only guests who type the key. Without a key it listens on this PC only, which is what two game
        /// copies and the automated tests use.
        /// </summary>
        public static void HostTcp(int port, string key)
        {
            ulong id = LaunchArgs.FakeId ?? 1;
            try
            {
                var bind = string.IsNullOrEmpty(key) ? System.Net.IPAddress.Loopback : System.Net.IPAddress.Any;
                var t = TcpTransport.Listen(id, port, bind, key ?? "");
                TcpKey = key ?? "";
                Begin(t, s => s.Host(LocalName, PlayerInfo.Encode()));
            }
            catch (Exception e) { Fail($"could not open port {port} for a direct connection: {e.Message}"); }
        }

        /// <summary>Makes a short join key for a direct-connection host, avoiding look-alike characters (0/O, 1/I).</summary>
        public static string NewTcpKey()
        {
            const string abc = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var b = new byte[6];
            System.Security.Cryptography.RandomNumberGenerator.Fill(b);
            var c = new char[6];
            for (int i = 0; i < 6; i++) c[i] = abc[b[i] % abc.Length];
            return new string(c);
        }

        static System.Threading.Tasks.Task<TcpTransport> _connecting;
        static string _connectingTo;
        public static bool Connecting => _connecting != null;

        /// <summary>Connects off the game thread because it can take seconds. The session starts on a later tick.</summary>
        public static void JoinTcp(string hostPort, string key)
        {
            if (_connecting != null) return;
            hostPort = (hostPort ?? "").Trim();
            var parts = hostPort.Split(':');
            int port = 27731;
            if (parts.Length > 2 || parts[0].Length == 0 || (parts.Length > 1 && !int.TryParse(parts[1], out port)) || port < 1 || port > 65535)
            { Fail($"'{hostPort}' is not an address: use host-ip:port, e.g. 192.168.1.20:27731"); return; }
            ulong id = LaunchArgs.FakeId ?? (ulong)new Random().Next(1000, int.MaxValue);
            string host = parts[0], k = (key ?? "").Trim().ToUpperInvariant();
            _connectingTo = hostPort;
            _connecting = System.Threading.Tasks.Task.Run(() => TcpTransport.Connect(id, 1, host, port, 5000, k));
        }

        static void FinishConnect()
        {
            var task = _connecting;
            if (task == null || !task.IsCompleted) return;
            _connecting = null;
            if (task.IsFaulted)
            {
                var e = task.Exception?.InnerException ?? task.Exception;
                Fail($"could not connect to {_connectingTo}: {e?.Message}");
                return;
            }
            TcpKey = "";
            try { Begin(task.Result, s => s.Join(1, LocalName, PlayerInfo.Encode())); }
            catch (Exception e) { Fail($"could not connect to {_connectingTo}: {e.Message}"); }
        }

        public static void HostSteam(SteamLobby.Visibility vis)
        {
            if (!SteamApi.Available) { Fail("Steam is not available: " + SteamApi.Error); return; }
            var lobby = new SteamLobby();
            Lobby = lobby;
            lobby.Create(vis, Protocol.MaxPlayers, Plugin.Version, ok =>
            {
                if (!ok) { Fail("Steam could not create the lobby: " + lobby.Error); Lobby = null; return; }
                var t = new SteamTransport(lobby.MemberIds);
                Begin(t, s => s.Host(LocalName, PlayerInfo.Encode()));
            });
        }

        public static void JoinSteam(ulong lobbyId)
        {
            if (!SteamApi.Available) { Fail("Steam is not available: " + SteamApi.Error); return; }
            var lobby = new SteamLobby();
            Lobby = lobby;
            lobby.Join(lobbyId, ok =>
            {
                if (!ok) { Fail("could not join the lobby: " + lobby.Error); Lobby = null; return; }
                if (lobby.ModVersion != null && lobby.ModVersion != Plugin.Version)
                {
                    Fail($"the host runs Nestmates {lobby.ModVersion}, you run {Plugin.Version}. Both need the same version.");
                    lobby.Leave(); Lobby = null; return;
                }
                var t = new SteamTransport(lobby.MemberIds);
                Begin(t, s => s.Join(lobby.Owner, LocalName, PlayerInfo.Encode()));
            });
        }

        static void Begin(ITransport t, Action<Session> start)
        {
            if (Session != null) Leave("restart");
            LastError = null;
            IsTcp = t is TcpTransport;
            if (LaunchArgs.InSandbox && LaunchArgs.Chaos != null && LaunchArgs.Chaos.Length == 3)   // the lag simulator is only for tests
                t = new ChaosTransport(t, LaunchArgs.Chaos[0], LaunchArgs.Chaos[1], LaunchArgs.Chaos[2]);
            var s = new Session(t, Plugin.Version);
            s.Log += m => CoopLog.Info("[net] " + m);
            s.AdmitFilter = Bans.AdmitFilter;
            s.Connected += () => OnStarted();
            s.Closed += reason => OnClosed(reason);
            s.PlayerJoined += p => { foreach (var m in Modules) Safe(m, () => m.OnPlayerJoined(p)); JoinToast(p, null); };
            s.PlayerLeft += (p, why) => { foreach (var m in Modules) Safe(m, () => m.OnPlayerLeft(p)); JoinToast(p, why ?? "left"); };
            foreach (var m in Modules) Safe(m, () => m.Register(s));
            Session = s;
            start(s);
            if (s.IsHost) OnStarted();
        }

        static float _startedAt;

        /// <summary>Shows a corner message such as "Anna joined" or "Ben left". The players already present when we join
        /// arrive as joins too, so they get one combined line instead of one message each.</summary>
        static void JoinToast(Player p, string left)
        {
            if (p == null || p.IsLocal || !Settings.ShowJoinLeave) return;
            var color = UI.Theme.PlayerColor(PlayerInfo.Decode(p.Info).Color);
            string name = string.IsNullOrEmpty(p.Name) ? "A player" : p.Name;
            if (left == null)
            {
                if (UnityEngine.Time.unscaledTime - _startedAt < 3f) return;
                UI.Toasts.Add($"{name} joined", color);
            }
            else UI.Toasts.Add(left == "left" ? $"{name} left" : $"{name} left ({left})", color);
        }

        static void OnStarted()
        {
            _startedAt = UnityEngine.Time.unscaledTime;
            if (!Session.IsHost && Settings.ShowJoinLeave)
            {
                var host = Session.GetPlayer(0);
                UI.Toasts.Add(host != null && !string.IsNullOrEmpty(host.Name) ? $"Joined {host.Name}'s game" : "Joined the game", UI.Theme.PlayerColor(PlayerInfo.Local.Color));
            }
            CoopLog.Info($"session started: {(Session.IsHost ? "host" : "guest")} slot {Session.LocalSlot}");
            foreach (var m in Modules) Safe(m, () => m.OnSessionStart());
            Started?.Invoke();
        }

        static void OnClosed(string reason)
        {
            CoopLog.Info($"session ended: {reason}");
            if (!reason.StartsWith("left") && reason != "restart" && reason != "shutdown") LastError = reason;
            foreach (var m in Modules) Safe(m, () => m.OnSessionEnd());
            Ended?.Invoke(reason);
            Session?.Transport.Dispose();
            Session = null;
            Lobby?.Leave();
            Lobby = null;
        }

        public static void Leave(string reason = "left")
        {
            if (Session == null) { Lobby?.Leave(); Lobby = null; return; }
            var s = Session;
            s.Leave(reason);
            if (Session == s) OnClosed(reason); // Leave on an unconnected session does not raise the Closed event
        }

        static void Fail(string why)
        {
            LastError = why;
            CoopLog.Warn(why);
        }

        internal static void Tick(float dt)
        {
            FinishConnect();
            Lobby?.Tick();
            var s = Session;
            if (s == null) return;
            long t0 = Perf.Start();
            s.Tick();
            Perf.Stop("network + messages", t0);
            if (!s.IsConnected) return;
            foreach (var m in Modules) { long t = Perf.Start(); Safe(m, () => m.Tick(dt)); Perf.Stop(m.Name, t); }
        }

        static void Safe(SyncModule m, Action a)
        {
            try { a(); }
            catch (Exception e) { CoopLog.Error($"[{m.Name}] {e}"); }
        }
    }
}
