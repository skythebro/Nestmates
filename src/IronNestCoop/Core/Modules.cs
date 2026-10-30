namespace IronNestCoop.Core
{
    /// <summary>Creates every sync module once, in dependency order (clock first, then flow, then the rest).</summary>
    public static class Modules
    {
        static bool _done;

        public static void CreateAll()
        {
            if (_done) return;
            _done = true;
            Settings.Bind(OldName.OpenConfig(new BepInEx.BepInPlugin(Plugin.Guid, Plugin.Name, Plugin.Version)));
            Coop.Add(new HostRules());   // must come first, because the other modules read the rules
            Coop.Add(new Sync.MissionFlow());
            Coop.Add(new Sync.WorldReady());
            Coop.Add(new Sync.WorldEntities());
            Coop.Add(new Sync.WorldEvents());
            Coop.Add(new Sync.Avatars());
            Coop.Add(new Sync.Controls());
            Coop.Add(new Sync.Animators());
            Coop.Add(new Sync.Visibility());
            Coop.Add(new Sync.Paint());
            Coop.Add(new Sync.Gunnery());
            Coop.Add(new Sync.Machinery());
            Coop.Add(new Sync.Music());
            Coop.Add(new Sync.Cats());
            Coop.Add(new Sync.Requisition());   // must come before Items, because the deck has to exist before cards are placed
            Coop.Add(new Sync.Items());
            Coop.Add(new Sync.Markers());
            Coop.Add(new Sync.Unlocks());
            Coop.Add(new Sync.NoPause());
            Coop.Add(new Sync.Toggles());
            Coop.Add(new Sync.Espresso());
            Coop.Add(new Sync.PrinterAlerts());
            Coop.Add(new Sync.Newspaper());
            Coop.Add(new Sync.MedalValues());
        }
    }

    /// <summary>Returns a one-line summary of the mission state for the lobby window.</summary>
    public static class MissionStatus
    {
        public static string Describe()
        {
            var mm = MissionManager.Instance;
            if (mm == null) return "Loading…";
            var phase = mm._CurrentPhase_k__BackingField;
            if (phase == MissionManager.GamePhase.MissionActive)
            {
                var m = mm._CurrentMission_k__BackingField;
                return m != null ? $"In mission: {m.MissionID}" : "In mission";
            }
            return phase == MissionManager.GamePhase.BrowsingMap ? "On the operations map" : "In the main menu";
        }
    }
}
