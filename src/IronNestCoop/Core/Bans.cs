using System;
using System.Collections.Generic;
using System.IO;

namespace IronNestCoop.Core
{
    /// <summary>The host's permanent ban list, stored as one SteamID64 per line in BepInEx/config/Nestmates_bans.txt.</summary>
    public static class Bans
    {
        static HashSet<ulong> _ids;
        static string PathName => Path.Combine(BepInEx.Paths.ConfigPath, OldName.BansFileName);

        static HashSet<ulong> Ids
        {
            get
            {
                if (_ids != null) return _ids;
                _ids = new HashSet<ulong>();
                try
                {
                    if (File.Exists(PathName))
                        foreach (var line in File.ReadAllLines(PathName))
                        {
                            var t = line.Split('#')[0].Trim();
                            if (ulong.TryParse(t, out var id)) _ids.Add(id);
                        }
                }
                catch (Exception e) { CoopLog.Warn("could not read ban list: " + e.Message); }
                return _ids;
            }
        }

        public static string AdmitFilter(ulong peer, string name) => Ids.Contains(peer) ? "you are banned from this host's games" : null;

        public static void Ban(ulong peer, string name)
        {
            if (!Ids.Add(peer)) return;
            try { File.AppendAllText(PathName, $"{peer} # {name} {DateTime.Now:yyyy-MM-dd}{Environment.NewLine}"); }
            catch (Exception e) { CoopLog.Warn("could not save ban list: " + e.Message); }
        }
    }
}
