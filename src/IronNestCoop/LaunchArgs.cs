using System;
using System.Collections.Generic;
using System.Globalization;

namespace IronNestCoop
{
    /// <summary>
    /// Command-line switches. Players never need any; they exist for automated tests and power users.
    ///   -coop-host=PORT             host a direct (TCP) connection instead of Steam: on this PC only, or on the
    ///                               network when -coop-key is given
    ///   -coop-join=HOST:PORT        join a direct-connection host
    ///   -coop-key=KEY               the direct connection's join key
    ///   -coop-id=N                  fake player id for TCP sessions (tests)
    ///   -coop-name=NAME             player name override
    ///   -coop-sandbox=DIR           redirect saves into DIR (for test copies, so real saves stay untouched)
    ///   -coop-bridge=PORT           enable the loopback test bridge (QA only)
    ///   -coop-chaos=LAT,JIT,LOSS    simulated latency ms, jitter ms, loss 0..1
    ///   -coop-log=PATH              log file path
    ///   -coop-verbose               debug logging
    ///   -coop-bot=SCENARIO:ROLE     run an in-game QA bot scenario (needs -coop-sandbox)
    ///   -coop-mission=ID            mission for QA scenarios that take one (gunnery)
    ///   -coop-join-delay=SECONDS    QA: join only after this many seconds (late-join tests)
    ///   -coop-result=PATH           where the bot writes its JSON verdict
    ///   +connect_lobby ID           Steam "Join Game" launch
    /// </summary>
    public static class LaunchArgs
    {
        public static int? TcpHostPort;
        public static string TcpJoin;
        /// <summary>-coop-key=KEY: the direct-connection join key (a host with one listens on the network).</summary>
        public static string TcpKey = "";
        public static ulong? FakeId;
        public static string Name;
        public static string Sandbox;
        public static int? BridgePort;
        public static double[] Chaos;
        public static string LogPath;
        public static bool Verbose;
        public static ulong? ConnectLobby;
        public static string Bot;
        public static string ResultPath;
        public static string Mission;
        /// <summary>QA: limits the sweep to objects whose path contains this text.</summary>
        public static string Filter;
        public static float JoinDelay;

        static readonly List<string> Seen = new();

        public static void Parse(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string Val() { int eq = a.IndexOf('='); return eq < 0 ? "" : a.Substring(eq + 1); }
                if (a.StartsWith("-coop-host=")) TcpHostPort = int.Parse(Val(), CultureInfo.InvariantCulture);
                else if (a.StartsWith("-coop-join=")) TcpJoin = Val();
                else if (a.StartsWith("-coop-key=")) TcpKey = Val();
                else if (a.StartsWith("-coop-id=")) FakeId = ulong.Parse(Val(), CultureInfo.InvariantCulture);
                else if (a.StartsWith("-coop-name=")) Name = Val();
                else if (a.StartsWith("-coop-sandbox=")) Sandbox = Val();
                else if (a.StartsWith("-coop-bridge=")) BridgePort = int.Parse(Val(), CultureInfo.InvariantCulture);
                else if (a.StartsWith("-coop-chaos="))
                    Chaos = Array.ConvertAll(Val().Split(','), s => double.Parse(s, CultureInfo.InvariantCulture));
                else if (a.StartsWith("-coop-log=")) LogPath = Val();
                else if (a == "-coop-verbose") Verbose = true;
                else if (a.StartsWith("-coop-bot=")) Bot = Val();
                else if (a.StartsWith("-coop-mission=")) Mission = Val().Replace('_', ' ');
                else if (a.StartsWith("-coop-filter=")) Filter = Val().Replace('_', ' ');
                else if (a.StartsWith("-coop-join-delay=")) float.TryParse(Val(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out JoinDelay);
                else if (a.StartsWith("-coop-result=")) ResultPath = Val();
                else if (a == "+connect_lobby" && i + 1 < args.Length && ulong.TryParse(args[i + 1], out var lobby)) ConnectLobby = lobby;
                else continue;
                Seen.Add(a);
            }
        }

        public static bool IsTest => BridgePort != null || Sandbox != null;

        /// <summary>True when started by the QA runner (a sandboxed save folder): only then do the test switches below work.</summary>
        public static readonly bool InSandbox = System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a.StartsWith("-coop-sandbox="));

        /// <summary>QA mutation switch: -coop-mutant=NAME turns one sync safeguard off on purpose, so a test can prove
        /// that the safeguard matters (that test must then fail). Always false outside the sandbox.</summary>
        public static bool Mutant(string name) => InSandbox && System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-coop-mutant=" + name) >= 0;

        /// <summary>Describes the arguments for the log, leaving out the join key.</summary>
        public static string Describe() => Seen.Count == 0 ? "none" : string.Join(" ", Seen.ConvertAll(a => a.StartsWith("-coop-key=") ? "-coop-key=(hidden)" : a));
    }
}
