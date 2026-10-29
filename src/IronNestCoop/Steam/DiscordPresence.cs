using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// Shows the player's Discord status, built from <see cref="PresenceInfo"/> just like the Steam one. For example
    /// "Artillery Introduction" on the first line and "At the tactical map · hosting co-op (2 of 5)" on the second,
    /// with Discord's own live "elapsed" clock counting from the mission's start.
    ///
    /// Talks to the Discord app running on this PC through its local connection (the named pipe discord-ipc-N,
    /// Discord's documented RPC), so no SDK is needed and nothing is downloaded. Discord only shows a status for an
    /// application registered in its developer portal. That id is the ApplicationId setting, and leaving it empty
    /// turns the Discord status off. All pipe work runs on a background thread, so a slow or missing Discord
    /// never stalls a frame.
    /// </summary>
    public static class DiscordPresence
    {
        const int OpHandshake = 0, OpFrame = 1, OpClose = 2;
        const int IoTimeoutMs = 5000;

        /// <summary>Discord's replies are logged in shortened form, because they can carry the account's user data.</summary>
        static string Short(string s) => s == null ? "-" : System.Text.RegularExpressions.Regex.Replace(s.Length > 160 ? s.Substring(0, 160) + "..." : s, "\"user\":\\{[^}]*\\}", "\"user\":{..}");

        static float _next;
        static volatile string _want;        // the SET_ACTIVITY "activity" JSON; "null" clears the status
        static volatile string _wantId;      // the application id the status is meant for
        static volatile bool _quit;
        static Thread _worker;
        static readonly AutoResetEvent Wake = new(false);

        /// <summary>Connection state for the settings tab, such as "connected" or "Discord is not running". Set by the worker thread.</summary>
        public static string State { get; private set; } = "off";
        /// <summary>The two status lines as last worked out. Used by the tests and shown on the settings tab.</summary>
        public static string Text { get; private set; } = "";

        public static void Update(PresenceInfo info)
        {
            if (Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            string id = (Settings.DiscordApplicationId ?? "").Trim();
            bool on = Settings.DiscordStatus && IsId(id);
            var (details, state) = Lines(info);
            Text = on ? details + (state != null ? " / " + state : "") : "";
            // test copies run next to the developer's own Discord, so they must never set a status
            if (Sandbox.Active || LaunchArgs.Mutant("nopresence")) { State = "not in test copies"; return; }
            if (!Settings.DiscordStatus) State = "off";
            else if (!IsId(id)) State = "the ApplicationId setting is not a Discord application id";
            string want = on ? Activity(info, details, state) : "null", wantId = IsId(id) ? id : null;
            bool changed = want != _want || wantId != _wantId;
            _want = want; _wantId = wantId;
            if (on && _worker == null)
            {
                _worker = new Thread(Run) { IsBackground = true, Name = "Nestmates Discord" };
                _worker.Start();
            }
            if (changed) Wake.Set();
        }

        static bool IsId(string id) => id.Length >= 15 && id.Length <= 22 && ulong.TryParse(id, out _);

        /// <summary>Builds Discord's two lines: what the player is doing (the mission or menu) and the details (where, and with whom).</summary>
        public static (string Details, string State) Lines(PresenceInfo info)
        {
            string details = info.Headline;
            string how = info.InCoop ? (info.IsHost ? "Hosting co-op" : "In co-op") : info.Phase == PresenceInfo.Where.Mission ? "Playing alone" : null;
            string state = info.Station != null ? Capital(info.Station) + (how != null ? " · " + how.ToLowerInvariant() : "") : how;
            return (details, state);
        }

        static string Capital(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        public static string Activity(PresenceInfo info, string details, string state)
        {
            var sb = new StringBuilder("{");
            sb.Append("\"details\":").Append(Json(Fit(details)));
            if (state != null) sb.Append(",\"state\":").Append(Json(Fit(state)));
            long start = info.MissionStartUnix;
            if (start > 0) sb.Append(",\"timestamps\":{\"start\":").Append(start).Append('}');
            string image = (Settings.DiscordImageKey ?? "").Trim();
            if (image.Length > 0) sb.Append(",\"assets\":{\"large_image\":").Append(Json(Fit(image))).Append(",\"large_text\":\"Nestmates: online co-op for IRON NEST\"}");
            if (info.InCoop)
            {
                string party = info.GroupKey ?? "nestmates-direct";
                sb.Append(",\"party\":{\"id\":").Append(Json(party)).Append(",\"size\":[").Append(Math.Max(1, info.Players)).Append(',').Append(Net.Session.Protocol.MaxPlayers).Append("]}");
                // The Join button: Discord hands this secret to a friend who presses it, and only to them. It names the
                // Steam lobby to join, which still admits only the host's Steam friends. Not offered while the game is full.
                string secret = JoinSecret(info);
                if (secret != null) sb.Append(",\"secrets\":{\"join\":").Append(Json(secret)).Append('}');
            }
            sb.Append(",\"instance\":false}");
            return sb.ToString();
        }

        const string SecretPrefix = "nm1:";

        /// <summary>Returns the join secret for this game, or null when there is no Steam lobby, the game is full, or joining is off.</summary>
        public static string JoinSecret(PresenceInfo info)
            => Settings.DiscordJoin && info.InCoop && info.LobbyId != 0 && info.Players < Net.Session.Protocol.MaxPlayers ? SecretPrefix + info.LobbyId : null;

        /// <summary>Returns the lobby a join secret names, or 0 if it is not one of ours. The secret comes from Discord, so it is validated.</summary>
        public static ulong LobbyOf(string secret)
        {
            if (secret == null || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length > 40) return 0;
            return ulong.TryParse(secret.Substring(SecretPrefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : 0;
        }

        static long _joinLobby;   // written by the pipe thread, read on the main thread
        public static int JoinsReceived;   // counted for the tests

        /// <summary>Called every frame by CoopRunner. Carries out a Join from a Discord friend that arrived on the pipe.</summary>
        public static void TakeJoin()
        {
            ulong id = (ulong)Interlocked.Exchange(ref _joinLobby, 0);
            if (id == 0) return;
            CoopLog.Info("[discord] joining a friend's game from Discord");
            if (Coop.InSession) { UI.Toasts.Add("Leave this game first to join from Discord", UI.Theme.Text); return; }
            if (!SteamApi.Available) { UI.Toasts.Add("Steam is not ready: could not join from Discord", UI.Theme.Text); return; }
            if (Coop.Connecting) return;
            UI.Toasts.Add("Joining from Discord...", UI.Theme.Text);
            Coop.JoinSteam(id);
        }

        /// <summary>Handles a frame from Discord that no request is waiting for, such as a Join pressed by this player or a ping.</summary>
        internal static void OnEvent(string json)
        {
            if (json == null) return;
            // The answer to our SUBSCRIBE names the event too ("data":{"evt":"ACTIVITY_JOIN"}), so only a DISPATCH frame from Discord counts as an event
            if (json.Contains("\"cmd\":\"SUBSCRIBE\"")) { CoopLog.Info("[discord] Join button ready (Discord will pass on a friend's Join)"); return; }
            if (!json.Contains("\"cmd\":\"DISPATCH\"")) return;
            var evt = System.Text.RegularExpressions.Regex.Match(json, "\"evt\"\\s*:\\s*\"([A-Z_]{1,40})\"");
            CoopLog.Info("[discord] event " + (evt.Success ? evt.Groups[1].Value : "?"));   // log the name only, because the rest can carry account data
            if (!json.Contains("\"evt\":\"ACTIVITY_JOIN\"")) return;
            var m = System.Text.RegularExpressions.Regex.Match(json, "\"secret\"\\s*:\\s*\"([^\"]{1,64})\"");
            ulong id = m.Success ? LobbyOf(m.Groups[1].Value) : 0;
            if (id == 0) { CoopLog.Info("[discord] a Join from Discord that is not a Nestmates game: ignored"); return; }
            JoinsReceived++;
            Interlocked.Exchange(ref _joinLobby, (long)id);
        }

        /// <summary>Trims a line to the 2 to 128 characters Discord accepts.</summary>
        static string Fit(string s)
        {
            s ??= "";
            if (s.Length > 128) s = s.Substring(0, 127) + "…";
            return s.Length < 2 ? s.PadRight(2) : s;
        }

        static string Json(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Called when leaving the game. The status goes away with the connection, since Discord clears it when the pipe closes.</summary>
        public static void Shutdown()
        {
            if (_worker == null) return;
            _want = "null";
            _quit = true;
            Wake.Set();
            _worker.Join(1500);
        }

        // ------------------------------------------------------------------ the background thread

        const int OpPing = 3, OpPong = 4;

        static void Run()
        {
            NamedPipeClientStream pipe = null;
            System.Threading.Tasks.Task<int> pending = null;   // the pending read of the next frame's first byte
            var head = new byte[1];
            string connectedId = null, sent = null;
            var sentAt = DateTime.MinValue;
            var retryAt = DateTime.MinValue;   // no new connection attempt before this time (Discord was not running, or the last try failed)
            var buf = new byte[65536];
            while (true)
            {
                if (_quit && pipe == null) return;
                try
                {
                    string want = _want, id = _wantId;
                    if (pipe != null && (id != connectedId || !pipe.IsConnected)) { Close(ref pipe); sent = null; pending = null; }   // the app id changed, or Discord quit
                    if (want != null && want != "null" && id != null && pipe == null)
                    {
                        var wait = retryAt - DateTime.UtcNow;
                        if (wait > TimeSpan.Zero) { if (_quit) return; Wake.WaitOne(wait); continue; }
                        pipe = Connect(id, buf);
                        connectedId = pipe != null ? id : null;
                        if (pipe == null) { if (_quit) return; retryAt = DateTime.UtcNow.AddSeconds(15); continue; }
                        pending = null;
                        // a friend pressing Join on this player's status arrives as an event (read further below)
                        if (!LaunchArgs.Mutant("nodiscordjoin"))
                            Write(pipe, OpFrame, "{\"cmd\":\"SUBSCRIBE\",\"evt\":\"ACTIVITY_JOIN\",\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"}");
                    }
                    // Resend every 20 s even if unchanged. A broken pipe is only noticed when written to; without this, a
                    // restarted Discord would lose the status and nothing would notice while the text stayed the same.
                    bool stale = want != null && want != "null" && (DateTime.UtcNow - sentAt).TotalSeconds > 20;
                    if (pipe != null && want != null && (want != sent || stale))
                    {
                        string cmd = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + Environment.ProcessId + ",\"activity\":" + want + "},\"nonce\":\"" + Guid.NewGuid().ToString("N") + "\"}";
                        Write(pipe, OpFrame, cmd);
                        State = want == "null" ? "cleared" : "connected";   // a refusal comes back later as a frame (handled below)
                        sent = want; sentAt = DateTime.UtcNow;
                    }
                    // read whatever Discord sent: answers (a refusal is logged), pings, or a Join pressed by this player
                    while (pipe != null)
                    {
                        pending ??= pipe.ReadAsync(head, 0, 1);
                        if (!pending.IsCompleted) break;
                        if (pending.Result <= 0) throw new IOException("Discord closed the connection");
                        pending = null;
                        buf[0] = head[0];
                        string frame = ReadRest(pipe, buf, out int op);
                        if (op == OpClose) throw new IOException("Discord closed the connection: " + Short(frame));
                        if (op == OpPing) { Write(pipe, OpPong, frame ?? "{}"); continue; }
                        if (frame != null && frame.Contains("\"evt\":\"ERROR\"") && frame.Contains("\"cmd\":\"SUBSCRIBE\"")) { CoopLog.ErrorOnce("discord-subscribe", "[discord] the Join button is not available: " + Short(frame)); continue; }
                        if (frame != null && frame.Contains("\"evt\":\"ERROR\"")) { State = "Discord refused the status (see the log)"; CoopLog.ErrorOnce("discord-refused", "[discord] refused: " + Short(frame)); continue; }
                        OnEvent(frame);
                    }
                    if (_quit) { Close(ref pipe); return; }
                    if (want == "null" && sent == "null" && pipe != null) { Close(ref pipe); sent = null; State = Settings.DiscordStatus ? State : "off"; }
                }
                catch (Exception e)
                {
                    if (pipe != null) CoopLog.Info("[discord] connection lost (" + e.Message + "): reconnecting");
                    else CoopLog.Debug("[discord] " + e.Message);
                    State = "Discord connection lost (retrying)";
                    Close(ref pipe); sent = null; pending = null;
                    if (_quit) return;
                    retryAt = DateTime.UtcNow.AddSeconds(15);
                    continue;
                }
                // wait until there is something to send, or Discord sends something
                if (pipe != null && pending != null) WaitHandle.WaitAny(new[] { Wake, ((IAsyncResult)pending).AsyncWaitHandle }, 5000);
                else Wake.WaitOne(5000);
            }
        }

        /// <summary>Reads the rest of a frame whose first byte is already in buf[0].</summary>
        static string ReadRest(Stream s, byte[] buf, out int op)
        {
            var hdr = new byte[8];
            hdr[0] = buf[0];
            int got = 1;
            while (got < 8)
            {
                var t = s.ReadAsync(hdr, got, 8 - got);
                if (!t.Wait(IoTimeoutMs)) throw new IOException("Discord did not answer in time");
                if (t.Result <= 0) throw new IOException("Discord closed the connection");
                got += t.Result;
            }
            op = BitConverter.ToInt32(hdr, 0);
            int len = BitConverter.ToInt32(hdr, 4);
            if (len < 0 || len > buf.Length) throw new IOException("bad frame from Discord (" + len + " bytes)");
            ReadExactly(s, buf, len);
            return Encoding.UTF8.GetString(buf, 0, len);
        }

        static NamedPipeClientStream Connect(string appId, byte[] buf)
        {
            for (int i = 0; i < 10; i++)
            {
                var p = new NamedPipeClientStream(".", "discord-ipc-" + i, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { p.Connect(200); }
                catch (Exception) { p.Dispose(); continue; }
                try
                {
                    Write(p, OpHandshake, "{\"v\":1,\"client_id\":\"" + appId + "\"}");
                    string reply = Read(p, buf, out int op);
                    if (op == OpClose || reply == null || !reply.Contains("\"READY\""))
                    {
                        State = "Discord did not accept the application id";
                        CoopLog.ErrorOnce("discord-handshake", "[discord] handshake refused: " + Short(reply));
                        p.Dispose();
                        return null;
                    }
                    CoopLog.Info($"[discord] connected (discord-ipc-{i})");
                    return p;
                }
                catch (Exception e) { CoopLog.Debug("[discord] handshake: " + e.Message); p.Dispose(); }
            }
            State = "Discord is not running";
            return null;
        }

        static void Write(Stream s, int op, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            byte[] frame = new byte[8 + body.Length];
            BitConverter.GetBytes(op).CopyTo(frame, 0);
            BitConverter.GetBytes(body.Length).CopyTo(frame, 4);
            body.CopyTo(frame, 8);
            if (!s.WriteAsync(frame, 0, frame.Length).Wait(IoTimeoutMs)) throw new IOException("Discord did not take the message in time");
        }

        static string Read(Stream s, byte[] buf, out int op)
        {
            ReadExactly(s, buf, 8);
            op = BitConverter.ToInt32(buf, 0);
            int len = BitConverter.ToInt32(buf, 4);
            if (len < 0 || len > buf.Length) throw new IOException("bad frame from Discord (" + len + " bytes)");
            ReadExactly(s, buf, len);
            return Encoding.UTF8.GetString(buf, 0, len);
        }

        static void ReadExactly(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                var t = s.ReadAsync(buf, got, n - got);
                if (!t.Wait(IoTimeoutMs)) throw new IOException("Discord did not answer in time");
                int r = t.Result;
                if (r <= 0) throw new IOException("Discord closed the connection");
                got += r;
            }
        }

        static void Close(ref NamedPipeClientStream p)
        {
            if (p == null) return;
            try { p.Dispose(); } catch (Exception) { }
            p = null;
        }
    }
}
