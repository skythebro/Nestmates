using System;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;

namespace IronNestCoop
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInIncompatibility(Core.OldName.Guid)]   // stops the old test builds' folder from loading next to this one
    public sealed class Plugin : BasePlugin
    {
        public const string Guid = "io.github.nestmates";
        public const string Name = "Nestmates";
        public const string Version = "1.0.2";

        public static Plugin Instance { get; private set; }
        public static Harmony Harmony { get; private set; }

        /// <summary>QA bisection helper: -coop-nopatch=A,B skips patch classes whose "Outer.Inner" name contains A or B.</summary>
        static bool Skip(Type t)
        {
            if (!LaunchArgs.InSandbox) return false;   // a test switch that does nothing in normal play
            foreach (var a in Environment.GetCommandLineArgs())
            {
                if (!a.StartsWith("-coop-nopatch=")) continue;
                string full = (t.DeclaringType != null ? t.DeclaringType.Name + "." : "") + t.Name;
                foreach (var part in a.Substring(14).Split(','))
                    if (part.Length > 0 && full.Contains(part)) return true;
            }
            return false;
        }

        public override void Load()
        {
            Instance = this;
            LaunchArgs.Parse(Environment.GetCommandLineArgs());
            CoopLog.Init(LaunchArgs.LogPath);
            CoopLog.Info($"{Name} {Version} loading (args: {LaunchArgs.Describe()})");

            if (LaunchArgs.Sandbox != null) Sandbox.Enable(LaunchArgs.Sandbox);

            Harmony = new Harmony(Guid);
            int ok = 0, failed = 0;
            foreach (var t in typeof(Plugin).Assembly.GetTypes())
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                if (Skip(t)) { CoopLog.Info($"patch {t.DeclaringType?.Name}.{t.Name} disabled by -coop-nopatch"); continue; }
                try { Harmony.CreateClassProcessor(t).Patch(); ok++; }
                catch (Exception e) { failed++; CoopLog.Error($"patch {t.Name} failed: {e.Message}"); }
            }
            CoopLog.Info($"patch classes applied: {ok}, failed: {failed}");

            ClassInjector.RegisterTypeInIl2Cpp<CoopRunner>();
            AddComponent<CoopRunner>();
        }
    }
}
