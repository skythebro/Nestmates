using System.Collections.Generic;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.UI;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Other players as visible soldiers: body, head, helmet and gas-mask visor in their colour, a name
    /// tag, and their look direction. Each machine sends its own pose at 20 Hz; avatars interpolate.
    /// Avatars are visual only: no colliders, no player tag, so they never trigger zones or pickups.
    /// </summary>
    public sealed class Avatars : SyncModule
    {
        public override string Name => "avatars";
        public static Avatars Instance { get; private set; }

        const float SendInterval = 0.05f;

        sealed class Remote
        {
            public GameObject Root, Head, Body;
            public TextMeshPro Tag;
            public Renderer[] Tinted;
            public AvatarModel Model;   // the crew figure (see AvatarModel.cs): its parts and its motion
            public Vector3 Pos, TargetPos;
            public float Yaw, TargetYaw, Pitch, TargetPitch;
            public (Vector3 Pos, float Yaw)? MapStand;   // where they last stood while working on the map during this visit
            public int MapSide = -1;   // the table side they work from during this visit; they never swap sides mid-visit
            public bool Crouch;
            public string Console;
            public bool HasPoint; public Vector3 Point, ShownPoint;   // where their cursor is on the tactical map
            public bool EmoteKnown; public byte EmoteSeq;   // the last emote heard; a new number plays it
            public bool Operating, HasReach; public Vector3 Reach, ShownReach;   // holding a lever, wheel, slider and so on
            public GameObject Pip;
            public TextMeshPro PipName;
            public float LastHeard;
            public byte Color = 255;
        }

        readonly Dictionary<byte, Remote> _remotes = new();
        float _nextSend;
        FirstPersonController _fpc;
        Material _baseMat;

        public Avatars() { Instance = this; }

        float _nextHueCheck;

        void KeepHueUnique()
        {
            if (!Coop.InSession || Time.unscaledTime < _nextHueCheck) return;
            _nextHueCheck = Time.unscaledTime + 2f;
            var others = new List<byte>();
            bool clash = false;
            foreach (var p in Coop.Session.Players)
            {
                if (p == null || p.Slot == Coop.Session.LocalSlot) continue;
                byte h = PlayerInfo.Decode(p.Info).Color;
                others.Add(h);
                if (p.Slot < Coop.Session.LocalSlot && Theme.HueDistance(h, PlayerInfo.Local.Color) < Theme.HueClash) clash = true;
            }
            if (!clash) return;
            // pick the free cell of the colour strip nearest to ours, searching outwards
            byte mine = PlayerInfo.Local.Color, pick = mine;
            int at = Theme.NearestHueCell(mine);
            for (int step = 1; step <= Theme.HueSteps / 2; step++)
            {
                foreach (int cand in new[] { at + step, at - step })
                {
                    byte h = Theme.HueCell(cand);
                    bool free = true;
                    foreach (var o in others) if (Theme.HueDistance(o, h) < Theme.HueClash) { free = false; break; }
                    if (free) { pick = h; step = 999; break; }
                }
            }
            if (pick == mine) return;
            CoopLog.Info($"[avatars] colour {mine} is taken; using {pick}");
            PlayerInfo.Local.Color = pick;
            Settings.Save();
            Coop.Session.SetLocalInfo(Coop.LocalName, PlayerInfo.Encode());
        }

        public override void Register(Session s) => s.On((ushort)Msg.AvatarState, OnState);

        public override void OnSessionEnd() { foreach (var r in _remotes.Values) { r.Model?.Dispose(); Object.Destroy(r.Root); if (r.Pip != null) Object.Destroy(r.Pip); } _remotes.Clear(); }

        public override void OnPlayerLeft(Player p)
        {
            if (_remotes.TryGetValue(p.Slot, out var r)) { r.Model?.Dispose(); Object.Destroy(r.Root); if (r.Pip != null) Object.Destroy(r.Pip); _remotes.Remove(p.Slot); }
        }

        /// <summary>For the tests: reports whether any part of this slot's avatar is rendered by a camera right now, plus diagnostics.</summary>
        public bool IsVisible(byte slot, out string diag)
        {
            diag = "no avatar";
            if (!_remotes.TryGetValue(slot, out var r) || r.Root == null) return false;
            var cam = Camera.main;
            bool any = false;
            var sb = new System.Text.StringBuilder();
            sb.Append($"active={r.Root.activeInHierarchy} layer={r.Root.layer} cam={(cam != null ? cam.name : "none")} mask=0x{(cam != null ? cam.cullingMask : 0):X} ");
            if (cam != null)
            {
                var planes = GeometryUtility.CalculateFrustumPlanes(cam);
                sb.Append($"camPos={cam.transform.position:F2} camFwd={cam.transform.forward:F2} camEnabled={cam.enabled} near={cam.nearClipPlane} avatarPos={r.Root.transform.position:F2} ");
                foreach (var t in r.Tinted) sb.Append($"inFrustum({t.gameObject.name})={GeometryUtility.TestPlanesAABB(planes, t.bounds)} bounds={t.bounds.center:F2} ");
                int cams = Camera.allCamerasCount;
                sb.Append($"cameras={cams} ");
            }
            foreach (var t in r.Tinted)
            {
                any |= t.isVisible;
                sb.Append($"[{t.gameObject.name} vis={t.isVisible} en={t.enabled} sh={t.sharedMaterial?.shader?.name} layer={t.gameObject.layer}]");
            }
            diag = sb.ToString();
            return any;
        }

        /// <summary>Returns where a remote player's figure has its head, and which way it looks.</summary>
        public bool TryGetHead(byte slot, out Vector3 pos, out Quaternion rot)
        {
            pos = default; rot = Quaternion.identity;
            if (!_remotes.TryGetValue(slot, out var r) || r.Head == null) return false;
            pos = r.Head.transform.position; rot = r.Head.transform.rotation;
            return true;
        }

        /// <summary>For the tests: reports the remote figure's operating flag and where it stands relative to the map table.</summary>
        public bool TryGetActivity(byte slot, out bool operating, out bool atMap, out float fromTable)
        {
            operating = false; atMap = false; fromTable = -1;
            if (!_remotes.TryGetValue(slot, out var r) || r.Root == null) return false;
            operating = r.Operating; atMap = AtMap(r.Console);
            if (Table() is Bounds t) { var p = r.Root.transform.position; p.y = t.center.y; fromTable = Mathf.Sqrt(t.SqrDistance(p)); }
            return true;
        }

        /// <summary>For the tests: reports where the figure of a slot holds a carried cat (the cradle of its arms).</summary>
        public bool TryGetHoldPoint(byte slot, out Vector3 pos)
        {
            pos = default;
            return _remotes.TryGetValue(slot, out var r) && r.Model != null && r.Model.TryGetHoldPoint(out pos, out _);
        }

        public bool TryGetAvatar(byte slot, out Vector3 pos, out float yaw)
        {
            pos = default; yaw = 0;
            if (!_remotes.TryGetValue(slot, out var r) || r.Root == null) return false;
            pos = r.Root.transform.position; yaw = r.Yaw;
            return true;
        }

        FirstPersonController Local
        {
            get
            {
                if (_fpc == null && Time.unscaledTime >= _retryFpc) { _fpc = Object.FindObjectOfType<FirstPersonController>(); _retryFpc = Time.unscaledTime + 2f; }
                return _fpc;
            }
        }

        // ------------------------------------------------------------------ emotes

        static byte _emoteSeq, _emoteId;
        public static int EmotesSent, EmotesPlayed;   // counted for the tests
        public static int LastEmotePlayed;            // for the tests: the id of the last emote a remote figure played

        /// <summary>Plays emote <paramref name="id"/> (1-8) on this player's figure, as seen by the others.</summary>
        public static void PlayEmote(int id)
        {
            if (!Coop.InSession || id < 1 || id >= AvatarModel.EmoteNames.Length) return;
            if (!LaunchArgs.Mutant("noemote")) _emoteSeq++;   // test mutation: the number never changes, so nobody plays the emote
            _emoteId = (byte)id; EmotesSent++;
            if (Instance != null) Instance._nextSend = 0;   // send now, rather than waiting up to 50 ms
            UI.Toasts.Add("You: " + AvatarModel.EmoteNames[id], Theme.PlayerColor(PlayerInfo.Local.Color));
        }

        /// <summary>Handles the emote keys 1-8 (the game binds no digit keys). Ignored while typing, either in the co-op
        /// window or in a text field of the game, and ignored with Ctrl, Alt or Shift held (those are the dev shortcuts).</summary>
        static void EmoteKeys()
        {
            if (!Settings.EmoteKeys || !Coop.InSession || L.AnyFieldFocused || GameTextFocused()) return;
            var kb = Keyboard.current;
            if (kb == null || kb.ctrlKey.isPressed || kb.altKey.isPressed || kb.shiftKey.isPressed) return;
            for (int i = 1; i < AvatarModel.EmoteNames.Length && i <= 9; i++)
                if (kb[Key.Digit1 + (i - 1)].wasPressedThisFrame) { PlayEmote(i); return; }
        }

        static bool GameTextFocused()
        {
            var es = UnityEngine.EventSystems.EventSystem.current;
            var sel = es != null ? es.currentSelectedGameObject : null;
            if (sel == null) return false;
            return sel.GetComponent<TMP_InputField>() != null || sel.GetComponent<UnityEngine.UI.InputField>() != null;
        }

        // ------------------------------------------------------------------ what the hands are doing

        internal static bool TestOperating; internal static Vector3 TestReach;   // for the tests: acts as if holding something there
        static DynamicCursorManager _dcm;
        static float _retryDcm, _pressSince = -1;
        static Vector3 _heldAt; static bool _heldKnown;

        /// <summary>
        /// True while the local player holds the click on something: pulling a lever, turning a wheel or dial, or
        /// dragging a slider (taken from the game's own cursor manager: its press and its drag). A short click does
        /// not count, because a button press would make the figure twitch. <paramref name="reach"/> is the thing
        /// being held, when known.
        /// </summary>
        static bool Operating(out Vector3 reach, out bool hasReach)
        {
            reach = default; hasReach = false;
            if (TestOperating) { reach = TestReach; hasReach = true; return true; }
            if (_dcm == null && Time.unscaledTime >= _retryDcm) { _dcm = Object.FindObjectOfType<DynamicCursorManager>(); _retryDcm = Time.unscaledTime + 2f; }
            var m = _dcm;
            bool pressed = m != null && m._clickPressed;   // only this player's own button counts; a drag replayed for another player is not theirs
            Transform held = null;
            if (pressed)
            {
                var src = m._pressSourceForBroadcast;
                if (src == null) src = m._currentHover;
                if (src != null) held = src.transform;
            }
            if (!pressed || (held == null && !m.IsDragging && !_heldKnown)) { _pressSince = -1; _heldKnown = false; return false; }
            if (_pressSince < 0) _pressSince = Time.unscaledTime;
            if (held != null) { var p = held.position; if (float.IsFinite(p.x + p.y + p.z)) { _heldAt = p; _heldKnown = true; } }
            if (Time.unscaledTime - _pressSince < 0.15f) return false;
            reach = _heldAt; hasReach = _heldKnown;
            return true;
        }

        public override void Tick(float dt)
        {
            KeepHueUnique();
            EmoteKeys();
            if (Time.unscaledTime >= _nextSend) { _nextSend = Time.unscaledTime + SendInterval; SendPose(); }
            foreach (var kv in _remotes) Animate(kv.Key, kv.Value, dt);
        }

        void SendPose()
        {
            var f = Local;
            if (f == null) return;
            var w = Coop.Writer();
            var p = Feet(f);
            w.Vec3(p.x, p.y, p.z);
            // Body yaw (the controller turns its own transform) and camera pitch, as rendered.
            float yaw = f.transform.eulerAngles.y;
            float pitch = f.cameraRoot != null ? f.cameraRoot.localEulerAngles.x : 0;
            if (pitch > 180) pitch -= 360;
            w.F32(yaw); w.F32(pitch);
            w.Bool(f.isCrouched);
            var zone = CameraZoneTrigger.s_currentActiveZone;
            bool atConsole = zone != null && zone.consoleActive;
            w.Str(atConsole ? Util.ScenePath.Of(zone.transform) : null);   // where the zone sits tells us which station it is
            Vector3 mp = default;
            bool has = atConsole && MapPoint(out mp);
            if (TestPointOn && atConsole) { has = true; mp = TestPoint; }
            w.Bool(has);
            if (has) w.Vec3(mp.x, mp.y, mp.z);
            // Added later, so an older reader stops before this: the last emote and what the hands are doing. The
            // emote is sent every time so a lost packet does not lose it, and a new number plays it.
            w.U8(_emoteSeq); w.U8(_emoteId);
            bool op = Operating(out var reach, out bool hasReach);
            w.Bool(op); w.Bool(op && hasReach);
            if (op && hasReach) w.Vec3(reach.x, reach.y, reach.z);
            Coop.Send(Msg.AvatarState, w, Delivery.Unreliable);
        }

        internal static bool TestPointOn; internal static Vector3 TestPoint;   // for the tests: acts as if the cursor were on the map there

        static VirtualCursor _cursor;
        static float _retryCursor, _retryFpc;

        /// <summary>The point on the tactical map under the local player's cursor (the game's own cursor).</summary>
        static bool MapPoint(out Vector3 point)
        {
            point = default;
            var cam = Camera.main;
            if (cam == null) return false;
            if (_cursor == null && Time.unscaledTime >= _retryCursor) { _cursor = UnityEngine.Object.FindObjectOfType<VirtualCursor>(); _retryCursor = Time.unscaledTime + 2f; }
            Vector2 sp = _cursor != null ? _cursor.ScreenPosition : new Vector2(Screen.width / 2f, Screen.height / 2f);
            if (!Physics.Raycast(cam.ScreenPointToRay(new Vector3(sp.x, sp.y, 0)), out var hit, 20f)) return false;
            if (!(Util.ScenePath.Of(hit.collider.transform) ?? "").Contains("Tactical Map")) return false;
            point = hit.point;
            return true;
        }

        /// <summary>Maps stations to where their camera zone sits in the turret, because the zones themselves are all
        /// called "Camera Zone Collider ...". The first match wins.</summary>
        static readonly (string Part, string Label)[] Stations =
        {
            ("Tactical Map", "at the tactical map"),
            ("Artillery Computer", "at the ballistic computer"),
            ("Aiming Console/Camera Zone Collider Elevation", "at the elevation controls"),
            ("Aiming Console/Camera Zone Collider Rotation", "at the turret rotation wheel"),
            ("Aiming Console", "at the aiming controls"),
            ("Gun System Left", "at the left loader"),
            ("Gun System Right", "at the right loader"),
            ("Requisition Console", "at the requisition console"),
            ("EngineControls", "at the engine controls"),
            ("Trigger Console", "at fire control"),
            ("Record Player", "at the record player"),
            ("Expresso Machine", "at the coffee machine"),
            ("SettingsCamera", "at the settings board"),
            ("CatObjects", "with the cat"),
            ("Main Menu", "at the main menu"),
        };

        /// <summary>Returns a station description such as "at the tactical map", worked out from the scene path of the camera zone a player is using.</summary>
        internal static string ConsoleLabel(string zonePath)
        {
            if (string.IsNullOrEmpty(zonePath)) return null;
            foreach (var (part, label) in Stations)
                if (zonePath.IndexOf(part, System.StringComparison.OrdinalIgnoreCase) >= 0) return label;
            return "at a console";
        }

        static bool AtMap(string zonePath) => zonePath != null && zonePath.Contains("Tactical Map");

        static Bounds? _table; static int _tableScene = -1; static float _tableRetry;
        static float _tableTopY = float.NaN;   // world height of the map surface; NaN means not known
        internal static float TableTopY { get { Table(); return _tableTopY; } }
        // Which sides of the table have floor to stand on (left -x, right +x, back -z, front +z). Some sides run along
        // a stairwell or the engine bay, and a figure placed there used to stand in the air or inside a wall.
        static readonly bool[] _tableSides = { true, true, true, true };
        // How far from the table edge a figure stands: far enough that it can lean in and point without its body
        // being inside the table (the table top is about 1.2 m high).
        const float TableOut = 0.75f;

        static void FindTableSides(Bounds t)
        {
            int any = 0;
            for (int side = 0; side < 4; side++)
            {
                int floor = 0;
                for (int i = 0; i < 3; i++)
                {
                    float f = (i + 1) / 4f;
                    var p = side switch
                    {
                        0 => new Vector3(t.min.x - TableOut, 0, Mathf.Lerp(t.min.z, t.max.z, f)),
                        1 => new Vector3(t.max.x + TableOut, 0, Mathf.Lerp(t.min.z, t.max.z, f)),
                        2 => new Vector3(Mathf.Lerp(t.min.x, t.max.x, f), 0, t.min.z - TableOut),
                        _ => new Vector3(Mathf.Lerp(t.min.x, t.max.x, f), 0, t.max.z + TableOut),
                    };
                    p.y = t.max.y + 0.5f;
                    if (Physics.Raycast(p, Vector3.down, out var hit, t.size.y + 1.5f, ~0, QueryTriggerInteraction.Ignore) && Mathf.Abs(hit.point.y - t.min.y) < 0.2f) floor++;
                }
                _tableSides[side] = floor >= 2;
                if (_tableSides[side]) any++;
            }
            if (any == 0) for (int side = 0; side < 4; side++) _tableSides[side] = true;   // none found: standing somewhere is better than nowhere
            CoopLog.Debug($"[avatars] tactical map table sides with floor: left {_tableSides[0]}, right {_tableSides[1]}, back {_tableSides[2]}, front {_tableSides[3]}");
        }

        static int SceneSig()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        /// <summary>Returns the footprint of the tactical map table, cached per scene.</summary>
        internal static string TableSides => $"left {_tableSides[0]}, right {_tableSides[1]}, back {_tableSides[2]}, front {_tableSides[3]}";

        internal static Bounds? Table()
        {
            int sig = SceneSig();
            if (_tableScene == sig && (_table != null || Time.unscaledTime < _tableRetry)) return _table;   // already found, or searched recently
            _tableScene = sig; _table = null; _tableRetry = Time.unscaledTime + 5f;
            // Use the table's own collider if there is one. Its renderers are no use: the iris tool's skinned parts
            // report bounds tens of metres wide, which made the table room-sized and put figures inside the walls.
            // Otherwise fall back to the map surface.
            Bounds? surface = null;
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Collider>()))
            {
                var c = o.TryCast<Collider>();
                if (c == null || c.isTrigger) continue;
                string path = Util.ScenePath.Of(c.transform) ?? "";
                if (path.EndsWith("Tactical Map/Map table collider")) _table = c.bounds;
                if (path.EndsWith("Tactical Map/Draggable Surface")) surface = c.bounds;
            }
            if (_table == null && surface is Bounds sb) { sb.min = new Vector3(sb.min.x, sb.max.y - 0.92f, sb.min.z); _table = sb; }
            // The map itself lies higher than the table's collider (about 1.2 m above the floor), so hands and pointing go there.
            _tableTopY = surface is Bounds top ? top.max.y : _table is Bounds tt ? tt.max.y : float.NaN;
            if (_table is Bounds found) { CoopLog.Debug($"[avatars] tactical map table: centre {found.center:F2}, size {found.size:F2}"); FindTableSides(found); }
            return _table;
        }

        /// <summary>Returns the side of the table (that has floor) nearest to <paramref name="p"/>: 0 is left (-x), 1 right (+x),
        /// 2 back (-z) and 3 front (+z). Returns -1 when there is no table.</summary>
        internal static int NearestSide(Vector3 p)
        {
            if (Table() is not Bounds t) return -1;
            float best = float.MaxValue; int side = -1;
            for (int i = 0; i < 4; i++)
            {
                if (!_tableSides[i]) continue;
                float d = i switch { 0 => p.x - t.min.x, 1 => t.max.x - p.x, 2 => p.z - t.min.z, _ => t.max.z - p.z };
                if (d < best) { best = d; side = i; }
            }
            return side;
        }

        /// <summary>Returns where a figure stands on table side <paramref name="side"/> to work at <paramref name="point"/>:
        /// out from the edge and slid along it towards the point, but kept out of the corners.</summary>
        static Vector3 TableEdge(Vector3 point, int side)
        {
            if (Table() is not Bounds t || side < 0) return point;
            const float Corner = 0.3f;
            float x = Mathf.Clamp(point.x, t.min.x + Corner, t.max.x - Corner), z = Mathf.Clamp(point.z, t.min.z + Corner, t.max.z - Corner);
            if (t.size.x < 2 * Corner) x = t.center.x;
            if (t.size.z < 2 * Corner) z = t.center.z;
            // use the floor the table stands on, whatever the player is standing on (a step, the map view's rig)
            return side switch
            {
                0 => new Vector3(t.min.x - TableOut, t.min.y, z),
                1 => new Vector3(t.max.x + TableOut, t.min.y, z),
                2 => new Vector3(x, t.min.y, t.min.z - TableOut),
                _ => new Vector3(x, t.min.y, t.max.z + TableOut),
            };
        }

        /// <summary>Returns the direction straight across the table from side <paramref name="side"/>, or zero if there is no side.</summary>
        static Vector3 AcrossFrom(int side) => side switch { 0 => Vector3.right, 1 => Vector3.left, 2 => Vector3.forward, 3 => Vector3.back, _ => Vector3.zero };

        /// <summary>Returns the facing from table side <paramref name="side"/> towards <paramref name="point"/>, at most 50° off
        /// straight across the table, so the figure never twists round to point sideways.</summary>
        static float SideYaw(int side, Vector3 from, Vector3 point)
        {
            float straight = side switch { 0 => 90f, 1 => 270f, 2 => 0f, _ => 180f };
            var look = point - from; look.y = 0;
            if (look.sqrMagnitude < 1e-4f) return straight;
            float yaw = Quaternion.LookRotation(look).eulerAngles.y;
            return straight + Mathf.Clamp(Mathf.DeltaAngle(straight, yaw), -50f, 50f);
        }

        /// <summary>Returns the feet of the local player, which is the bottom of the CharacterController capsule.</summary>
        public static Vector3 Feet(FirstPersonController f)
        {
            var c = f.controller;
            if (c == null) return (f.mainGameObjectTransform != null ? f.mainGameObjectTransform : f.transform).position;
            var centre = c.transform.TransformPoint(c.center);
            return new Vector3(centre.x, c.bounds.min.y, centre.z);
        }

        void OnState(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot) return;
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            float yaw = r.F32(), pitch = r.F32();
            if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z) || !float.IsFinite(yaw) || !float.IsFinite(pitch)) return;   // ignore a broken pose from the network
            bool crouch = r.Bool();
            string console = r.Str(256);
            bool hasPoint = r.Bool();
            var point = hasPoint ? new Vector3(r.F32(), r.F32(), r.F32()) : default;
            if (float.IsNaN(point.x + point.y + point.z) || float.IsInfinity(point.x + point.y + point.z)) hasPoint = false;
            if (!_remotes.TryGetValue(c.FromSlot, out var a))
            {
                if (Time.unscaledTime < _retryCreate) return;   // the last build failed, so do not retry on every message
                a = Create(c.FromSlot);
                if (a == null) { _retryCreate = Time.unscaledTime + 10f; return; }
                a.Pos = pos; a.Yaw = yaw; a.Pitch = pitch;
                _remotes[c.FromSlot] = a;
            }
            if (r.Remaining >= 2)
            {
                byte seq = r.U8(), id = r.U8();
                // the first state of a figure only tells us where the count is, so we do not play an emote from minutes ago
                if (a.EmoteKnown && seq != a.EmoteSeq && id >= 1 && id < AvatarModel.EmoteNames.Length && a.Model != null)
                { a.Model.Emote(id); EmotesPlayed++; LastEmotePlayed = id; }
                a.EmoteSeq = seq; a.EmoteKnown = true;
            }
            bool op = false, hasReach = false; Vector3 reach = default;
            if (r.Remaining >= 2)
            {
                op = r.Bool(); hasReach = r.Bool();
                if (hasReach) { reach = new Vector3(r.F32(), r.F32(), r.F32()); if (!float.IsFinite(reach.x + reach.y + reach.z)) hasReach = false; }
            }
            if (hasReach && !a.HasReach) a.ShownReach = reach;
            a.Operating = op; a.HasReach = op && hasReach; a.Reach = reach;
            if (!AtMap(console)) { a.MapStand = null; a.MapSide = -1; }
            // Arriving at the map: keep the side of the table they came up to for the whole visit. A figure that
            // followed the pointer round to the nearest side used to jump across the table.
            else if (Settings.ShowMapPresence && a.MapSide < 0 && Table() is Bounds table)
            {
                a.MapSide = LaunchArgs.Mutant("mapsides") ? -1 : NearestSide(pos);
                int side = a.MapSide >= 0 ? a.MapSide : NearestSide(pos);
                var stand = TableEdge(pos, side);   // they are already at the table edge, facing across it and ready to point
                a.MapStand = (stand, SideYaw(side, stand, table.center));
            }
            a.Crouch = crouch; a.Console = console;
            if (hasPoint && !a.HasPoint) a.ShownPoint = point;
            a.HasPoint = hasPoint; a.Point = point;
            if (!hasPoint && a.MapStand is (Vector3 standPos, float standYaw) && Settings.ShowMapPresence)
            {
                // Still at the map, but the cursor is off it (on the clipboard or past the edge): stay where they stood
                // instead of jumping back to where they entered map view.
                a.TargetPos = standPos; a.TargetYaw = standYaw; a.TargetPitch = 35f;
            }
            else { a.TargetPos = pos; a.TargetYaw = yaw; a.TargetPitch = pitch; }
            // Working on the tactical map: stand at the table edge next to the pointer, facing it.
            if (hasPoint && Settings.ShowMapPresence)
            {
                int side = a.MapSide >= 0 ? a.MapSide : NearestSide(point);   // test mutation "mapsides": always use the side nearest to the pointer
                a.TargetPos = TableEdge(point, side);
                a.TargetYaw = SideYaw(side, a.TargetPos, point);
                a.TargetPitch = 35f;
                a.MapStand = (a.TargetPos, a.TargetYaw);
            }
            a.LastHeard = Time.unscaledTime;
        }

        void Animate(byte slot, Remote a, float dt)
        {
            if (a.Root == null) return;
            // Smooth exponentially toward the latest pose, but snap on teleports (respawn, scene change).
            if ((a.TargetPos - a.Pos).sqrMagnitude > 9f) a.Pos = a.TargetPos;
            float k = 1f - Mathf.Exp(-dt * 15f);
            a.Pos = Vector3.Lerp(a.Pos, a.TargetPos, k);
            a.Yaw = Mathf.LerpAngle(a.Yaw, a.TargetYaw, k);
            a.Pitch = Mathf.LerpAngle(a.Pitch, a.TargetPitch, k);
            a.Root.transform.position = a.Pos;
            a.Root.transform.rotation = Quaternion.Euler(0, a.Yaw, 0);
            a.Root.SetActive(Time.unscaledTime - a.LastHeard < 3f);
            UpdatePip(slot, a, k);
            if (a.Model != null)   // runs after the root has moved (the figure reads its speed and turning from it) and after the pip
            {
                if (a.HasReach) a.ShownReach = Vector3.Lerp(a.ShownReach, a.Reach, k);
                var pose = new AvatarModel.Pose { Pitch = a.Pitch, Crouch = a.Crouch, AtConsole = a.Console != null, AtMap = AtMap(a.Console), HasPoint = a.HasPoint && Settings.ShowMapPresence, Point = a.ShownPoint,
                                                  Operating = a.Operating, HasReach = a.HasReach, Reach = a.ShownReach,
                                                  Holding = Cats.Instance != null && Cats.Instance.IsHeld(slot),
                                                  TableTopY = AtMap(a.Console) ? TableTopY : float.NaN,
                                                  TableAcross = AcrossFrom(a.MapSide) };
                a.Model.Animate(dt, pose);
                // their carried cat lies in the figure's arms
                if (pose.Holding && a.Model.TryGetHoldPoint(out var holdPos, out var holdRot)) Cats.Instance.PlaceHeld(slot, holdPos, holdRot);
                if (a.Tag != null) a.Tag.transform.localPosition = new Vector3(0, a.Model.TagHeight, 0);   // the name tag follows a crouch
            }

            var p = Coop.Session.GetPlayer(slot);
            var info = PlayerInfo.Decode(p?.Info);
            if (info.Color != a.Color)
            {
                a.Color = info.Color;
                Tint(a, Theme.PlayerColor(info.Color));
                if (a.Pip != null) { var pr = a.Pip.GetComponent<Renderer>(); if (pr != null) pr.material.color = Theme.PlayerColor(info.Color); }
                if (a.PipName != null) a.PipName.color = Theme.PlayerColor(info.Color);
            }
            if (a.PipName != null && p != null && a.PipName.text != p.Name) a.PipName.text = p.Name;
            if (a.Tag != null)
            {
                a.Tag.gameObject.SetActive(Settings.ShowNameTags);
                string where = ConsoleLabel(a.Console);
                a.Tag.text = (p?.Name ?? "?") + (where != null ? $"\n<size=60%>{where}</size>" : "");
                var cam = Camera.main;
                if (cam != null) a.Tag.transform.rotation = Quaternion.LookRotation(a.Tag.transform.position - cam.transform.position);
            }
        }

        /// <summary>Creates the remote player's pointer on the tactical map: a small dot in their colour.</summary>
        void UpdatePip(byte slot, Remote a, float k)
        {
            bool show = a.HasPoint && Settings.ShowMapPresence && a.Root.activeSelf;
            if (!show) { if (a.Pip != null) a.Pip.SetActive(false); return; }
            if (a.Pip == null)
            {
                a.Pip = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                a.Pip.name = "Coop map pointer " + slot;
                var col = a.Pip.GetComponent<Collider>();
                if (col != null) UnityEngine.Object.Destroy(col);
                a.Pip.layer = 2;   // the Ignore Raycast layer
                a.Pip.transform.localScale = Vector3.one * 0.035f;
                UnityEngine.Object.DontDestroyOnLoad(a.Pip);
                var r = a.Pip.GetComponent<Renderer>();
                if (r != null) { r.material = new Material(BaseMaterial()); r.material.color = Theme.PlayerColor(a.Color); }
                // shows whose dot it is: their name above it, in their colour
                var nameGo = new GameObject("Name");
                nameGo.transform.SetParent(a.Pip.transform, false);
                nameGo.transform.localPosition = new Vector3(0, 1.6f, 0);   // measured in pip units (the pip is 3.5 cm)
                a.PipName = nameGo.AddComponent<TextMeshPro>();
                a.PipName.fontSize = 6f;
                a.PipName.alignment = TextAlignmentOptions.Center;
                a.PipName.color = Theme.PlayerColor(a.Color);
                a.PipName.outlineWidth = 0.25f;
                a.PipName.rectTransform.sizeDelta = new Vector2(30, 6);
                a.PipName.text = Coop.Session.GetPlayer(slot)?.Name ?? "";
            }
            if (a.PipName != null)
            {
                var cam = Camera.main;
                if (cam != null) a.PipName.transform.rotation = Quaternion.LookRotation(a.PipName.transform.position - cam.transform.position);
            }
            a.Pip.SetActive(true);
            a.ShownPoint = Vector3.Lerp(a.ShownPoint, a.Point, k);
            a.Pip.transform.position = a.ShownPoint + Vector3.up * 0.02f;
        }

        // ------------------------------------------------------------------ visuals

        Material BaseMaterial()
        {
            if (_baseMat != null) return _baseMat;
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh != null) { _baseMat = new Material(sh); return _baseMat; }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<MeshRenderer>()))
            {
                var mr = o.TryCast<MeshRenderer>();
                if (mr?.sharedMaterial != null && mr.sharedMaterial.HasProperty("_BaseColor")) { _baseMat = new Material(mr.sharedMaterial); return _baseMat; }
            }
            CoopLog.Warn("[avatars] no lit material found; avatars may render incorrectly");
            _baseMat = new Material(Shader.Find("Standard"));
            return _baseMat;
        }

        static void SetColor(Renderer r, Color c)
        {
            if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c);
            if (r.material.HasProperty("_Color")) r.material.SetColor("_Color", c);
        }

        static void Tint(Remote a, Color c) => a.Model?.SetColor(c);

        float _retryCreate;

        Remote Create(byte slot)
        {
            GameObject root = null;
            try
            {
                root = new GameObject($"IronNestCoop Avatar {slot}");
                Object.DontDestroyOnLoad(root);
                var a = new Remote { Root = root };
                a.Model = AvatarModel.Build(root.transform, Theme.PlayerColor(PlayerInfo.Decode(Coop.Session.GetPlayer(slot)?.Info).Color), BaseMaterial(), slot);
                a.Head = a.Model.Head; a.Body = a.Model.Body; a.Tinted = a.Model.Renderers;

                var tagGo = new GameObject("Name");
                tagGo.transform.SetParent(root.transform, false);
                tagGo.transform.localPosition = new Vector3(0, 2.05f, 0);
                a.Tag = tagGo.AddComponent<TextMeshPro>();
                a.Tag.fontSize = 2.2f;
                a.Tag.alignment = TextAlignmentOptions.Center;
                a.Tag.color = Theme.Text;
                a.Tag.outlineWidth = 0.2f;
                a.Tag.rectTransform.sizeDelta = new Vector2(4, 1);
                CoopLog.Info($"[avatars] created avatar for slot {slot}");
                return a;
            }
            catch (System.Exception e)
            {
                CoopLog.ErrorOnce("avatar-create", "[avatars] could not build avatar: " + e);
                if (root != null) Object.Destroy(root);   // do not leave anything half-built behind
                return null;
            }
        }
    }
}
