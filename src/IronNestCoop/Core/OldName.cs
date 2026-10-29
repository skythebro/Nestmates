using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;

namespace IronNestCoop.Core
{
    /// <summary>
    /// The mod was called "IronNest Co-op" (GUID io.github.ironnestcoop) in its test builds and is now Nestmates.
    /// On the first start under the new name, the old settings and ban list are copied over (the old files are left
    /// where they are). The old plugin folder itself is not touched here: Plugin declares the old GUID incompatible,
    /// so a leftover BepInEx\plugins\IronNestCoop\ can't load next to this one.
    /// </summary>
    public static class OldName
    {
        public const string Guid = "io.github.ironnestcoop";
        public const string ConfigFileName = "Nestmates.cfg", BansFileName = "Nestmates_bans.txt", LogFileName = "Nestmates.log";
        const string OldConfig = Guid + ".cfg", OldBans = "IronNestCoop_bans.txt";

        /// <summary>Returns the settings file. The first time, it starts with the values copied from the old one.</summary>
        public static ConfigFile OpenConfig(BepInPlugin meta)
        {
            string path = Path.Combine(Paths.ConfigPath, ConfigFileName);
            CopyOnce(Path.Combine(Paths.ConfigPath, OldConfig), path, "settings");
            CopyOnce(Path.Combine(Paths.ConfigPath, OldBans), Path.Combine(Paths.ConfigPath, BansFileName), "ban list");
            return new ConfigFile(path, true, meta);
        }

        static void CopyOnce(string from, string to, string what)
        {
            try
            {
                if (File.Exists(to) || !File.Exists(from)) return;
                File.Copy(from, to);
                CoopLog.Info($"[nestmates] took over the {what} of IronNest Co-op ({Path.GetFileName(from)} -> {Path.GetFileName(to)})");
            }
            catch (Exception e) { CoopLog.Warn($"[nestmates] could not copy the old {what}: {e.Message}"); }
        }
    }
}
