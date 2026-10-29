using BepInEx.Configuration;

namespace IronNestCoop.Core
{
    /// <summary>Player-facing options, stored in BepInEx/config/Nestmates.cfg.</summary>
    public static class Settings
    {
        static ConfigEntry<bool> _devTools, _nameTags, _hud, _cardsLocal, _cat, _mapPresence, _verbose, _sharedVolume, _sharedHover, _sharedDrawings;
        static ConfigEntry<int> _timerPct;
        static ConfigEntry<bool> _skipIntro, _steamStatus, _steamStation, _discordStatus, _joinToasts, _ownCatTag, _hideCodes, _emoteKeys, _discordJoin;
        static ConfigEntry<string> _discordApp, _discordImage;
        static ConfigEntry<int> _color, _hue;

        public static void Bind(ConfigFile cfg)
        {
            _nameTags = cfg.Bind("Display", "ShowNameTags", true, "Name tags above other players.");
            _hud = cfg.Bind("Display", "ShowCrewList", true, "Crew list in the top-left corner while the window is closed.");
            _devTools = cfg.Bind("Debug", "DevTools", false, "Test shortcuts for the host (Ctrl+Shift+H lists them) and a Dev tab in the co-op window: points, cards, shells, charges, valves, engine, timer, win/lose, start any mission. The first use backs up your saves and turns achievements, stats and leaderboards off until the game restarts.");
            _verbose = cfg.Bind("Logging", "DetailedLog", false, "Detailed log lines (what each sync module sends and applies, and slow-frame warnings). Off by default. For a bug report: turn it on, play until the problem happens, then send BepInEx\\Nestmates.log.");
            _mapPresence = cfg.Bind("Display", "ShowMapPresence", true, "While someone uses the tactical map, show their pointer on the table and their figure at the table edge next to it.");
            _steamStatus = cfg.Bind("Steam", "ShowStatus", true, "Your Steam friends see what you are doing: main menu, operations map, or the mission and how long you have played it (and with how many players).");
            _steamStation = cfg.Bind("Status", "ShowStation", false, "Add where you are in the turret to your Steam and Discord status (at the tactical map, at the coffee machine, ...).");
            _discordStatus = cfg.Bind("Discord", "ShowStatus", true, "Your Discord friends see what you are doing, like the Steam status (needs the Discord app running on this PC).");
            _discordApp = cfg.Bind("Discord", "ApplicationId", "", "The id of another Discord application to show the status as (Discord developer portal). Empty: Nestmates' own. To turn the Discord status off, use ShowStatus.");
            _discordImage = cfg.Bind("Discord", "ImageKey", "nestmates", "Name of the picture uploaded to that Discord application's Rich Presence art (empty: no picture).");
            _joinToasts = cfg.Bind("Display", "ShowJoinLeave", true, "A short message in the top-left corner when a player joins or leaves.");
            _ownCatTag = cfg.Bind("Display", "ShowOwnCatName", true, "Your own cat's name above it while playing together (to find it in the turret).");
            _hideCodes = cfg.Bind("Display", "HideJoinCodes", true, "Streamer mode: the lobby code, the join key and this PC's network addresses show as dots in the co-op window (Copy still copies them; Show reveals them for a moment). Off: always shown.");
            _emoteKeys = cfg.Bind("Display", "EmoteKeys", true, "Keys 1-8 play an emote on your figure for the other players (wave, point, thumbs up, ...). Off: the keys do nothing.");
            _discordJoin = cfg.Bind("Discord", "AllowJoin", true, "While you play together over Steam, your Discord status has a Join button: a Discord friend with the game and Nestmates running joins your game with it (the lobby stays friends-only: they must be your Steam friend too).");
            _skipIntro = cfg.Bind("Display", "SkipIntro", true, "Skip the game's launch intro (the logo and credits before the turret), as pressing Esc does.");
            _color = cfg.Bind("Player", "Color", 0, "Your colour (0-5).");
            _cat = cfg.Bind("Player", "BringCat", true, "Spawn your own cat for the other players.");
            _cardsLocal = cfg.Bind("Host", "CardsToComputerOnly", false, "Fire-mission cards go only to the player who pressed Calculate (the computer operator).");
            _sharedVolume = cfg.Bind("Host", "SharedRecordVolume", true, "One record player volume for everyone. Off: each player sets how loud they hear the record player.");
            _sharedHover = cfg.Bind("Host", "SharedHoverEffects", true, "Hover effects (a lever pulling its clamp back while looked at) show for everyone. Off: only for the player looking.");
            _sharedDrawings = cfg.Bind("Host", "SharedMapDrawings", true, "Lines drawn on the tactical map are on everyone's map. Off: each player draws on their own map.");
            _timerPct = cfg.Bind("Host", "CounterBatteryTimerPercent", 100, "Length of the counter-battery timer in co-op, in percent of the game's own: 0 (off: it stays paused), 50, 100, 200 or 300 (other values go to the nearest). More players, more chaos: give them more time, or less for a challenge.");
            _hue = cfg.Bind("Player", "Hue", -1, "Your colour as a hue around the colour wheel (0-255). Pick it in the co-op window. No two players can share one.");
            if (_hue.Value < 0)
            {
                // first run with hue colours: start from the old palette colour
                UnityEngine.Color.RGBToHSV(UI.Theme.PlayerColors[_color.Value % UI.Theme.PlayerColors.Length], out float h, out _, out _);
                _hue.Value = (int)(h * 256) & 255;
            }
            PlayerInfo.Local.Color = (byte)(_hue.Value & 255);
            PlayerInfo.Local.CatEnabled = _cat.Value;
        }

