using System;
using System.IO;
using BepInEx.Logging;

namespace IronNestCoop
{
    /// <summary>
    /// Writes to the BepInEx log and to our own flushed file (BepInEx/Nestmates.log by default),
    /// so a crash or a test run always leaves a complete record. English only, with timestamps.
    /// </summary>
    public static class CoopLog
    {
        static readonly ManualLogSource Src = Logger.CreateLogSource("Nestmates");
        static StreamWriter _file;
        static readonly object Lock = new();

        public static string FilePath { get; private set; }

        public static void Init(string path)
        {
            try
            {
                FilePath = path ?? Path.Combine(BepInEx.Paths.BepInExRootPath, Core.OldName.LogFileName);
                _file = new StreamWriter(new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            catch (Exception e) { Src.LogWarning($"own log file unavailable: {e.Message}"); }
        }

        static void Write(string level, string m)
        {
            lock (Lock) _file?.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {m}");
        }

        public static void Info(string m) { Src.LogInfo(m); Write("INFO", m); }
        public static void Warn(string m) { Src.LogWarning(m); Write("WARN", m); }
        public static int ErrorCount;
        public static string FirstError;
        static readonly System.Collections.Generic.HashSet<string> Seen = new();

        public static void Error(string m) { ErrorCount++; FirstError ??= m; Src.LogError(m); Write("ERROR", m); }

        /// <summary>Log an error once per key (per-frame failures would flood the log).</summary>
        public static void ErrorOnce(string key, string m)
        {
            lock (Lock) if (!Seen.Add(key)) { ErrorCount++; return; }
            Error(m);
        }

        /// <summary>Logs the first exception thrown by one of our Harmony patches, once per patch. The game carries on,
        /// because an exception escaping a patch would otherwise unwind into the game's own code.</summary>
        public static void PatchFailed(string where, Exception e) => ErrorOnce("patch:" + where, $"[patch] {where} failed: {e}");

        /// <summary>Log a warning once per key.</summary>
        public static void WarnOnce(string key, string m)
        {
            lock (Lock) if (!Seen.Add("w:" + key)) return;
            Warn(m);
        }

        /// <summary>Writes to our own file only. Meant for high-volume diagnostics.</summary>
        /// <summary>Detailed logging is on (the DetailedLog setting, or -coop-verbose for the tests).</summary>
        public static bool DebugOn => LaunchArgs.Verbose || Core.Settings.VerboseLog;

        public static void Debug(string m) { if (DebugOn) Write("DEBUG", m); }
    }
}
