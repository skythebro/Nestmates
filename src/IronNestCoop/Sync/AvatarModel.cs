using System;
using UnityEngine;
using G = IronNestCoop.Sync.AvatarGeometry;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// A remote player's crew figure: greatcoat, leather gas-mask hood with glowing goggles, a Brodie helmet and
    /// a scarf in the player's colour. Meshes and the four fixed materials are built once and shared by every
    /// figure; each figure owns one material (its colour). Purely visual: no colliders.
    /// <see cref="Animate"/> adds the life on top of the pose the caller already put on the parent (position and
    /// yaw): breathing, blinking, head pitch and turn lead, walk cycle from the parent's own motion, resting at a
    /// station, reaching for what they operate, leaning over and pointing at the map, cradling the cat
    /// (<see cref="TryGetHoldPoint"/>), eight emotes (<see cref="Emote"/>), a trailing scarf. No allocations per frame.
    /// </summary>
    public sealed class AvatarModel
    {
        /// <summary>Describes what the figure is doing, as received in the AvatarState message.</summary>
        public struct Pose
        {
            public float Pitch;      // camera pitch in degrees; positive looks down
            public bool Crouch;
            public bool AtConsole;   // in a station's camera view; the hands rest there unless Operating is set
            public bool AtMap;       // the station is the tactical map: leans over the table
            public bool HasPoint;    // their cursor is on the map ...
            public Vector3 Point;    // ... here (world): the right hand points at it
            public bool Operating;   // holding an interaction now (lever, wheel, dial, slider), anywhere
            public bool HasReach;    // where they hold it is known ...
            public Vector3 Reach;    // ... here (world): the hand(s) on that side reach for it
            public bool Holding;     // carrying the cat: both arms in a cradle, over every other arm pose
            public float TableTopY;  // at the map: world y of the table surface; NaN (or anything implausible) means 1.20 m above the feet
            public Vector3 TableAcross;   // at the map: horizontal direction across the table, away from the figure;
                                          // zero means straight ahead. The figure stands TableEdge (0.75 m) out from the edge.
        }

        public GameObject Root { get; private set; }
        /// <summary>The head (the centre of the hood). It turns with the look pitch.</summary>
        public GameObject Head { get; private set; }
        public GameObject Body { get; private set; }
        public Renderer[] Renderers { get; private set; }
        /// <summary>Height for a name tag above the helmet. It follows the crouch.</summary>
        public float TagHeight => Lerp(2.15f, 1.7f, _crouch);

        Transform _parent, _rig, _hips, _coat, _neck, _helmet, _eyes, _armL, _armR, _legL, _legR, _tail;
        Material _player;

        // per-figure personality, fixed for each figure
        System.Random _rng;
        float _breathPhase, _jaunt, _lookSeed;

        float _shrugSide;

        // smoothed state
        float _t, _walk, _crouch, _station, _map, _point, _operate, _reachW, _handL, _handR, _idle, _still;
        float _vf, _vl, _yawRate, _phase;
        float _lastX, _lastZ, _lastYaw; bool _hasLast;
        float _nextBlink, _blinkT = -1, _nextTilt, _tiltEnd, _tiltTarget, _tilt;
        int _emote; float _emoteStart;
        Vector3 _pointAt, _reach; float _pointHold;
        Quaternion _qNeck, _qArmL, _qArmR; bool _posed;
        float _tailX, _tailXv, _tailZ, _tailZv;
        // this frame's placement, used by ToHips
        float _wpX, _wpY, _wpZ, _sinYaw, _cosYaw, _bob, _hipY, _shoulderX, _shoulderY, _shoulderZ;
        Quaternion _qHips;
        // carrying the cat
        float _hold; Vector3 _holdPos; Quaternion _holdRot;
        // the map table in the figure's frame (smoothed)
        float _tableTop = DefaultTableTop, _acrossX, _acrossZ = 1;

        // ------------------------------------------------------------------ shared assets

        static Mesh[] s_meshes;
        static int[][] s_subMats;   // per piece: the material slot of each submesh
        static Material[] s_mats;   // indexed by material slot; the Player slot stays null because each figure owns its own

        static Mesh[] Meshes()
        {
            if (s_meshes != null && Array.TrueForAll(s_meshes, m => m != null)) return s_meshes;
            var parts = G.BuildAll();
            s_meshes = new Mesh[parts.Length];
            s_subMats = new int[parts.Length][];
            for (int i = 0; i < parts.Length; i++)
            {
                s_meshes[i] = ToMesh(parts[i]);
                s_subMats[i] = parts[i].Subs.ConvertAll(sub => sub.Material).ToArray();
            }
            return s_meshes;
        }

        static Mesh ToMesh(G.Part p)
        {
            var m = new Mesh { name = "Nestmates " + p.Name, hideFlags = HideFlags.DontUnloadUnusedAsset };
            var v = new Vector3[p.Pos.Count];
            var n = new Vector3[p.Pos.Count];
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = new Vector3(p.Pos[i].X, p.Pos[i].Y, p.Pos[i].Z);
                n[i] = new Vector3(p.Nrm[i].X, p.Nrm[i].Y, p.Nrm[i].Z);
            }
            m.vertices = v;
            m.normals = n;
            m.subMeshCount = p.Subs.Count;
            for (int s = 0; s < p.Subs.Count; s++) m.SetTriangles(p.Subs[s].Tris.ToArray(), s);
            m.RecalculateBounds();
            return m;
        }

        static Material MakeMaterial(Material baseMat, int slot)
        {
            var m = new Material(baseMat) { name = "Nestmates " + G.MaterialNames[slot] };
            // a material copied from the scene may carry its textures, but we want a plain colour only
            foreach (var tex in new[] { "_BaseMap", "_MainTex", "_BumpMap", "_MetallicGlossMap", "_OcclusionMap", "_EmissionMap", "_SpecGlossMap" })
                if (m.HasProperty(tex)) m.SetTexture(tex, null);
            foreach (var kw in new[] { "_NORMALMAP", "_METALLICSPECGLOSSMAP", "_OCCLUSIONMAP", "_SPECGLOSSMAP" }) m.DisableKeyword(kw);
            var c = G.Colors[slot];
            SetColor(m, new Color(c[0], c[1], c[2]));
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", G.Smoothness[slot]);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", G.Smoothness[slot]);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", G.Metallic[slot]);
            if (slot == G.Glow)
            {
                m.EnableKeyword("_EMISSION");
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", new Color(c[0], c[1], c[2]) * 1.4f);
            }
            else
            {
                m.DisableKeyword("_EMISSION");
                if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
            }
            return m;
        }

        static Material[] SharedMaterials(Material baseMat)
        {
            if (s_mats != null && s_mats[G.Coat] != null && s_mats[G.Leather] != null && s_mats[G.Brass] != null && s_mats[G.Glow] != null) return s_mats;
            s_mats = new Material[G.MaterialNames.Length];
            foreach (int slot in new[] { G.Coat, G.Leather, G.Brass, G.Glow })
            {
                s_mats[slot] = MakeMaterial(baseMat, slot);
                s_mats[slot].hideFlags = HideFlags.DontUnloadUnusedAsset;
            }
            return s_mats;
        }

        static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        // ------------------------------------------------------------------ build

        /// <summary>Builds the figure under <paramref name="parent"/> (feet at the parent's origin, facing its +z).
        /// <paramref name="seed"/> (the player's slot) picks the small quirks: helmet tilt, breathing and blink rhythm.</summary>
        public static AvatarModel Build(Transform parent, Color playerColor, Material baseMat, int seed)
        {
            var meshes = Meshes();
            var shared = SharedMaterials(baseMat);
            var a = new AvatarModel { _parent = parent, _rng = new System.Random(seed * 7919 + 17) };
            a._player = MakeMaterial(baseMat, G.Player);
            a.SetColor(playerColor);
            Material Mat(int slot) => slot == G.Player ? a._player : shared[slot];

            int layer = parent.gameObject.layer;
            var renderers = new System.Collections.Generic.List<Renderer>();
            Transform Node(string name, Transform under, Vector3 pos)
            {
                var go = new GameObject(name) { layer = layer };
                go.transform.SetParent(under, false);
                go.transform.localPosition = pos;
                return go.transform;
            }
            Transform Piece(string name, G.Piece piece, Transform under, Vector3 pos)
            {
                var t = Node(name, under, pos);
                var mesh = meshes[(int)piece];
                t.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = t.gameObject.AddComponent<MeshRenderer>();
                var slots = s_subMats[(int)piece];
                var mats = new Material[slots.Length];
                for (int s = 0; s < mats.Length; s++) mats[s] = Mat(slots[s]);
                mr.sharedMaterials = mats;
                if (piece == G.Piece.Eyes) mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderers.Add(mr);
                return t;
            }
            static Vector3 V(G.V3 v) => new(v.X, v.Y, v.Z);

            var root = Node("Nestmates figure", parent, Vector3.zero);
            a.Root = root.gameObject;
            a._rig = Node("Rig", root, Vector3.zero);
            a._legL = Piece("Leg L", G.Piece.Leg, a._rig, new Vector3(-G.HipX, G.HipY, 0));
            a._legR = Piece("Leg R", G.Piece.Leg, a._rig, new Vector3(G.HipX, G.HipY, 0));
            a._hips = Node("Hips", a._rig, new Vector3(0, G.HipY, 0));
            a._coat = Piece("Coat", G.Piece.Coat, a._hips, Vector3.zero);
            a._armL = Piece("Arm L", G.Piece.Arm, a._hips, new Vector3(-G.ShoulderX, G.ShoulderY, 0));
            a._armR = Piece("Arm R", G.Piece.Arm, a._hips, new Vector3(G.ShoulderX, G.ShoulderY, 0));
            a._tail = Piece("Scarf", G.Piece.Tail, a._hips, new Vector3(0, G.TailY, G.TailZ));
            a._neck = Node("Neck", a._hips, new Vector3(0, G.NeckY, 0));
            var head = Piece("Head", G.Piece.Head, a._neck, V(G.HeadOffset));
            head.localScale = Vector3.one * G.HeadScale;
            a._helmet = Piece("Helmet", G.Piece.Helmet, head, V(G.HelmetOffset));
            a._eyes = Piece("Eyes", G.Piece.Eyes, head, V(G.EyesOffset));
            a.Head = head.gameObject;
            a.Body = a._coat.gameObject;
            a.Renderers = renderers.ToArray();

            a._breathPhase = (float)a._rng.NextDouble() * 6.28f;
            a._lookSeed = (float)a._rng.NextDouble() * 50f;
            a._jaunt = ((float)a._rng.NextDouble() * 5f + 3f) * (a._rng.Next(2) == 0 ? -1 : 1);
            a._nextBlink = 1f + (float)a._rng.NextDouble() * 2f;
            a._nextTilt = 4f + (float)a._rng.NextDouble() * 4f;
            a._shrugSide = a._rng.Next(2) == 0 ? -1 : 1;
            a._emote = 1; a._emoteStart = 0.5f;   // the figure waves hello when it first appears
            a.Animate(0.02f, default);
            return a;
        }

        public void SetColor(Color c) { if (_player != null) SetColor(_player, c); }

        /// <summary>Frees the figure's own material. The caller destroys the objects along with the root.</summary>
        public void Dispose()
        {
            if (_player != null) UnityEngine.Object.Destroy(_player);
            _player = null;
        }

        // ------------------------------------------------------------------ emotes

        /// <summary>Short labels for a HUD hint, indexed by emote id (keys 1-8). Index 0 is unused.</summary>
        public static readonly string[] EmoteNames = { "", "Wave", "Look here!", "Thumbs up", "Salute", "Shrug", "Cheer", "Nod", "Shake head" };
        static readonly float[] EmoteLength = { 0, 1.8f, 1.7f, 1.6f, 1.6f, 1.6f, 2.0f, 1.3f, 1.4f };
        static readonly float[] EmoteIn = { 0, 0.25f, 0.2f, 0.25f, 0.15f, 0.3f, 0.2f, 0.15f, 0.15f };
        static readonly float[] EmoteOut = { 0, 0.35f, 0.35f, 0.35f, 0.25f, 0.4f, 0.4f, 0.25f, 0.25f };

        /// <summary>Plays emote <paramref name="id"/> (1..8, see <see cref="EmoteNames"/>); anything else is ignored.
        /// A new emote replaces a running one; the final pose is smoothed, so the switch never snaps.</summary>
        public void Emote(int id)
        {
            if (id < 1 || id >= EmoteNames.Length) return;
            _emote = id;
            _emoteStart = _t;
        }

        /// <summary>True while an emote is playing.</summary>
        public bool Emoting => _emote != 0 && _t >= _emoteStart && _t < _emoteStart + EmoteLength[_emote];

        /// <summary>The hello wave (emote 1), which also plays once when the figure arrives.</summary>
        public void Wave() => Emote(1);

        // ------------------------------------------------------------------ the map table

        /// <summary>Measured in the game: the map surface is 1.20 m above the floor, and the figure stands 0.75 m from the edge.</summary>
        public const float DefaultTableTop = 1.20f, TableEdge = 0.75f;
        /// <summary>The mitten rests this far past the edge, its centre a mitten radius above the top.</summary>
        const float RailPast = 0.03f, MittenRadius = 0.066f;
        /// <summary>The shoulders come this far forward at the map (a reach over the rail).</summary>
        const float MapHunch = 0.06f;
        /// <summary>Aim length for a hand on the rail. It is a little under the arm length, so the mitten presses on the rail rather than floating.</summary>
        const float RailReach = 0.49f;

        /// <summary>Returns the hip lean (in degrees, all terms together) that brings the hand on <paramref name="side"/> to
        /// the rail point nearest that shoulder. It is solved by bisection between 4 and 24 degrees.</summary>
        float MapLeanNeeded(float side)
        {
            float qx = side * _shoulderX, qz = _shoulderZ;
            float k = TableEdge + RailPast - (qx * _acrossX + qz * _acrossZ);
            float tx = qx + _acrossX * k, tz = qz + _acrossZ * k, ty = _tableTop + MittenRadius;
            float lo = 4f, hi = 24f;
            if (RailDistance(lo, side, tx, ty, tz) <= RailReach) return lo;
            if (RailDistance(hi, side, tx, ty, tz) > RailReach)
            {
                // Out of reach (crouched, or the rail is off to the side): use the lean that gets closest, and no more.
                for (int i = 0; i < 12; i++)
                {
                    float m1 = lo + (hi - lo) / 3f, m2 = hi - (hi - lo) / 3f;
                    if (RailDistance(m1, side, tx, ty, tz) > RailDistance(m2, side, tx, ty, tz)) lo = m1; else hi = m2;
                }
                float best = (lo + hi) * 0.5f;
                return RailDistance(best, side, tx, ty, tz) > RailReach + 0.06f ? 10f : best;   // far off (crouched), so do not stoop for nothing
            }
            for (int i = 0; i < 10; i++)
            {
                float mid = (lo + hi) * 0.5f;
                if (RailDistance(mid, side, tx, ty, tz) > RailReach) lo = mid; else hi = mid;
            }
            return hi;
        }

        float RailDistance(float leanDeg, float side, float tx, float ty, float tz)
        {
            float c = MathF.Cos(leanDeg * Deg), sn = MathF.Sin(leanDeg * Deg);
            float sx = side * _shoulderX, sy = _hipY + _bob + _shoulderY * c - _shoulderZ * sn, sz = _shoulderY * sn + _shoulderZ * c;
            float dx = tx - sx, dy = ty - sy, dz = tz - sz;
            return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Returns the arm rotation that rests the hand on <paramref name="side"/> on the rail, and how well it
        /// reaches (1 means on the rail, 0 means too far, in which case the caller keeps a relaxed arm).</summary>
        Quaternion RailAim(float side, out float reach)
        {
            var sh = Rotate(_qHips, V(side * _shoulderX, _shoulderY, _shoulderZ));
            float shy = sh.y + _hipY + _bob;
            float k = TableEdge + RailPast - (sh.x * _acrossX + sh.z * _acrossZ);
            float tx = sh.x + _acrossX * k, tz = sh.z + _acrossZ * k, ty = _tableTop + MittenRadius;
            float dx = tx - sh.x, dy = ty - shy, dz = tz - sh.z;
            reach = Clamp((0.58f - MathF.Sqrt(dx * dx + dy * dy + dz * dz)) / 0.08f, 0, 1);
            var q = Aim(FigToHips(V(tx, ty, tz)), side, out bool ok);
            if (!ok) reach = 0;
            return q;
        }

        // ------------------------------------------------------------------ holding the cat

        /// <summary>Hand targets of the cradle, measured from the shoulder in the hips' frame (right arm; the left mirrors x),
        /// and how far the shoulders come forward. Checked against the coat: the forearms are 22 cm apart, so a kitten
        /// (27 cm) bridges them and a grown cat (45 cm) drapes over them, and the cat clears the chest by about 11 cm.</summary>
        const float CradleX = -0.24f, CradleY = -0.26f, CradleZ = 0.43f, CradleShoulderX = 0.01f, CradleShoulderZ = 0.06f;
        /// <summary>The cat rests on the forearms this far along the arm (the wrist end of the sleeve) ...</summary>
        const float CradleRest = 0.88f;
        /// <summary>... and this far on top of the sleeve.</summary>
        const float SleeveTop = 0.072f;

        /// <summary>Where a carried cat lies, in world space, while <see cref="Pose.Holding"/> is mostly blended in
        /// (also during the first moments of putting it down). <paramref name="pos"/> is the resting point on top
        /// of the forearms, centred between the hands: the cat's underside goes here. <paramref name="rot"/> lays
        /// the cat across the arms: its forward is the figure's right, its up is world up. Valid after
        /// <see cref="Animate"/> this frame.</summary>
        public bool TryGetHoldPoint(out Vector3 pos, out Quaternion rot)
        {
            pos = _holdPos; rot = _holdRot;
            return _hold > 0.6f && _rig != null;
        }

        // ------------------------------------------------------------------ animate

        const float Deg = MathF.PI / 180f, Stride = 1.1f;

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
        static float Smooth(float x) { x = Clamp(x, 0, 1); return x * x * (3 - 2 * x); }
        static float Approach(float x, float target, float dt, float rate) => x + (target - x) * (1f - MathF.Exp(-rate * dt));
        static float DeltaAngle(float from, float to) { float d = (to - from) % 360f; if (d > 180) d -= 360; if (d < -180) d += 360; return d; }
        float Rand(float lo, float hi) => lo + (float)_rng.NextDouble() * (hi - lo);

        /// <summary>Converts a world point into the figure's own frame (feet at the origin, +z in the facing direction).</summary>
        Vector3 ToFigure(Vector3 w)
        {
            float px = w.x - _wpX, py = w.y - _wpY, pz = w.z - _wpZ;
            return V(px * _cosYaw - pz * _sinYaw, py, px * _sinYaw + pz * _cosYaw);
        }

        /// <summary>Converts a point in the figure's frame into the hips' frame, using this frame's placement.</summary>
        Vector3 FigToHips(Vector3 f) => Rotate(Conj(_qHips), V(f.x, f.y - _bob - _hipY, f.z));

        /// <summary>Converts a world point into the hips' frame, using this frame's placement.</summary>
        Vector3 ToHips(Vector3 w) => FigToHips(ToFigure(w));

        /// <summary>Returns the arm rotation that aims the hand from the shoulder at <paramref name="side"/> (+1 right, -1 left)
        /// towards a point in the hips' frame. <paramref name="ok"/> is false when the point sits on the shoulder.</summary>
        Quaternion Aim(Vector3 target, float side, out bool ok)
        {
            var d = V(target.x - side * _shoulderX, target.y - _shoulderY, target.z - _shoulderZ);
            ok = d.x * d.x + d.y * d.y + d.z * d.z > 1e-4f;
            return ok ? FromDown(d) : Q(0, 0, 0, 1);
        }

        /// <summary>Call this every frame after placing the parent. <paramref name="dt"/> is in seconds.</summary>
        public void Animate(float dt, in Pose pose)
        {
            if (_rig == null || _parent == null) return;   // it was destroyed along with its parent
            if (dt <= 0) return;
            if (dt > 0.1f) dt = 0.1f;
            _t += dt;

            // the parent's motion in its own frame (forward and sideways), and how fast it turns
            var wp = _parent.position;
            float yaw = _parent.eulerAngles.y;
            float dx = wp.x - _lastX, dz = wp.z - _lastZ, dyaw = DeltaAngle(_lastYaw, yaw);
            if (!_hasLast || dx * dx + dz * dz > 4f) { dx = dz = 0; dyaw = 0; }   // first frame, or a teleport
            _lastX = wp.x; _lastZ = wp.z; _lastYaw = yaw; _hasLast = true;
            _wpX = wp.x; _wpY = wp.y; _wpZ = wp.z;
            _sinYaw = MathF.Sin(yaw * Deg); _cosYaw = MathF.Cos(yaw * Deg);
            float k = 1f - MathF.Exp(-dt * 8f);
            _vf += ((dx * _sinYaw + dz * _cosYaw) / dt - _vf) * k;
            _vl += ((dx * _cosYaw - dz * _sinYaw) / dt - _vl) * k;
            _yawRate += (Clamp(dyaw / dt, -400, 400) - _yawRate) * k;
            float speed = MathF.Sqrt(_vf * _vf + _vl * _vl);

            // what they are doing
            bool atMap = pose.AtMap, operating = pose.Operating;
            bool station = pose.AtConsole && !atMap && !operating;
            // The map pointer comes and goes as the cursor crosses the table edge, so keep the last point for a moment.
            if (atMap && pose.HasPoint) { _pointAt = pose.Point; _pointHold = 0.6f; }
            else _pointHold -= dt;
            bool pointing = atMap && _pointHold > 0;
            bool reaching = operating && pose.HasReach;
            if (reaching)
            {
                float jx = pose.Reach.x - _reach.x, jy = pose.Reach.y - _reach.y, jz = pose.Reach.z - _reach.z;
                if (_reachW < 0.05f || jx * jx + jy * jy + jz * jz > 2.25f) _reach = pose.Reach;   // a new grip, so start there
                else
                {
                    float kr = 1f - MathF.Exp(-dt * 12f);
                    _reach = V(_reach.x + jx * kr, _reach.y + jy * kr, _reach.z + jz * kr);
                }
            }
            _walk = Approach(_walk, Clamp((speed - 0.15f) / 1.6f, 0, 1), dt, 6);
            _crouch = Approach(_crouch, pose.Crouch ? 1 : 0, dt, 10);
            _station = Approach(_station, station ? 1 : 0, dt, 4);
            _map = Approach(_map, atMap ? 1 : 0, dt, 5);
            _point = Approach(_point, pointing ? 1 : 0, dt, pointing ? 7 : 4);
            _operate = Approach(_operate, operating ? 1 : 0, dt, 9);
            _reachW = Approach(_reachW, reaching ? 1 : 0, dt, 9);
            _hold = Approach(_hold, pose.Holding ? 1 : 0, dt, 14);   // takes about 0.25 s in and out
            _still = speed < 0.2f && !pose.AtConsole && !atMap && !operating ? _still + dt : 0;
            _idle = Approach(_idle, _still > 5f ? 1 : 0, dt, 1.2f);

            _phase += dt * speed / Stride * MathF.PI;   // one leg swing per stride
            if (_phase > 2 * MathF.PI) _phase -= 2 * MathF.PI;
            float sw = MathF.Sin(_phase);
            float breath = MathF.Sin(_t * (2 * MathF.PI / 3.4f) + _breathPhase);
            float busy = MathF.Max(_operate, _map), work = MathF.Max(busy, 0.4f * _station);

            // emote: an eased envelope, and what it adds to the body and head
            int e = _emote;
            float te = _t - _emoteStart, em = 0;
            if (e != 0)
            {
                if (te >= EmoteLength[e]) { _emote = e = 0; }
                else if (te >= 0) em = Smooth(te / EmoteIn[e]) * Smooth((EmoteLength[e] - te) / EmoteOut[e]);
            }
            float emLean = 0, emHop = 0, emPitch = 0, emYaw = 0, emRoll = 0, emLook = 0, emLookPitch = 0, emShrug = 0, emEyes = 1;
            switch (em > 0 ? e : 0)
            {
                case 1: emRoll = -8f * em; break;                                                     // wave: the head tips towards the hand
                case 2: emLean = 4f * em; emLook = 0.8f * em; emLookPitch = -6f; emYaw = 10f * em; break;   // look here: leans in and looks along
                case 3: emRoll = 7f * em; emPitch = 5f * MathF.Sin(te * 7f) * em; break;           // thumbs up: a cheeky head tilt
                case 4: emLean = -4f * em; emLook = em; emLookPitch = -4f; break;                  // salute: stands up straight
                case 5: emShrug = em; emRoll = 12f * _shrugSide * em; emLook = 0.5f * em; emLookPitch = -6f; emLean = -3f * em; break;
                case 6: emHop = 0.07f * MathF.Abs(MathF.Sin(te * MathF.PI * 1.8f)) * em * (1f - 0.6f * _crouch); emLook = 0.7f * em; emLookPitch = -14f; emEyes = 1.15f; break;
                case 7: emPitch = 14f * MathF.Sin(te * 2 * MathF.PI * 1.7f) * em; break;          // nod yes
                case 8: emYaw = 24f * MathF.Sin(te * 2 * MathF.PI * 2.1f) * em; emEyes = Lerp(1f, 0.8f, em); break;   // shake no
            }

            // body: crouch squash, breathing, bob and waddle, lean into the walk, the look, the map and the work
            _hipY = Lerp(G.HipY, G.HipYCrouch, _crouch);
            float sy = Lerp(1f, 0.76f, _crouch) * (1f + 0.012f * breath);
            _bob = (MathF.Abs(MathF.Cos(_phase)) - 1f) * 0.04f * _walk + emHop;
            _rig.localPosition = V(0, _bob, 0);
            _hips.localPosition = V(0, _hipY, 0);
            // shoulders: shrug only with the arms free; a little hunch forward to hold the cat or reach the map rail
            _shoulderX = G.ShoulderX + CradleShoulderX * _hold;
            _shoulderY = G.ShoulderY * sy + 0.05f * emShrug * (1f - _hold);
            _shoulderZ = CradleShoulderZ * _hold + MapHunch * _map * (1f - _hold);
            float jabZ = e == 2 ? 0.05f * MathF.Max(0, MathF.Sin(te * 12f)) * MathF.Exp(-te * 2f) * em * (1f - _hold) : 0;   // "there!"
            _armL.localPosition = V(-_shoulderX, _shoulderY, _shoulderZ);   // the arms ride on the squashed shoulders
            _armR.localPosition = V(_shoulderX, _shoulderY, _shoulderZ + jabZ);

            // the map table in the figure's frame: its top above the feet, and which way is across
            if (atMap)
            {
                float top = pose.TableTopY - wp.y;
                if (!(top > 0.6f && top < 2.0f)) top = DefaultTableTop;   // the value is NaN, unknown or implausible (a default Pose has 0)
                float ax = 0, az = 1, wx = pose.TableAcross.x, wz = pose.TableAcross.z, wl = MathF.Sqrt(wx * wx + wz * wz);
                if (wl > 0.1f) { ax = (wx * _cosYaw - wz * _sinYaw) / wl; az = (wx * _sinYaw + wz * _cosYaw) / wl; }
                if (_map < 0.05f) { _tableTop = top; _acrossX = ax; _acrossZ = az; }
                else
                {
                    float kt = 1f - MathF.Exp(-dt * 6f);
                    _tableTop += (top - _tableTop) * kt;
                    _acrossX += (ax - _acrossX) * kt; _acrossZ += (az - _acrossZ) * kt;
                    float al = MathF.Sqrt(_acrossX * _acrossX + _acrossZ * _acrossZ);
                    if (al > 1e-3f) { _acrossX /= al; _acrossZ /= al; }
                }
            }

            float lean = Clamp(_vf / 2.5f, -1, 1) * 8f * _walk + Clamp(pose.Pitch, -60, 70) * 0.12f + 6f * _crouch
                + 4f * _operate * _reachW + emLean;
            // at the map: lean just enough for the resting hand (the left one while the right points) to reach the rail
            if (_map > 0.001f) lean += Clamp(MapLeanNeeded(_point > 0.5f ? -1 : 1) - lean, 0, 20f) * _map;
            // turn the chest a little towards what they point at
            float twist = e == 2 ? 8f * em : 0;
            if (_point > 0.001f)
            {
                var pf = ToFigure(_pointAt);
                twist += Clamp(MathF.Atan2(pf.x, pf.z) / Deg * 0.3f, -15f, 15f) * _point;
            }
            float roll = sw * 3.5f * _walk - Clamp(_yawRate * 0.02f, -6, 6);
            _qHips = Euler(lean, twist, roll);
            _hips.localRotation = _qHips;
            _coat.localScale = V(1, sy, 1);
            _neck.localPosition = V(0, G.NeckY * sy, 0);
            _tail.localPosition = V(0, G.TailY * sy, G.TailZ);

            // legs: swing on the walk, shorten in the crouch
            float legScale = _hipY / G.HipY, legSwing = sw * 26f * _walk;
            _legL.localPosition = V(-G.HipX, _hipY, 0);
            _legR.localPosition = V(G.HipX, _hipY, 0);
            _legL.localScale = _legR.localScale = V(1, legScale, 1);
            _legL.localRotation = Euler(legSwing, 0, 0);
            _legR.localRotation = Euler(-legSwing, 0, 0);

            // head: the look pitch, leads turns, an occasional curious tilt, glances around when idle, the emote on top
            if (_t >= _nextTilt)
            {
                _tiltTarget = Rand(6f, 12f) * (_rng.Next(2) == 0 ? -1 : 1);
                _tiltEnd = _t + Rand(1.2f, 2.2f);
                _nextTilt = _t + Rand(5f, 11f);
            }
            if (_t >= _tiltEnd) _tiltTarget = 0;
            _tilt = Approach(_tilt, _tiltTarget * (1f - work) * (1f - em), dt, 4);
            float look = 22f * MathF.Sin(_t * 0.41f + _lookSeed) * MathF.Sin(_t * 0.17f + _lookSeed * 0.7f);
            float headPitch = Clamp(pose.Pitch * 0.8f, -32f, 56f) - lean * 0.5f;   // limited, because looking far up would show the helmet's underside
            headPitch = Lerp(headPitch, emLookPitch, emLook) + emPitch;
            float headYaw = Clamp(_yawRate * 0.07f, -25, 25) + look * _idle + emYaw;
            if (_point > 0.001f)
            {
                // look where the hand points
                var d = ToHips(_pointAt);
                float ex = d.x, ey = d.y - (G.NeckY * sy + 0.2f), ez = d.z - 0.08f;
                float yawP = MathF.Atan2(ex, ez) / Deg, pitchP = MathF.Atan2(-ey, MathF.Sqrt(ex * ex + ez * ez)) / Deg;
                headPitch = Lerp(headPitch, Clamp(pitchP, -30f, 60f), 0.85f * _point);
                headYaw = Lerp(headYaw, Clamp(yawP, -60f, 60f), 0.85f * _point);
            }
            var qNeck = Euler(headPitch, headYaw, _tilt + sw * 2f * _walk + emRoll);
            _qNeck = _posed ? Nlerp(_qNeck, qNeck, 1f - MathF.Exp(-dt * 20f)) : qNeck;
            _neck.localRotation = _qNeck;
            _helmet.localRotation = Euler(-5f + MathF.Sin(_phase * 2f - 1.2f) * 3f * _walk, 0, _jaunt);

            // goggles: blink now and then (sometimes twice), squint while working, a little at a station
            if (_t >= _nextBlink)
            {
                _blinkT = 0;
                _nextBlink = _t + (_rng.NextDouble() < 0.2 ? 0.28f : Rand(2.2f, 6f));
            }
            float lid = 1f;
            if (_blinkT >= 0)
            {
                _blinkT += dt;
                lid = _blinkT < 0.07f ? 1f - _blinkT / 0.07f : _blinkT < 0.15f ? (_blinkT - 0.07f) / 0.08f : 1f;
                if (_blinkT >= 0.15f) _blinkT = -1;
            }
            _eyes.localScale = V(1, Lerp(0.08f, 1f, lid) * Lerp(1f, 0.6f, busy) * Lerp(1f, 0.85f, _station) * emEyes, 1);

            // arms, in layers: walk swing -> resting at a station -> on the map table -> busy hands -> map pointer
            // -> holding something -> emote. Each layer blends over the last by its own weight.
            float armSwing = sw * 24f * _walk + breath * 1.5f;
            float roll0 = 7f + 6f * _crouch - 3f * _station;
            float xL = Lerp(-armSwing, -26f + breath * 2f, _station);
            float xR = Lerp(armSwing, -26f + breath * 2f, _station);
            float yawIn = 10f * _station;
            var qL = Euler(xL, yawIn, -roll0);
            var qR = Euler(xR, -yawIn, roll0);
            if (_map > 0.001f)
            {
                // at the map: each hand on the rail where it is nearest; a hand that cannot reach it hangs relaxed
                var qa = RailAim(-1, out float wl);
                qL = Nlerp(qL, Nlerp(Euler(-20f, 6f, -roll0), qa, wl), _map);
                qa = RailAim(1, out float wr);
                qR = Nlerp(qR, Nlerp(Euler(-20f, -6f, roll0), qa, wr), _map);
            }

            float generic = _operate * (1f - _reachW);   // holding something we cannot place: hands forward and busy
            if (generic > 0.001f)
            {
                float f = MathF.Sin(_t * 7f) * 6f;
                qL = Nlerp(qL, Euler(-58f - f, 14f, -4f), generic);
                qR = Nlerp(qR, Euler(-58f + f, -14f, 4f), generic);
            }
            if (_point > 0.001f)
            {
                var qp = Aim(ToHips(_pointAt), 1, out bool ok);
                if (ok) qR = Nlerp(qR, qp, _point);
            }
            float hold = _operate * _reachW;
            if (hold > 0.001f)
            {
                // the hand on that side takes it (both when it is in front of them), turning a small circle
                var target = ToHips(_reach);
                _handR = Approach(_handR, target.x > -0.08f ? 1 : 0, dt, 6);
                _handL = Approach(_handL, target.x < 0.08f ? 1 : 0, dt, 6);
                var crank = Euler(MathF.Sin(_t * 5f) * 5f, 0, MathF.Cos(_t * 5f) * 4f);
                var qa = Aim(V(target.x + 0.05f, target.y, target.z), 1, out bool okR);
                if (okR) qR = Nlerp(qR, Mul(qa, crank), hold * _handR);
                qa = Aim(V(target.x - 0.05f, target.y, target.z), -1, out bool okL);
                if (okL) qL = Nlerp(qL, Mul(qa, crank), hold * _handL);
            }
            float emArms = em * (1f - _hold);   // when carrying the cat, emotes still move the head and body but not the arms
            if (emArms > 0.001f)
            {
                em = emArms;
                switch (e)
                {
                    case 1:   // wave
                        qR = Nlerp(qR, Euler(15f, 0, 150f + 20f * MathF.Sin(te * 11f)), em);
                        break;
                    case 2:   // look here: the arm straight out ahead at shoulder height (18 deg out, 8 up), jabbed forward
                    {
                        var qp = FromDown(Rotate(Conj(_qHips), V(0.309f, 0.139f, 0.951f)));
                        float pulse = MathF.Max(0, MathF.Sin(te * 12f)) * MathF.Exp(-te * 2f);
                        qR = Nlerp(qR, Mul(qp, Euler(-4f * pulse, 0, 0)), em);
                        break;
                    }
                    case 3:   // thumbs up: arm out to the front-side at chest height, thumb to the sky, a little pump
                        qR = Nlerp(qR, Euler(-78f + 6f * MathF.Sin(te * 8f), 28f, 12f), em);
                        break;
                    case 4:   // salute: the hand to the helmet brim
                    {
                        var qs = Aim(V(0.11f, G.NeckY * sy + 0.27f, 0.17f), 1, out bool ok);
                        if (ok) qR = Nlerp(qR, qs, em);
                        break;
                    }
                    case 5:   // shrug: both hands out, palms up, shoulders up
                        qL = Nlerp(qL, Euler(-25f, 10f, -38f), em);
                        qR = Nlerp(qR, Euler(-25f, -10f, 38f), em);
                        break;
                    case 6:   // cheer: both arms up, pumping with the hops
                    {
                        float pump = 10f * MathF.Sin(te * 2 * MathF.PI * 1.8f);
                        qL = Nlerp(qL, Euler(12f, 0, -(160f + pump)), em);
                        qR = Nlerp(qR, Euler(12f, 0, 160f + pump), em);
                        break;
                    }
                }
            }
            if (_hold > 0.001f)
            {
                // the cradle: both hands forward and together at the waist, forearms under the cat; a breath of sway
                float b = breath * 0.006f;
                var qc = Aim(V(_shoulderX + CradleX, _shoulderY + CradleY + b, _shoulderZ + CradleZ), 1, out bool okR);
                if (okR) qR = Nlerp(qR, qc, _hold);
                qc = Aim(V(-_shoulderX - CradleX, _shoulderY + CradleY + b, _shoulderZ + CradleZ), -1, out bool okL);
                if (okL) qL = Nlerp(qL, qc, _hold);
            }
            float ka = 1f - MathF.Exp(-dt * 18f);   // the final smoothing, so there are no snaps between layers or emotes
            _qArmL = _posed ? Nlerp(_qArmL, qL, ka) : qL;
            _qArmR = _posed ? Nlerp(_qArmR, qR, ka) : qR;
            _armL.localRotation = _qArmL;
            _armR.localRotation = _qArmR;
            _posed = true;
            if (_hold > 0.001f)
            {
                // on top of the forearms, between the hands: hips -> figure -> world (the figure only turns about y)
                var down = V(0, -G.ArmLength * CradleRest, 0);
                var l = Rotate(_qArmL, down); var r = Rotate(_qArmR, down);
                var mid = Rotate(_qHips, V((l.x + r.x) * 0.5f, _shoulderY + (l.y + r.y) * 0.5f, _shoulderZ + (l.z + r.z) * 0.5f));
                float fx = mid.x, fy = mid.y + _bob + _hipY, fz = mid.z;
                _holdPos = V(_wpX + fx * _cosYaw + fz * _sinYaw, _wpY + fy + SleeveTop, _wpZ - fx * _sinYaw + fz * _cosYaw);
                _holdRot = Euler(0, yaw + 90f, 0);   // forward = the figure's right
            }

            // scarf end: a springy lag behind the motion, a little flutter, a bounce on the hops
            float tx = 14f + Clamp(_vf * 22f, -10f, 55f) + MathF.Sin(_t * 8.3f) * 3f * (0.3f + _walk) - 6f * _crouch;
            float tz = Clamp(-_vl * 16f + _yawRate * 0.06f, -35f, 35f);
            _tailXv += ((tx - _tailX) * 60f - _tailXv * 9f) * dt; _tailX += _tailXv * dt;
            _tailZv += ((tz - _tailZ) * 60f - _tailZv * 9f) * dt; _tailZ += _tailZv * dt;
            _tail.localRotation = Euler(_tailX, 0, _tailZ);
        }

        // ------------------------------------------------------------------ managed maths
        // Unity's struct methods (Quaternion.Euler, Slerp, the Vector3 constructor ...) go through the IL2CPP bridge
        // and can box their results; per frame we only fill struct fields ourselves.

        static Vector3 V(float x, float y, float z) { Vector3 v = default; v.x = x; v.y = y; v.z = z; return v; }

        static Quaternion Q(float x, float y, float z, float w) { Quaternion q = default; q.x = x; q.y = y; q.z = z; q.w = w; return q; }

        static Quaternion Mul(Quaternion a, Quaternion b) => Q(
            a.w * b.x + a.x * b.w + a.y * b.z - a.z * b.y,
            a.w * b.y - a.x * b.z + a.y * b.w + a.z * b.x,
            a.w * b.z + a.x * b.y - a.y * b.x + a.z * b.w,
            a.w * b.w - a.x * b.x - a.y * b.y - a.z * b.z);

        static Quaternion Conj(Quaternion q) => Q(-q.x, -q.y, -q.z, q.w);

        /// <summary>Works like Quaternion.Euler: z, then x, then y, about the parent's axes.</summary>
        static Quaternion Euler(float x, float y, float z)
        {
            float hx = x * Deg * 0.5f, hy = y * Deg * 0.5f, hz = z * Deg * 0.5f;
            return Mul(Mul(Q(0, MathF.Sin(hy), 0, MathF.Cos(hy)), Q(MathF.Sin(hx), 0, 0, MathF.Cos(hx))), Q(0, 0, MathF.Sin(hz), MathF.Cos(hz)));
        }

        static Vector3 Rotate(Quaternion q, Vector3 v)
        {
            // v + 2w(u x v) + 2u x (u x v)
            float tx = 2 * (q.y * v.z - q.z * v.y), ty = 2 * (q.z * v.x - q.x * v.z), tz = 2 * (q.x * v.y - q.y * v.x);
            return V(v.x + q.w * tx + (q.y * tz - q.z * ty), v.y + q.w * ty + (q.z * tx - q.x * tz), v.z + q.w * tz + (q.x * ty - q.y * tx));
        }

        /// <summary>Returns the rotation that turns straight down (the hanging arm) towards <paramref name="d"/>.</summary>
        static Quaternion FromDown(Vector3 d)
        {
            float len = MathF.Sqrt(d.x * d.x + d.y * d.y + d.z * d.z);
            float x = d.x / len, y = d.y / len, z = d.z / len;
            // cross((0,-1,0), t) = (-z, 0, x); w = 1 + dot = 1 - y
            if (1f - y < 1e-4f) return Q(1, 0, 0, 0);   // pointing straight up, which is half a turn about x
            return Normalize(Q(-z, 0, x, 1f - y));
        }

        static Quaternion Normalize(Quaternion q)
        {
            float n = 1f / MathF.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return Q(q.x * n, q.y * n, q.z * n, q.w * n);
        }

        /// <summary>Normalised lerp along the short way around. That is accurate enough for blending poses.</summary>
        static Quaternion Nlerp(Quaternion a, Quaternion b, float t)
        {
            if (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0) b = Q(-b.x, -b.y, -b.z, -b.w);
            return Normalize(Q(Lerp(a.x, b.x, t), Lerp(a.y, b.y, t), Lerp(a.z, b.z, t), Lerp(a.w, b.w, t)));
        }
    }
}