        public static bool ShowNameTags { get => _nameTags?.Value ?? true; set { if (_nameTags != null) _nameTags.Value = value; } }
        public static bool ShowHud { get => _hud?.Value ?? true; set { if (_hud != null) _hud.Value = value; } }
        public static bool DevTools { get => _devTools?.Value ?? false; set { if (_devTools != null) _devTools.Value = value; } }
        public static bool VerboseLog => _verbose?.Value ?? false;
        public static bool ShowMapPresence { get => _mapPresence?.Value ?? true; set { if (_mapPresence != null) _mapPresence.Value = value; } }
        public static bool SteamStatus { get => _steamStatus?.Value ?? true; set { if (_steamStatus != null) _steamStatus.Value = value; } }
        public static bool StatusStation { get => _steamStation?.Value ?? false; set { if (_steamStation != null) _steamStation.Value = value; } }
        public static bool DiscordStatus { get => _discordStatus?.Value ?? true; set { if (_discordStatus != null) _discordStatus.Value = value; } }
        /// <summary>The Nestmates Discord application id. The status pictures are uploaded to that application.</summary>
        public const string NestmatesDiscordApp = "1565162114010841188";
        public static string DiscordApplicationId => string.IsNullOrWhiteSpace(_discordApp?.Value) ? NestmatesDiscordApp : _discordApp.Value.Trim();
        public static string DiscordImageKey => _discordImage?.Value ?? "";
        public static bool ShowJoinLeave { get => _joinToasts?.Value ?? true; set { if (_joinToasts != null) _joinToasts.Value = value; } }
        public static bool ShowOwnCatName { get => _ownCatTag?.Value ?? true; set { if (_ownCatTag != null) _ownCatTag.Value = value; } }
        public static bool HideJoinCodes { get => _hideCodes?.Value ?? true; set { if (_hideCodes != null) _hideCodes.Value = value; } }
        public static bool EmoteKeys { get => _emoteKeys?.Value ?? true; set { if (_emoteKeys != null) _emoteKeys.Value = value; } }
        public static bool DiscordJoin { get => _discordJoin?.Value ?? true; set { if (_discordJoin != null) _discordJoin.Value = value; } }
        public static bool SkipIntro { get => _skipIntro?.Value ?? true; set { if (_skipIntro != null) _skipIntro.Value = value; } }
        public static bool CardsToComputerOnly { get => _cardsLocal?.Value ?? false; set { if (_cardsLocal != null) _cardsLocal.Value = value; } }

        public static bool SharedRecordVolume { get => _sharedVolume?.Value ?? true; set { if (_sharedVolume != null) _sharedVolume.Value = value; } }
        public static bool SharedHover { get => _sharedHover?.Value ?? true; set { if (_sharedHover != null) _sharedHover.Value = value; } }
        public static bool SharedMapDrawings { get => _sharedDrawings?.Value ?? true; set { if (_sharedDrawings != null) _sharedDrawings.Value = value; } }
        public static int CounterBatteryTimerPercent { get => HostRules.NearestTimerChoice(_timerPct?.Value ?? 100); set { if (_timerPct != null) _timerPct.Value = HostRules.NearestTimerChoice(value); } }

        public static void Save()
        {
            if (_color == null) return;
            _hue.Value = PlayerInfo.Local.Color;
            _cat.Value = PlayerInfo.Local.CatEnabled;
        }
    }
}
