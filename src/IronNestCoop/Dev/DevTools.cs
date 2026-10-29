using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime;
using SleepyNodes;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestCoop.Dev
{
    /// <summary>
    /// Test shortcuts for the host (or solo play), so a code path can be tested without playing for an hour to reach it.
    /// They are off unless [Debug] DevTools = true in the config. Everything runs on the host the game's own way
    /// (adding points, dealing cards, filling magazines, ending the mission), so the normal sync carries it to guests.
    ///
    /// For safety, the first use in a game run copies the save folder to a backup next to it, and from then on
    /// achievements, stats and leaderboard uploads are blocked until the game is restarted (as in the QA sandbox).
    /// Missions started from the Dev tab do not unlock anything by themselves, but finishing one counts as finishing it.
    /// </summary>
    public static class DevTools
    {
        /// <summary>True once a shortcut has been used in this game run. Online records then stay untouched (see SandboxOnline).</summary>
        public static bool Used { get; private set; }

        public static bool Enabled => Settings.DevTools && (!Coop.InSession || Coop.IsHost);

        public static string LastMessage;
        public static float LastMessageAt;
        public static bool ShowHelp;

        static float _armedUntil; static string _armed;

        public sealed class Action { public Key Key; public string Label; public Func<string> Run; public bool Confirm; }

        public static readonly List<Action> Actions = new()
        {
            new() { Key = Key.H, Label = "show / hide this list", Run = () => { ShowHelp = !ShowHelp; return null; } },
            new() { Key = Key.P, Label = "+500 requisition points", Run = AddPoints },
            new() { Key = Key.C, Label = "deal every punchcard into this mission's deck", Run = DealAllCards },
            new() { Key = Key.S, Label = "fill both magazines with HE shells", Run = FillMagazines },
            new() { Key = Key.G, Label = "+10 powder charges", Run = AddCharges },
            new() { Key = Key.V, Label = "break a random pressure valve", Run = BreakValve },
            new() { Key = Key.E, Label = "engine: stop / start", Run = ToggleEngine },
            new() { Key = Key.T, Label = "counter-battery timer +60 s", Run = () => AddTimer(60) },
            new() { Key = Key.Y, Label = "counter-battery timer to 10 s", Run = () => SetTimer(10) },
            new() { Key = Key.W, Label = "win the mission (press twice)", Run = Win, Confirm = true },
            new() { Key = Key.L, Label = "lose the mission (press twice)", Run = Lose, Confirm = true },
        };

        /// <summary>Called every frame by CoopRunner. Handles the Ctrl+Shift+key shortcuts.</summary>
        public static void Update()
        {
            if (!Enabled) { ShowHelp = false; return; }
            var kb = Keyboard.current;
            if (kb == null || !(kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) || !(kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed)) return;
            foreach (var a in Actions)
                if (kb[a.Key].wasPressedThisFrame) { Run(a); break; }
        }

        public static void Run(Action a)
        {
            if (!Enabled) return;
            if (a.Confirm && !(_armed == a.Label && Time.unscaledTime < _armedUntil))
            {
                _armed = a.Label; _armedUntil = Time.unscaledTime + 3f;
                Say($"press again within 3 s to {a.Label.Replace(" (press twice)", "")}");
                return;
            }
            _armed = null;
            string result;
            try
            {
                if (a.Key != Key.H) FirstUse();
                result = a.Run();
            }
            catch (Exception e) { result = "failed: " + e.Message; CoopLog.Warn($"[dev] {a.Label} failed: {e}"); }
            if (result != null) { Say(result); CoopLog.Info($"[dev] {a.Label}: {result}"); }
        }

        static void Say(string m) { LastMessage = m; LastMessageAt = Time.unscaledTime; }

        // ------------------------------------------------------------------ safety

        static void FirstUse()
        {
            if (Used) return;
            Used = true;
            CoopLog.Info("[dev] test shortcuts in use: achievements, stats and leaderboard uploads are off until the game restarts");
            if (Sandbox.Active) { CoopLog.Info("[dev] test copy: saves are sandboxed, no backup needed"); return; }
            try
            {
                string src = Application.persistentDataPath;
                string dst = Path.Combine(Path.GetDirectoryName(src) ?? src, Path.GetFileName(src) + "_coop-dev-backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                int n = 0;
                foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    if (fi.Extension.Equals(".log", StringComparison.OrdinalIgnoreCase) || fi.Length > 64L * 1024 * 1024) continue;
                    string to = Path.Combine(dst, Path.GetRelativePath(src, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(f, to);
                    n++;
                }
                CoopLog.Info($"[dev] saves backed up before the first shortcut: {n} files -> {dst}");
            }
            catch (Exception e) { CoopLog.Warn("[dev] save backup failed: " + e.Message); }
        }

        // ------------------------------------------------------------------ actions

        static bool InMission => MissionManager.Instance != null && MissionManager.Instance._CurrentPhase_k__BackingField == MissionManager.GamePhase.MissionActive;

        static string AddPoints()
        {
            var st = MissionStatsTracker.Instance;
            if (st == null || !InMission) return "not in a mission";
            st.AddRequisitionPoints(500, "dev");
            return $"requisition points now {st.RequisitionPoints}";
        }

        static string DealAllCards()
        {
            var con = RequisitionConsoleManager.Instance;
            if (con == null || !InMission) return "no requisition console in this mission";
            var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PunchcardDefinitionV2>()))
            {
                var d = o.TryCast<PunchcardDefinitionV2>();
                if (d != null && !string.IsNullOrEmpty(d.ID) && !d.ID.Contains("Demo") && !d.name.Contains("(Clone)")) defs.Add(d);
            }
            con.AddSetCardsToDeck(defs);   // this mission's deck only; nothing is unlocked in the campaign
            return $"{defs.Count} punchcards dealt";
        }

        static string FillMagazines()
        {
            if (!InMission) return "not in a mission";
            ShellDefinition he = MissionSaveManager.FindShell("HE");
            if (he == null) return "no HE shell definition";
            int n = 0;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<CylinderShellSelector>()))
            {
                var c = o.TryCast<CylinderShellSelector>();
                for (int i = 0; c != null && i < 12 && c.TryInsertShellRuntime(he, ShellSlotPool.ShellSource.Mission, out _); i++) n++;
            }
            return $"{n} HE shells loaded into the magazines";
        }

        static string AddCharges()
        {
            var inv = PowderChargeInventory.Instance;
            if (inv == null || !InMission) return "no powder charge stock here";
            inv.AddCharges(10);
            return $"powder charges now {inv.CurrentCharges}";
        }

        static string BreakValve()
        {
            var m = HighPressureSystemManager.GetRandomManager();
            if (m == null || !InMission) return "no pressure system here";
            m.DamageRandomValve();
            return "a valve broke";
        }

        static string ToggleEngine()
        {
            var e = UnityEngine.Object.FindObjectOfType<DieselEngineController>();
            if (e == null || !InMission) return "no engine here";
            if (e.EnginesRunning) { e.ForceStop(); return "engine stopped"; }
            e.ForceStart(); return "engine started";
        }

        static string AddTimer(float s)
        {
            var t = CounterBatteryTimer.Instance;
            if (t == null || !InMission) return "no counter-battery timer in this mission";
            WorldEvents.TimerUnscaled = true;
            try { t.AddTime(s); } finally { WorldEvents.TimerUnscaled = false; }
            return $"counter-battery: {t.TimeRemaining:F0} s left";
        }

        static string SetTimer(float s)
        {
            var t = CounterBatteryTimer.Instance;
            if (t == null || !InMission) return "no counter-battery timer in this mission";
            WorldEvents.TimerUnscaled = true;
            try { t.SetTime(s); } finally { WorldEvents.TimerUnscaled = false; }
            return $"counter-battery: {t.TimeRemaining:F0} s left";
        }

        static string Win()
        {
            if (!InMission) return "not in a mission";
            MissionManager.Instance.MarkMissionComplete(true);
            return "mission marked complete";
        }

        static string Lose()
        {
            if (!InMission) return "not in a mission";
            MissionManager.Instance.MarkMissionFailed(true);
            return "mission marked failed";
        }

        // ------------------------------------------------------------------ missions (Dev tab)

        /// <summary>Lists every mission of the campaign in campaign order, as (id, whether it is already unlocked for this profile).</summary>
        public static List<(string Id, MissionGraph Mission)> Missions()
        {
            var list = new List<(string, MissionGraph)>();
            var op = MissionFlow.FindOperation("Base_Campaign");
            if (op?.Missions == null) return list;
            foreach (var n in op.Missions) if (n?.Mission != null) list.Add((n.Mission.MissionID, n.Mission));
            return list;
        }

        /// <summary>Starts any campaign mission. Co-op guests follow as usual.</summary>
        public static string StartMission(string id)
        {
            if (!Enabled) return null;
            if (!MissionFlow.GameReady) return "the game is still starting";
            var op = MissionFlow.FindOperation("Base_Campaign");
            var m = MissionFlow.FindMission(op, id);
            if (m == null) return $"no mission {id}";
            FirstUse();
            if (InMission) MissionManager.Instance.EndOperationAndReturnToMenu();
            MissionManager.Instance.StartOperation(op, m);
            CoopLog.Info($"[dev] started mission {id}");
            return $"starting {id}";
        }

        /// <summary>The key list, drawn by CoopRunner.OnGUI while ShowHelp is on, followed by the last result for 4 seconds.</summary>
        public static void OnGUI()
        {
            if (!Enabled) return;
            var style = UI.Theme.Small;
            if (ShowHelp)
            {
                float h = 22 + Actions.Count * 18;
                var r = new Rect(12, 40, 380, h);
                GUI.Box(r, GUIContent.none, UI.Theme.Window);
                GUI.Label(new Rect(r.x + 8, r.y + 3, r.width, 18), "Dev shortcuts (host): Ctrl+Shift + key", UI.Theme.Section);
                for (int i = 0; i < Actions.Count; i++)
                    GUI.Label(new Rect(r.x + 8, r.y + 22 + i * 18, r.width - 16, 18), $"{Actions[i].Key}   {Actions[i].Label}", style);
            }
            if (LastMessage != null && Time.unscaledTime - LastMessageAt < 4f)
                GUI.Label(new Rect(12, Screen.height - 60, 600, 22), "[dev] " + LastMessage, UI.Theme.Section);
        }
    }
}
