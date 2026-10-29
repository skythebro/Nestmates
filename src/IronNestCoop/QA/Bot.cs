using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Wire;

namespace IronNestCoop.QA
{
    /// <summary>
    /// In-game QA bot (the bot runs inside the game process). Active only with -coop-bot and a
    /// sandbox. A scenario is a C# iterator stepped once per frame; it drives the game through the
    /// same calls a player's input reaches, asserts on real state and writes a JSON verdict that
    /// tools/coopqa.py reads. Bots on host and guest coordinate with BotSignal messages.
    /// </summary>
    public sealed class Bot : SyncModule
    {
        public override string Name => "bot";

        public static Bot Instance { get; private set; }

        public readonly string Scenario;
        public readonly bool IsHostRole;
        readonly List<string> _failures = new();
        readonly List<string> _notes = new();
        readonly Dictionary<string, string> _signals = new();
        IEnumerator<object> _steps;
        bool _done;
        double _started;

        Bot(string scenario, bool host) { Scenario = scenario; IsHostRole = host; }

        public static void StartIfRequested()
        {
            if (LaunchArgs.Bot == null) return;
            if (!Sandbox.Active) { CoopLog.Error("bot refused: -coop-bot needs -coop-sandbox"); return; }
            var parts = LaunchArgs.Bot.Split(':');
            Instance = Coop.Add(new Bot(parts[0], parts.Length > 1 && parts[1] == "host"));
            CoopLog.Info($"bot: scenario '{Instance.Scenario}' as {(Instance.IsHostRole ? "host" : "guest")}");
        }

        // ------------------------------------------------------------- helpers for scenarios

        public double Now => UnityEngine.Time.realtimeSinceStartupAsDouble;
        public double Elapsed => Now - _started;

        public void Note(string m) { _notes.Add($"{Elapsed:F1}s {m}"); CoopLog.Info("[bot] " + m); }

        public void Check(bool ok, string what)
        {
            if (ok) Note("ok: " + what);
            else { _failures.Add($"{Elapsed:F1}s {what}"); CoopLog.Warn("[bot] FAILED: " + what); }
        }

        public void Signal(string key, string value = "1")
        {
            var w = Coop.Writer(); w.Str(key); w.Str(value);
            Coop.Send(Msg.BotSignal, w);
            Note($"signal sent {key}={(key.StartsWith("~") ? $"({value.Length} chars)" : value)}");   // "~" keys: bulk data, not logged
        }

        public bool HasSignal(string key) => _signals.ContainsKey(key);
        public string SignalValue(string key) => _signals.TryGetValue(key, out var v) ? v : null;

        /// <summary>Yield this from a scenario: waits until cond holds, or fails the step after timeout.</summary>
        public IEnumerable<object> Until(Func<bool> cond, double timeoutSec, string what, bool soft = false)
        {
            double end = Now + timeoutSec;
            while (true)
            {
                bool ok;
                try { ok = cond(); } catch (Exception e) { ok = false; Note($"waiting for {what}: {e.Message}"); }
                if (ok) { if (what != null) Note("reached: " + what); yield break; }
                if (Now > end && soft) yield break;
                if (Now > end) { Check(false, $"timed out after {timeoutSec}s waiting for {what}"); throw new BotAbort(); }
                yield return null;
            }
        }

        /// <summary>In-engine screenshot into the run folder (never a desktop capture).</summary>
        public IEnumerable<object> Shot(string name)
        {
            if (LaunchArgs.ResultPath == null) yield break;
            var dir = Path.Combine(Path.GetDirectoryName(LaunchArgs.ResultPath), "shots");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, name + ".png");
            UnityEngine.ScreenCapture.CaptureScreenshot(file);
            foreach (var x in Wait(0.6)) yield return x;
            Note($"screenshot {name}: {(File.Exists(file) ? "saved" : "MISSING")}");
        }

        public IEnumerable<object> Wait(double seconds)
        {
            double end = Now + seconds;
            while (Now < end) yield return null;
        }

        sealed class BotAbort : Exception { }

        // ------------------------------------------------------------- module

        public override void Register(Session s)
        {
            s.On((ushort)Msg.BotSignal, (in MsgContext c, NetReader r) =>
            {
                string k = r.Str(256), v = r.Str(256 * 1024);   // QA signals can carry whole state lists (toggles: ~25 KB)
                _signals[k] = v;
                CoopLog.Info($"[bot] signal received {k}={(k.StartsWith("~") ? $"({v.Length} chars)" : v)} from slot {c.FromSlot}");
            });
        }

        /// <summary>Called every frame by CoopRunner, connected or not.</summary>
        public void Step()
        {
            if (_done) return;
            if (_steps == null)
            {
                _started = Now;
                _steps = Scenarios.Create(this)?.GetEnumerator();
                if (_steps == null) { Check(false, $"unknown scenario '{Scenario}'"); Finish(); return; }
            }
            try
            {
                if (!_steps.MoveNext()) Finish();
            }
            catch (BotAbort) { Finish(); }
            catch (Exception e) { Check(false, "scenario threw: " + e); Finish(); }
        }

        /// <summary>Stack traces in BepInEx's LogOutput.log that run through our code (patch exceptions).</summary>
        static int SwallowedPatchExceptions(out string first)
        {
            first = null;
            try
            {
                var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log");
                if (!System.IO.File.Exists(path)) return 0;
                using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
                using var rd = new System.IO.StreamReader(fs);
                int n = 0; string line, lastException = null;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.Contains("Exception:")) lastException = line.Trim();
                    else if (line.TrimStart().StartsWith("at IronNestCoop.") && lastException != null) { n++; first ??= lastException + " " + line.Trim(); lastException = null; }
                }
                return n;
            }
            catch (System.Exception) { return 0; }
        }

        void Finish()
        {
            _done = true;
            // A run that logged mod errors is not a pass, whatever the scenario asserted.
            if (CoopLog.ErrorCount > 0) _failures.Add($"mod logged {CoopLog.ErrorCount} error(s); first: {CoopLog.FirstError}");
            // Exceptions thrown inside Harmony patches are logged by BepInEx/Il2CppInterop, not by us: count those too.
            var swallowed = SwallowedPatchExceptions(out var firstSwallowed);
            if (swallowed > 0) _failures.Add($"{swallowed} exception(s) inside the mod's patches (BepInEx log); first: {firstSwallowed}");
            bool pass = _failures.Count == 0;
            var sb = new StringBuilder();
            sb.Append("{\"pass\":").Append(pass ? "true" : "false");
            sb.Append(",\"scenario\":").Append(Json(Scenario));
            sb.Append(",\"role\":").Append(Json(IsHostRole ? "host" : "guest"));
            sb.Append(",\"seconds\":").Append(Elapsed.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));
            sb.Append(",\"summary\":").Append(Json($"{_notes.Count} steps, {_failures.Count} failures"));
            sb.Append(",\"failures\":[");
            for (int i = 0; i < _failures.Count; i++) sb.Append(i > 0 ? "," : "").Append(Json(_failures[i]));
            sb.Append("],\"notes\":[");
            for (int i = 0; i < _notes.Count; i++) sb.Append(i > 0 ? "," : "").Append(Json(_notes[i]));
            sb.Append("]}");
            CoopLog.Info($"[bot] RESULT: {(pass ? "PASS" : "FAIL")} {Scenario}");
            if (LaunchArgs.ResultPath != null)
            {
                var tmp = LaunchArgs.ResultPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString());
                File.Move(tmp, LaunchArgs.ResultPath, true);
            }
        }

        static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (var c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }
    }
}
