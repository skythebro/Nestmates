using System;
using System.Linq;
using System.Collections.Generic;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Steam;
using UnityEngine;

namespace IronNestCoop.UI
{
    /// <summary>
    /// Lobby window (F7). Host with one click, join friends from a live "friends hosting" list,
    /// invite through Steam or our own friend list, copy a lobby code, manage players.
    /// Drawn with <see cref="L"/> because GUILayout is stripped from this game.
    /// </summary>
    public static class CoopUI
    {
        enum Tab { Play, Players, Settings, Dev }

        const float PanelX = 16, PanelY = 16, PanelW = 470, Pad = 14;

        static bool _open = false;   // starts closed: F7, or the Multiplayer tab in the game's settings
        static Tab _tab;
        static float _scale = 1;

        /// <summary>Test hook: where each tab button was last drawn (screen pixels), and the index of the open tab.</summary>
        public static readonly Rect[] TabScreenRects = new Rect[Enum.GetValues(typeof(Tab)).Length];
        public static int TabIndex => (int)_tab;
        static string _code = "";
        static string _direct = "127.0.0.1:27731", _directKey = "";
        static bool _showDirect, _showInvite;
        static SteamLobby.Visibility _vis = SteamLobby.Visibility.FriendsOnly;
        static List<(ulong friend, string name, ulong lobby)> _hosting = new();
        static List<(ulong friend, string name)> _friends = new();
        static float _nextFriendsRefresh;
        static float _scroll, _contentH = 300;
        static string _toast; static float _toastUntil;

        public static bool IsOpen => _open;
        static float ScreenH = 1080;
        public static void Toggle() => _open = !_open;

        /// <summary>Test hook: opens the window on a tab (0 Play, 1 Players, 2 Settings).</summary>
        public static void Show(int tab) { _open = true; _tab = (Tab)tab; _scroll = 0; }
        static void Toast(string m) { _toast = m; _toastUntil = Time.unscaledTime + 4f; }

        public static void Draw()
        {
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F7 && !L.AnyFieldFocused) { _open = !_open; e.Use(); }
            Theme.Ensure();
            Pointer.Update();
            CursorRelease.Update(_open);
            CursorRelease.Keep();
            // Scale the whole UI with the screen height (designed for 1080p).
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1));
            L.SetScale(scale); _scale = scale;
            try { DrawScaled(); }
            finally
            {
                GUI.matrix = Matrix4x4.identity;
                if (Event.current != null && Event.current.type == EventType.Repaint)
                {
                    L.EndFrame();
                    if (Pointer.PressedThisFrame && _open)
                        CoopLog.Info($"[ui] click at {Pointer.Screen.x:F0},{Pointer.Screen.y:F0} (screen {Screen.width}x{Screen.height}, ui scale {scale:F2}, cursor {Cursor.lockState}/{(Cursor.visible ? "shown" : "hidden")}): {(Pointer.Consumed ? "hit a control" : "no control there")}");
                }
            }
        }

        static float _nextMenuCheck; static bool _menuWanted;

        static bool MenuButtonWanted()
        {
            if (Time.unscaledTime < _nextMenuCheck) return _menuWanted;
            _nextMenuCheck = Time.unscaledTime + 0.25f;
            var mm = MissionManager.Instance;
            bool mainMenu = mm != null && mm._CurrentPhase_k__BackingField == MissionManager.GamePhase.MainMenu;
            _menuWanted = (mainMenu && Cursor.visible) || Sync.NoPause.LocalMenuOpen;   // a locked cursor could not reach it
            return _menuWanted;
        }

        static void DrawScaled()
        {
            var e = Event.current;
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.75f, 2.5f);
            ScreenH = Screen.height / scale;
            Notify.Draw(Screen.width / scale);

            if (!_open)
            {
                L.ClearFocus();
                GUI.Label(new Rect(12, 6, 400, 22), Coop.InSession ? $"Nestmates: {Coop.Session.PlayerCount} players  ·  F7" : "Nestmates  ·  F7", Theme.Small);
                float y = PlayerHud.Draw() + 4;
                if (Sync.NoPause.AllPaused) { GUI.Label(new Rect(12, y, 420, 22), "Paused: every player has the menu open.", Theme.Small); y += 24; }
                Toasts.Draw(12, y);
                return;
            }

            float panelH = Mathf.Min(ScreenH - 32, 640);
            var panel = new Rect(PanelX, PanelY, PanelW, panelH);
            GUI.Box(panel, GUIContent.none, Theme.Window);

            float x = PanelX + Pad, w = PanelW - Pad * 2;
            L.Begin(x, PanelY + Pad, w);
            Header();
            Tabs();

            float footerH = FooterHeight(w);
            float viewTop = L.Cursor + 4;
            var view = new Rect(x, viewTop, w, panel.yMax - Pad - footerH - viewTop);
            L.BeginScroll(view, ref _scroll, _contentH);
            switch (_tab)
            {
                case Tab.Play: PlayTab(); break;
                case Tab.Players: PlayersTab(); break;
                case Tab.Settings: SettingsTab(); break;
                case Tab.Dev: if (Dev.DevTools.Enabled) DevTab(); break;
            }
            if (e.type == EventType.Repaint) _contentH = L.Used + 4;
            L.EndScroll(x, panel.yMax - Pad - footerH, w);
            if (_contentH > view.height)
                Theme.Fill(new Rect(view.xMax + 4, view.y + view.height * (_scroll / _contentH), 3, view.height * (view.height / _contentH)), Theme.AccentDim);
            Footer();
        }

        static void Header()
        {
            var row = L.BeginRow(26, 2);
            GUI.Label(row.Take(260), "NESTMATES", Theme.Title);
            if (L.Btn(row.TakeRight(30), "×", Theme.Button)) _open = false;
            GUI.Label(row.Rest(), "v" + Plugin.Version, Theme.Right);
            var (text, color) = StatusLine();
            var st = L.BeginRow(22, 6);
            Theme.Dot(st.Take(12), color, 10);
            GUI.Label(st.Rest(), text, Theme.LabelDim);
        }

        static (string, Color) StatusLine()
        {
            var s = Coop.Session;
            if (s == null)
            {
                if (Coop.Lobby != null) return ("Talking to Steam…", Theme.Accent);
                return ("Offline: single player", Theme.TextDim);
            }
            if (s.State == SessionState.Connecting) return ("Connecting to host…", Theme.Accent);
            string role = s.IsHost ? "Hosting" : "Connected";
            string ping = s.IsHost ? "" : $"  ·  {s.GetPlayer(0)?.RttMs:F0} ms";
            return ($"{role}  ·  {s.PlayerCount}/{Protocol.MaxPlayers} players{ping}", Theme.Good);
        }

        static void Tabs()
        {
            var row = L.BeginRow(28, 8);
            var tabs = ((Tab[])Enum.GetValues(typeof(Tab))).Where(t => t != Tab.Dev || Dev.DevTools.Enabled).ToArray();
            for (int i = 0; i < tabs.Length; i++)
            {
                var t = tabs[i];
                string label = t == Tab.Players && Coop.InSession ? $"Players ({Coop.Session.PlayerCount})" : t.ToString();
                var cell = row.Cell(i, tabs.Length);
                TabScreenRects[i] = new Rect(cell.position * _scale, cell.size * _scale);
                if (L.Btn(cell, label, _tab == t ? Theme.TabOn : Theme.Tab)) { _tab = t; _scroll = 0; }
            }
        }

        // ------------------------------------------------------------------ play

        static void PlayTab()
        {
            var s = Coop.Session;
            if (s == null && Coop.Lobby == null) { OfflinePlay(); return; }
            if (s == null || s.State == SessionState.Connecting)
            {
                L.Label("Connecting. This usually takes a few seconds.", Theme.Label);
                if (L.Button("Cancel", Theme.Button)) Coop.Leave();
                return;
            }
            InLobby(s);
        }

        static void OfflinePlay()
        {
            Section("Host a game");
            L.Label("Friends join your turret. You pick the missions, and progress is saved on your PC.", Theme.LabelDim);
            var vis = L.BeginRow(28);
            if (L.Btn(vis.Cell(0, 2), "Friends can join", _vis == SteamLobby.Visibility.FriendsOnly ? Theme.TabOn : Theme.Tab)) _vis = SteamLobby.Visibility.FriendsOnly;
            if (L.Btn(vis.Cell(1, 2), "Invite only", _vis == SteamLobby.Visibility.InviteOnly ? Theme.TabOn : Theme.Tab)) _vis = SteamLobby.Visibility.InviteOnly;
            GUI.enabled = SteamApi.Available;
            if (L.Button("Host lobby", Theme.ButtonAccent, 34)) Coop.HostSteam(_vis);
            GUI.enabled = true;
            if (!SteamApi.Available) L.Label("Waiting for Steam… " + SteamApi.Error, Theme.Small);

            L.Space(12);
            Section("Join a friend");
            RefreshFriends();
            if (_hosting.Count == 0) L.Label("No friends are hosting right now. Ask for an invite, or enter their lobby code.", Theme.LabelDim);
            foreach (var h in _hosting)
            {
                var r = L.BoxRow();
                if (L.Btn(r.TakeRight(90), "Join", Theme.ButtonAccent)) Coop.JoinSteam(h.lobby);
                GUI.Label(r.Rest(), h.name, Theme.Label);
            }
            L.Space(4);
            var code = L.BeginRow(32);
            bool join = L.Btn(code.TakeRight(110), "Join code", Theme.Button);
            _code = L.TextField(code.Rest(), "code", _code, 20, Theme.Field, "Lobby code, e.g. 3388F60A", Hidden);
            if (L.TakeSubmit("code")) join = true;
            if (join)
            {
                ulong id = SteamLobby.FromCode(_code);
                if (!SteamApi.Available) Toast("Steam is not ready yet.");
                else if (id == 0) Toast("That is not a lobby code (8 characters, 0-9 and A-F).");
                else Coop.JoinSteam(id);
            }

            L.Space(12);
            if (L.Button(_showDirect ? "▾  Direct connection (LAN / IP)" : "▸  Direct connection (LAN / IP)", Theme.Tab, 28)) _showDirect = !_showDirect;
            if (_showDirect)
            {
                L.Label("Without Steam: the host opens TCP port 27731 on their network and gets a join key. The others enter the host's address (host-ip:27731) and that key. Only for networks you trust: it is not hidden behind Steam.", Theme.Small);
                var d = L.BeginRow(32);
                if (L.Btn(d.TakeRight(70), "Host", Theme.Button)) Coop.HostTcp(27731, Coop.NewTcpKey());
                bool joinDirect = L.Btn(d.TakeRight(70), Coop.Connecting ? "…" : "Join", Theme.Button);
                _directKey = L.TextField(d.TakeRight(110), "directkey", _directKey, 12, Theme.Field, "Join key", Hidden);
                _direct = L.TextField(d.Rest(), "direct", _direct, 64, Theme.Field, null, Hidden);
                if (L.TakeSubmit("direct") || L.TakeSubmit("directkey")) joinDirect = true;
                if (joinDirect && !Coop.Connecting) Coop.JoinTcp(_direct, _directKey);
            }
        }

        static void InLobby(Session s)
        {
            var lobby = Coop.Lobby;
            if (lobby != null && lobby.Id != 0)
            {
                Section("Lobby code");
                var r = L.BoxRow(46);
                if (L.Btn(r.TakeRight(80), "Copy", Theme.Button)) { GUIUtility.systemCopyBuffer = lobby.Code; Toast("Lobby code copied."); }
                RevealButton(ref r);
                GUI.Label(r.Rest(), Hidden ? L.Mask(lobby.Code) : lobby.Code, Theme.Code);
                var inv = L.BeginRow(30);
                if (L.Btn(inv.Cell(0, 2), "Invite with Steam overlay", Theme.Button)) lobby.OpenInviteOverlay();
                if (L.Btn(inv.Cell(1, 2), _showInvite ? "Hide friends" : "Invite from list", Theme.Button)) _showInvite = !_showInvite;
                if (_showInvite) InviteList(lobby);
            }
            if (s.IsHost && Coop.IsTcp && Coop.TcpKey.Length > 0)
            {
                Section("Direct connection");
                var r = L.BoxRow(46);
                if (L.Btn(r.TakeRight(80), "Copy", Theme.Button)) { GUIUtility.systemCopyBuffer = Coop.TcpKey; Toast("Join key copied."); }
                RevealButton(ref r);
                GUI.Label(r.Rest(), Hidden ? L.Mask(Coop.TcpKey) : Coop.TcpKey, Theme.Code);
                L.Label("Others join with this key and one of these addresses: " + (Hidden ? "(hidden: Show)" : LanAddresses()), Theme.Small);
            }
            L.Space(8);
            Section("Mission");
            L.Label(MissionStatus.Describe(), Theme.Label);
            L.Label(s.IsHost ? "Start a mission from the map as usual and everyone comes along. Friends can join at any time."
                             : "The host picks the mission. You will be taken there automatically.", Theme.LabelDim);
            L.Space(8);
            Section("Crew");
            PlayerRows(s, compact: true);
            L.Space(8);
            if (L.Button(s.IsHost ? "Close lobby" : "Leave lobby", Theme.Button)) Coop.Leave();
        }

        // streamer mode: codes, keys and addresses as dots unless the player chose to see them (for 20 s, or
        // until the window closes: a code left on screen is how it ends up on a stream)
        static float _revealUntil;
        public static bool Hidden => Settings.HideJoinCodes && !(_open && Time.unscaledTime < _revealUntil);

        static void RevealButton(ref L.Row r)
        {
            if (!Settings.HideJoinCodes) return;
            if (L.Btn(r.TakeRight(70), Hidden ? "Show" : "Hide", Theme.Button)) _revealUntil = Hidden ? Time.unscaledTime + 20f : 0f;
        }

        static string _emoteList;
        /// <summary>"1 Wave · 2 Look here! · ..." for the settings.</summary>
        static string EmoteKeyList()
        {
            if (_emoteList != null) return _emoteList;
            var parts = new List<string>();
            for (int i = 1; i < Sync.AvatarModel.EmoteNames.Length; i++) parts.Add($"{i} {Sync.AvatarModel.EmoteNames[i]}");
            return _emoteList = string.Join("  ·  ", parts);
        }

        static string _lan;
        /// <summary>This PC's network addresses with the direct-connection port (looked up once).</summary>
        static string LanAddresses()
        {
            if (_lan != null) return _lan;
            var list = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a)) list.Add(a + ":27731");
            }
            catch { }
            return _lan = list.Count == 0 ? "(see your network settings):27731" : string.Join(", ", list);
        }

        static void InviteList(SteamLobby lobby)
        {
            RefreshFriends();
            if (_friends.Count == 0) L.Label("No friends online.", Theme.LabelDim);
            foreach (var f in _friends)
            {
                var r = L.BoxRow();
                if (L.Btn(r.TakeRight(80), "Invite", Theme.Button))
                    Toast(lobby.Invite(f.friend) ? $"Invite sent to {f.name}." : "Steam refused the invite.");
                GUI.Label(r.Rest(), f.name, Theme.Label);
            }
        }

        // ------------------------------------------------------------------ players

        static void PlayersTab()
        {
            var s = Coop.Session;
            if (s == null || !s.IsConnected) { L.Label("Not in a co-op game.", Theme.LabelDim); return; }
            PlayerRows(s, compact: false);
            if (s.IsHost) L.Label(Coop.IsTcp ? "Kick removes a player from this game. To keep someone out, host again: that gives a new join key."
                                             : "Kick removes a player from this game. Ban also keeps them out of your future games (list in BepInEx/config/IronNestCoop_bans.txt).", Theme.Small);
        }

        static void PlayerRows(Session s, bool compact)
        {
            foreach (var p in s.Players)
            {
                var info = PlayerInfo.Decode(p.IsLocal ? PlayerInfo.Encode() : p.Info);
                var r = L.BoxRow();
                Theme.Dot(r.Take(14), Theme.PlayerColor(info.Color));
                if (!compact && s.IsHost && !p.IsLocal)
                {
                    // a direct-connection guest picks their own id: a lasting ban would not hold, so only Kick
                    if (!Coop.IsTcp && L.Btn(r.TakeRight(56), "Ban", Theme.Button)) { Bans.Ban(p.PeerId, p.Name); s.Kick(p.Slot, "banned by the host", blockRejoin: true); }
                    if (L.Btn(r.TakeRight(60), "Kick", Theme.Button)) s.Kick(p.Slot, "kicked by the host", blockRejoin: true);
                }
                if (!p.IsLocal && (s.IsHost || p.Slot == 0)) GUI.Label(r.TakeRight(64), $"{p.RttMs:F0} ms", Theme.Right);
                string tag = p.Slot == 0 ? "  (host)" : "";
                if (p.IsLocal) tag += "  (you)";
                GUI.Label(r.Rest(), p.Name + tag, Theme.Label);
            }
        }

        // ------------------------------------------------------------------ dev (only with [Debug] DevTools = true)

        static void DevTab()
        {
            Section("Test shortcuts (Ctrl+Shift + key)");
            L.Label("For testing only. The first use backs up your saves and turns off achievements, stats and leaderboards until you restart the game.", Theme.Small);
            foreach (var a in Dev.DevTools.Actions)
                if (L.Button($"{a.Key}   {a.Label}", Theme.Tab, 26)) Dev.DevTools.Run(a);
            L.Space(8);
            Section("Start any mission");
            L.Label("Starts the mission now and guests follow. Starting it unlocks nothing, but finishing it counts as a normal finish.", Theme.Small);
            foreach (var (id, _) in Dev.DevTools.Missions())
                if (L.Button(id, Theme.Tab, 24)) Toast(Dev.DevTools.StartMission(id) ?? "");
        }

        // ------------------------------------------------------------------ settings

        /// <summary>The name of another player whose colour is (almost) this hue, or null.</summary>
        static string HueTakenBy(byte hue)
        {
            if (!Coop.InSession) return null;
            foreach (var p in Coop.Session.Players)
            {
                if (p == null || p.Slot == Coop.Session.LocalSlot) continue;
                if (Theme.HueDistance(PlayerInfo.Decode(p.Info).Color, hue) < Theme.HueClash) return p.Name;
            }
            return null;
        }

        static void SettingsTab()
        {
            Section("Your colour");
            // a hue strip: click anywhere along it; colours another player already has are marked and can't be taken
            const int Steps = Theme.HueSteps;
            var row = L.BeginRow(30);
            for (int i = 0; i < Steps; i++)
            {
                var cell = row.Cell(i, Steps);
                byte hue = Theme.HueCell(i);
                string by = HueTakenBy(hue);
                bool on = Theme.HueDistance(PlayerInfo.Local.Color, hue) <= 256 / Steps / 2;
                Theme.Fill(new Rect(cell.x + 1, cell.y + 2, cell.width - 2, cell.height - 4), Theme.PlayerColor(hue));
                if (L.Btn(cell, on ? "▲" : by != null ? "×" : "", Theme.HoverTint) && by == null)
                {
                    PlayerInfo.Local.Color = hue;
                    Settings.Save();
                    Coop.Session?.SetLocalInfo(Coop.LocalName, PlayerInfo.Encode());
                }
            }
            L.Label("× marks another player's colour. Yours shows on your figure, your map dot and in the crew list.", Theme.Small);
            L.Space(8);
            Section("Display");
            Settings.ShowNameTags = L.Toggle(Settings.ShowNameTags, "  Show name tags above players", Theme.Toggle);
            Settings.ShowHud = L.Toggle(Settings.ShowHud, "  Show the crew list while playing", Theme.Toggle);
            Settings.ShowMapPresence = L.Toggle(Settings.ShowMapPresence, "  Show where others work on the tactical map", Theme.Toggle);
            Settings.SkipIntro = L.Toggle(Settings.SkipIntro, "  Skip the game's intro when it starts", Theme.Toggle);
            Settings.ShowJoinLeave = L.Toggle(Settings.ShowJoinLeave, "  Show when players join or leave", Theme.Toggle);
            Settings.HideJoinCodes = L.Toggle(Settings.HideJoinCodes, "  Streamer mode: hide lobby codes and addresses", Theme.Toggle);
            Settings.EmoteKeys = L.Toggle(Settings.EmoteKeys, "  Emotes on keys 1-8", Theme.Toggle);
            if (Settings.EmoteKeys) L.Label("     " + EmoteKeyList(), Theme.Small);
            L.Space(8);
            Section("Status for friends");
            Settings.SteamStatus = L.Toggle(Settings.SteamStatus, "  Steam: show what I am doing (menu, mission, time played)", Theme.Toggle);
            Settings.DiscordStatus = L.Toggle(Settings.DiscordStatus, "  Discord: show what I am doing", Theme.Toggle);
            if (Settings.DiscordStatus) Settings.DiscordJoin = L.Toggle(Settings.DiscordJoin, "  Discord: friends can join me (Join button)", Theme.Toggle);
            if (Settings.SteamStatus || Settings.DiscordStatus) Settings.StatusStation = L.Toggle(Settings.StatusStation, "  Also show where I am in the turret", Theme.Toggle);
            if (Settings.SteamStatus && Steam.SteamPresence.Text.Length > 0) L.Label("Steam friends see: " + Steam.SteamPresence.Text, Theme.Small);
            if (Settings.DiscordStatus) L.Label(Steam.DiscordPresence.Text.Length > 0 ? $"Discord friends see: {Steam.DiscordPresence.Text}  ({Steam.DiscordPresence.State})" : "Discord: " + Steam.DiscordPresence.State, Theme.Small);
            L.Space(8);
            Section("Cat");
            Settings.ShowOwnCatName = L.Toggle(Settings.ShowOwnCatName, "  Show my cat's name above it", Theme.Toggle);
            bool cat = L.Toggle(PlayerInfo.Local.CatEnabled, "  Bring my own cat", Theme.Toggle);
            if (cat != PlayerInfo.Local.CatEnabled)
            {
                PlayerInfo.Local.CatEnabled = cat;
                Settings.Save();
                Coop.Session?.SetLocalInfo(Coop.LocalName, PlayerInfo.Encode());
            }
            L.Label("Every player can bring a cat. Yours looks the way you set it up in the game's cat settings.", Theme.Small);
            L.Space(8);
            Section("Host rules");
            if (Coop.IsHost || !Coop.InSession)
            {
                L.Label("Some things can be shared or kept separate for each player. Your choice applies to everyone in your game.", Theme.Small);
                Settings.CardsToComputerOnly = L.Toggle(Settings.CardsToComputerOnly, "  Fire-mission cards only for the computer operator", Theme.Toggle);
                L.Label("When on, the computer operator hands the cards to the gunners instead of everyone getting a copy.", Theme.Small);
                Settings.SharedRecordVolume = L.Toggle(Settings.SharedRecordVolume, "  One record player volume for everyone", Theme.Toggle);
                L.Label("When off, each player sets the record player's volume for themselves.", Theme.Small);
                Settings.SharedHover = L.Toggle(Settings.SharedHover, "  Show hover effects to everyone", Theme.Toggle);
                L.Label("When off, a lever that reacts to being looked at only moves for the player looking at it.", Theme.Small);
                Settings.SharedMapDrawings = L.Toggle(Settings.SharedMapDrawings, "  Share lines drawn on the tactical map", Theme.Toggle);
                L.Label("When off, everyone draws on their own map and the clear switch only removes your own lines.", Theme.Small);
                Settings.SharedMeasureNotes = L.Toggle(Settings.SharedMeasureNotes, "  Share map measurement notes", Theme.Toggle);
                L.Label("A finished measurement line writes its bearing and distance on everyone's notepad, not just the drawer's.", Theme.Small);
                L.Space(4);
                L.Label("Counter-battery timer", Theme.Label);
                var choices = HostRules.TimerChoices;
                var timerRow = L.BeginRow(26);
                for (int i = 0; i < choices.Length; i++)
                {
                    int pct = choices[i];
                    bool on = Settings.CounterBatteryTimerPercent == pct;
                    if (L.Btn(timerRow.Cell(i, choices.Length), TimerName(pct), on ? Theme.TabOn : Theme.Tab)) Settings.CounterBatteryTimerPercent = pct;
                }
                L.Label("More players can mean more chaos: give the crew more time, or less for a challenge. Off removes the timer.", Theme.Small);
            }
            else
            {
                L.Label(HostRules.Known ? "The host sets these. Ask them if you want one changed." : "Waiting for the host's rules…", Theme.Small);
                RuleLine(Rule.CardsToOperator, "Fire-mission cards only for the computer operator");
                RuleLine(Rule.SharedRecordVolume, "One record player volume for everyone");
                RuleLine(Rule.SharedHover, "Show hover effects to everyone");
                RuleLine(Rule.SharedMapDrawings, "Share lines drawn on the tactical map");
                RuleLine(Rule.SharedMeasureNotes, "Share map measurement notes");
                L.Label("     Counter-battery timer: " + (HostRules.TimerPercent == 0 ? "off" : TimerName(HostRules.TimerPercent)), Theme.Label);
            }
        }

        public static string TimerName(int pct) => pct switch
        {
            0 => "Off", 50 => "½×", 100 => "1×", 200 => "2×", 300 => "3×", _ => pct + "%",
        };

        static void RuleLine(Rule r, string text)
        {
            L.Label((HostRules.On(r) ? "✓  " : "✗  ") + text, Theme.Label);
        }

        // ------------------------------------------------------------------ footer and helpers

        static float FooterHeight(float w)
        {
            float h = 22;
            if (!string.IsNullOrEmpty(Coop.LastError)) h += Theme.Error.CalcHeight(new GUIContent(Coop.LastError), w - 16) + 18;
            if (_toast != null && Time.unscaledTime < _toastUntil) h += 22;
            return h;
        }

        static void Footer()
        {
            if (!string.IsNullOrEmpty(Coop.LastError))
            {
                float h = Theme.Error.CalcHeight(new GUIContent(Coop.LastError), L.Width - 16) + 12;
                var r = L.Next(h);
                GUI.Box(r, GUIContent.none, Theme.Row);
                GUI.Label(new Rect(r.x + 8, r.y + 6, r.width - 16, h - 12), Coop.LastError, Theme.Error);
            }
            if (_toast != null && Time.unscaledTime < _toastUntil) L.Label(_toast, Theme.Small);
            L.Label("F7 opens and closes this window.", Theme.Small);
        }

        static void Section(string title) => L.Label(title.ToUpperInvariant(), Theme.Section);

        static void RefreshFriends()
        {
            if (Time.unscaledTime < _nextFriendsRefresh) return;
            _nextFriendsRefresh = Time.unscaledTime + 3f;
            try
            {
                _hosting = SteamLobby.FriendsHosting();
                _friends = SteamLobby.OnlineFriends();
            }
            catch (Exception ex) { CoopLog.Warn("friend list: " + ex.Message); }
        }
    }
}
