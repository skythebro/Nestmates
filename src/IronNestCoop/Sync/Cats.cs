using System.Collections.Generic;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.UI;
using TMPro;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// One cat per player. Everyone keeps their own cat (the game spawns it from their own customisation and runs
    /// its AI locally), and every other machine shows a puppet of it: the same model (cat or kitten), colours, hat
    /// and name, moved and animated by the owner's stream (10 updates a second). Puppets have no AI, physics or
    /// interaction (they ignore raycasts), so nobody can pick up or shoo someone else's cat by accident. They also
    /// never touch the game's own cat bookkeeping, because they are built inactive and their gameplay scripts are
    /// stripped before Awake can run.
    /// </summary>
    public sealed class Cats : SyncModule
    {
        public override string Name => "cats";
        public static Cats Instance { get; private set; }

        sealed class Puppet
        {
            public GameObject Root;
            public Animator Anim;
            public TextMeshPro Tag;
            public string Look;      // the customisation it was built with
            public CatCustomization Custom;   // its customisation component (the shape sliders are applied live)
            public string ShapeKey;
            public Vector3 Pos, TargetPos;
            public float Yaw, TargetYaw, Speed, LastHeard;
            public int State;
            public bool Held;          // its owner is carrying it, so it lies in the owner's figure's arms
            public int PlacedFrame = -10;
            public List<Renderer> Hands;   // parts of the carried look that belong to a first-person hand
        }

        readonly Dictionary<byte, Puppet> _puppets = new();
        CatCustomizationController _ctl;
        int _sceneSig;
        float _nextSend, _nextInfo;
        int _speedHash;   // The Animator string overloads are broken in this interop layer (ReadOnlySpan), so use hashes instead

        int SpeedHash(CatController c)
        {
            if (_speedHash == 0 && c?._agentAnimation != null && !string.IsNullOrEmpty(c._agentAnimation._movementSpeed))
                _speedHash = Animator.StringToHash(c._agentAnimation._movementSpeed);
            return _speedHash;
        }

        public Cats() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.CatState, OnState);
        public override void OnSessionEnd() { Clear(); if (_ownTag != null) Object.Destroy(_ownTag.gameObject); _ownTag = null; }

        // The player's own cat gets its name shown above it (to help find it in the turret), in the same style as the others'
        TextMeshPro _ownTag;

        void OwnCatTag(CatCustomizationController ctl)
        {
            var cat = ctl?.currentCatController;
            bool want = Settings.ShowOwnCatName && cat != null && cat.gameObject.activeInHierarchy;
            if (!want) { if (_ownTag != null) _ownTag.gameObject.SetActive(false); return; }
            if (_ownTag == null || _ownTag.transform.parent != cat.transform)
            {
                if (_ownTag != null) Object.Destroy(_ownTag.gameObject);
                var go = new GameObject("Nestmates own cat name");
                go.transform.SetParent(cat.transform, false);
                go.transform.localPosition = new Vector3(0, ctl.isKitten ? 0.45f : 0.6f, 0);
                _ownTag = go.AddComponent<TextMeshPro>();
                _ownTag.fontSize = 1.4f; _ownTag.alignment = TextAlignmentOptions.Center; _ownTag.color = Theme.Text; _ownTag.outlineWidth = 0.2f;
                _ownTag.rectTransform.sizeDelta = new Vector2(3, 1);
            }
            _ownTag.gameObject.SetActive(true);
            string text = (string.IsNullOrEmpty(ctl.catName) ? "cat" : ctl.catName) + "\n<size=60%>your cat</size>";
            if (_ownTag.text != text) _ownTag.text = text;
            var cam = Camera.main;
            if (cam != null) _ownTag.transform.rotation = Quaternion.LookRotation(_ownTag.transform.position - cam.transform.position);
        }
        public override void OnPlayerLeft(Player pl) => Drop(pl.Slot);

        void Drop(byte slot) { if (_puppets.TryGetValue(slot, out var p)) { if (p.Root != null) Object.Destroy(p.Root); _puppets.Remove(slot); } }

        void Clear()
        {
            foreach (var p in _puppets.Values) if (p.Root != null) Object.Destroy(p.Root);
            _puppets.Clear();
        }

        float _retryFind;

        CatCustomizationController Ctl()
        {
            int sig = UnityEngine.SceneManagement.SceneManager.sceneCount;
            // Search the scene only when the scenes changed, or every 2 s while the cat is missing (not every frame)
            if (sig != _sceneSig || (_ctl == null && Time.unscaledTime >= _retryFind))
            { _sceneSig = sig; _ctl = Object.FindObjectOfType<CatCustomizationController>(); _retryFind = Time.unscaledTime + 2f; }
            return _ctl;
        }

        /// <summary>Hooks used by the tests.</summary>
        public CatController LocalCat => Ctl()?.currentCatController;
        public bool TryGetPuppet(byte slot, out Vector3 pos, out string look, out bool visible)
        {
            pos = default; look = null; visible = false;
            if (!_puppets.TryGetValue(slot, out var p) || p.Root == null) return false;
            pos = p.Root.transform.position; look = p.Look; visible = p.Root.activeInHierarchy;
            return true;
        }

        /// <summary>For the tests: reports what a cat model really shows for each shape slider of <paramref name="shape"/>, and its hat.</summary>
        public static string ShapeLine(CatCustomization cc, byte[] shape)
        {
            if (cc == null) return "no cat";
            var sb = new System.Text.StringBuilder();
            int hat = 0;
            var hats = cc.hatObjects;
            for (int i = 0; hats != null && i < hats.Count; i++) if (hats[i] != null && hats[i].activeSelf) { hat = i + 1; break; }
            sb.Append("hat ").Append(hat);
            for (int i = 0; shape != null && i + 2 < shape.Length; i += 3)
            {
                int f = shape[i];
                var mesh = (f & 1) != 0 ? cc.eyesMesh : (f & 2) != 0 ? cc.bodyMesh : (f & 4) != 0 ? cc.furMesh : cc.whiskersMesh;
                float wgt = mesh != null && mesh.sharedMesh != null && shape[i + 1] < mesh.sharedMesh.blendShapeCount ? mesh.GetBlendShapeWeight(shape[i + 1]) : -1;
                sb.Append($" {f}/{shape[i + 1]}={Mathf.RoundToInt(wgt)}");
            }
            return sb.ToString();
        }

        /// <summary>For the tests: describes the puppet of a slot as <see cref="ShapeLine"/> does, along with the shapes its owner sent.</summary>
        public string PuppetShapes(byte slot)
        {
            if (!_puppets.TryGetValue(slot, out var p) || p.Root == null) return "no puppet";
            return ShapeLine(p.Custom, PlayerInfo.Decode(Coop.Session.GetPlayer(slot)?.Info).CatShape);
        }

        /// <summary>For the tests: describes this player's own cat as <see cref="ShapeLine"/> does.</summary>
        public static string MyShapes()
        {
            var ctl = Object.FindObjectOfType<CatCustomizationController>();
            return ctl == null ? "no cat" : ShapeLine(ctl.currentCatCustomization, ShapesOf(ctl));
        }

        static string ShapeKey(byte[] s) => s == null || s.Length == 0 ? "" : System.Convert.ToBase64String(s);

        /// <summary>Reads the cat's shape sliders from the game's customisation, 3 bytes each, sorted so the key is stable.</summary>
        static byte[] ShapesOf(CatCustomizationController ctl)
        {
            var keys = ctl.blendShapeKeys;
            if (keys == null) return System.Array.Empty<byte>();
            var list = new System.Collections.Generic.List<(int Flags, int Index, int Value)>();
            foreach (var kv in keys)
            {
                var k = kv.Value;
                if (k == null || k.blendShapeIndex < 0 || k.blendShapeIndex > 255) continue;
                int flags = (k.eyes ? 1 : 0) | (k.body ? 2 : 0) | (k.fur ? 4 : 0) | (k.whiskers ? 8 : 0);
                list.Add((flags, k.blendShapeIndex, Mathf.Clamp(k.value, 0, 100)));
                if (list.Count >= 64) break;
            }
            list.Sort();
            var b = new byte[list.Count * 3];
            for (int i = 0; i < list.Count; i++) { b[i * 3] = (byte)list[i].Flags; b[i * 3 + 1] = (byte)list[i].Index; b[i * 3 + 2] = (byte)list[i].Value; }
            return b;
        }

        /// <summary>Applies the owner's sliders to this puppet. The values come from another player, so each one is validated and applied on its own.</summary>
        static void ApplyShapes(Puppet p, byte[] shape)
        {
            string key = ShapeKey(shape);
            if (p.Custom == null || p.ShapeKey == key) return;
            p.ShapeKey = key;
            for (int i = 0; i + 2 < shape.Length; i += 3)
            {
                int flags = shape[i];
                if (flags == 0 || flags > 15 || shape[i + 2] > 100) continue;
                var k = new BlendShapeKey { eyes = (flags & 1) != 0, body = (flags & 2) != 0, fur = (flags & 4) != 0, whiskers = (flags & 8) != 0, blendShapeIndex = shape[i + 1], value = shape[i + 2] };
                try { p.Custom.SetBlendShapeValue(k); }
                catch (System.Exception e) { CoopLog.ErrorOnce("cat-shape", "[cats] a shape slider could not be applied: " + e.Message); }
            }
        }

        static string LookOf(PlayerInfo i) => $"{(i.CatIsKitten ? "kitten" : "cat")}/body{i.CatBody}/eyes{i.CatEyes}/hat{i.CatHat}";

        public override void Tick(float dt)
        {
            if (!Coop.InSession) return;
            var ctl = Ctl();
            OwnCatTag(ctl);
            // 1. Tell the others what this player's cat looks like (from the game's own customisation)
            if (ctl != null && Time.unscaledTime >= _nextInfo)
            {
                _nextInfo = Time.unscaledTime + 2f;
                var me = PlayerInfo.Local;
                string before = LookOf(me) + me.CatName + ShapeKey(me.CatShape);
                me.CatIsKitten = ctl.isKitten; me.CatBody = ctl.bodyColor; me.CatEyes = ctl.eyesColor; me.CatHat = ctl.hatType; me.CatName = ctl.catName ?? "";
                me.CatShape = ShapesOf(ctl);
                if (LookOf(me) + me.CatName + ShapeKey(me.CatShape) != before) Coop.Session.SetLocalInfo(Coop.LocalName, PlayerInfo.Encode());
            }
            // 2. Stream this player's cat
            var cat = ctl?.currentCatController;
            if (cat != null && PlayerInfo.Local.CatEnabled && cat.gameObject.activeInHierarchy && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + 0.1f;
                var t = cat.transform;
                var anim = cat._agentAnimation != null ? cat._agentAnimation._animator : cat.GetComponentInChildren<Animator>();
                int sh = SpeedHash(cat);
                var w = Coop.Writer();
                w.F32(t.position.x); w.F32(t.position.y); w.F32(t.position.z); w.F32(t.eulerAngles.y);
                w.F32(anim != null && sh != 0 ? anim.GetFloat(sh) : 0f);
                w.VarI(anim != null && anim.isActiveAndEnabled ? anim.GetCurrentAnimatorStateInfo(0).fullPathHash : 0);
                w.VarU(anim != null && anim.isActiveAndEnabled ? BoolMask(anim) : 0);   // carried, sitting, sleeping, and so on
                w.Bool(IsCarried(cat));   // added later, so an older reader stops before it
                Coop.Send(Msg.CatState, w, Delivery.Unreliable);
            }
            // 3. Animate the puppets
            foreach (var kv in _puppets) Animate(kv.Key, kv.Value, dt);
        }

        CatPickUpHandler _pickup; float _retryPickup;
        public static bool TestCarried;   // for the tests: acts as if the player carried their cat

        /// <summary>True when this player is carrying their cat (the game's pick-up handler holds it).</summary>
        bool IsCarried(CatController cat)
        {
            if (TestCarried) return true;
            if (_pickup == null && Time.unscaledTime >= _retryPickup) { _pickup = Object.FindObjectOfType<CatPickUpHandler>(); _retryPickup = Time.unscaledTime + 2f; }
            var held = _pickup != null ? _pickup.heldCat : null;
            return held != null && held.Pointer == cat.Pointer;
        }

        /// <summary>True when the cat of this slot is being carried by its owner right now (the figure holds its arms out for it).</summary>
        public bool IsHeld(byte slot) => _puppets.TryGetValue(slot, out var p) && p.Held && p.Root != null && Time.unscaledTime - p.LastHeard < 1.5f;

        /// <summary>The owner's figure puts the carried cat here, after the figure has moved this frame.</summary>
        public void PlaceHeld(byte slot, Vector3 pos, Quaternion rot)
        {
            if (!_puppets.TryGetValue(slot, out var p) || p.Root == null || !p.Held || LaunchArgs.Mutant("nocatcradle")) return;
            p.Pos = pos; p.Yaw = rot.eulerAngles.y;
            p.Root.transform.SetPositionAndRotation(pos, rot);
            p.PlacedFrame = Time.frameCount;
        }

        /// <summary>For the tests: reports where the cat puppet of a slot is, and whether it is carried.</summary>
        public bool TryGetPuppet(byte slot, out Vector3 pos, out bool held)
        {
            pos = default; held = false;
            if (!_puppets.TryGetValue(slot, out var p) || p.Root == null) return false;
            pos = p.Root.transform.position; held = p.Held;
            return true;
        }

        /// <summary>For the tests: lists the names of the puppet's renderers that are showing.</summary>
        public string PuppetParts(byte slot)
        {
            if (!_puppets.TryGetValue(slot, out var p) || p.Root == null) return "";
            var names = new List<string>();
            foreach (var r in p.Root.GetComponentsInChildren<Renderer>(false)) if (r != null && r.enabled) names.Add(r.name);
            return string.Join(", ", names);
        }

        /// <summary>Identifies the first-person hand that comes with the carried look. It is hidden on a puppet,
        /// because the figure's own arms hold the cat.</summary>
        static void ShowHands(Puppet p, bool show)
        {
            if (p.Hands == null)
            {
                p.Hands = new List<Renderer>();
                foreach (var r in p.Root.GetComponentsInChildren<Renderer>(true))
                {
                    string n = r != null ? r.name.ToLowerInvariant() : "";
                    if (n.Contains("hand") || n.Contains("finger") || n.Contains("glove") || System.Text.RegularExpressions.Regex.IsMatch(n, "(^|[^a-z])arms?([^a-z]|$)")) p.Hands.Add(r);
                }
                if (p.Hands.Count > 0) CoopLog.Debug($"[cats] puppet hand parts: {p.Hands.Count}");
            }
            foreach (var r in p.Hands) if (r != null && r.enabled != show) r.enabled = show;
        }

        void OnState(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot) return;
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            float yaw = r.F32(), speed = r.F32();
            if (!float.IsFinite(pos.x) || !float.IsFinite(pos.y) || !float.IsFinite(pos.z) || !float.IsFinite(yaw) || !float.IsFinite(speed)) return;   // ignore a broken pose from the network
            int state = (int)r.VarI();
            ulong bools = r.VarU();
            bool held = r.Remaining > 0 && r.Bool();
            var info = PlayerInfo.Decode(Coop.Session.GetPlayer(c.FromSlot)?.Info);
            if (!info.CatEnabled) { Drop(c.FromSlot); return; }
            string look = LookOf(info);
            if (_puppets.TryGetValue(c.FromSlot, out var p) && (p.Root == null || p.Look != look)) { Drop(c.FromSlot); p = null; }
            if (p == null)
            {
                if (Time.unscaledTime < _retryBuild) return;   // the last build failed, so do not retry on every message
                p = Build(info);
                if (p == null) { _retryBuild = Time.unscaledTime + 10f; return; }
                p.Pos = pos; p.Yaw = yaw;
                _puppets[c.FromSlot] = p;
            }
            p.TargetPos = pos; p.TargetYaw = yaw; p.Speed = speed; p.LastHeard = Time.unscaledTime;
            if (p.Held && !held) p.Pos = pos;   // it was put down: start from where it lay, rather than floating back from the face
            p.Held = held;
            ApplyShapes(p, info.CatShape);   // the owner moved a shape slider; no rebuild is needed
            if (p.Anim != null && p.Anim.isActiveAndEnabled) ApplyBools(p.Anim, bools);
            if (state != 0 && state != p.State && p.Anim != null && p.Anim.isActiveAndEnabled && p.Anim.HasState(0, state))
            {
                p.Anim.CrossFadeInFixedTime(state, 0.2f, 0);
            }
            p.State = state;
        }

        // A cat's look depends on its bool parameters as much as on its state (being picked up sets a "carried"
        // bool). The owner sends them as a bit mask in parameter order, and puppets use the same controller.
        static ulong BoolMask(Animator a)
        {
            ulong m = 0; int bit = 0;
            for (int i = 0; i < a.parameterCount && bit < 64; i++)
            {
                var p = a.GetParameter(i);
                if (p.type != AnimatorControllerParameterType.Bool) continue;
                if (a.GetBool(p.nameHash)) m |= 1UL << bit;
                bit++;
            }
            return m;
        }

        static void ApplyBools(Animator a, ulong m)
        {
            int bit = 0;
            for (int i = 0; i < a.parameterCount && bit < 64; i++)
            {
                var p = a.GetParameter(i);
                if (p.type != AnimatorControllerParameterType.Bool) continue;
                bool on = (m & (1UL << bit)) != 0;
                if (a.GetBool(p.nameHash) != on) a.SetBool(p.nameHash, on);
                bit++;
            }
        }

        void Animate(byte slot, Puppet p, float dt)
        {
            if (p.Root == null) return;
            ShowHands(p, !p.Held);
            // Carried: the owner's figure has put it in its arms (this frame or the last, since the modules can tick
            // in either order). Otherwise it follows the stream (if it is carried and there is no figure, it goes
            // where the owner holds it).
            if (!(p.Held && Time.frameCount - p.PlacedFrame <= 1))
            {
                if ((p.TargetPos - p.Pos).sqrMagnitude > 9f) p.Pos = p.TargetPos;
                float k = 1f - Mathf.Exp(-dt * 12f);
                p.Pos = Vector3.Lerp(p.Pos, p.TargetPos, k);
                p.Yaw = Mathf.LerpAngle(p.Yaw, p.TargetYaw, k);
                p.Root.transform.SetPositionAndRotation(p.Pos, Quaternion.Euler(0, p.Yaw, 0));
            }
            int hash = SpeedHash(LocalCat);
            if (p.Anim != null && hash != 0 && p.Anim.isActiveAndEnabled) p.Anim.SetFloat(hash, p.Speed);
            p.Root.SetActive(Time.unscaledTime - p.LastHeard < 3f);
            if (p.Tag != null)
            {
                var pl = Coop.Session.GetPlayer(slot);
                var info = PlayerInfo.Decode(pl?.Info);
                p.Tag.gameObject.SetActive(Settings.ShowNameTags);
                p.Tag.text = (string.IsNullOrEmpty(info.CatName) ? "cat" : info.CatName) + $"\n<size=60%>{pl?.Name ?? "?"}'s</size>";
                var cam = Camera.main;
                if (cam != null) p.Tag.transform.rotation = Quaternion.LookRotation(p.Tag.transform.position - cam.transform.position);
            }
        }

        float _retryBuild;

        Puppet Build(PlayerInfo info)
        {
            GameObject holder = null, go = null;
            var ctl = Ctl();
            var prefab = ctl == null ? null : (info.CatIsKitten ? ctl.kittenPrefab : ctl.catPrefab);
            if (prefab == null) return null;
            try
            {
                // The holder is inactive, so the instance's scripts never run Awake and cannot register as "the" cat.
                holder = new GameObject("Coop cat (holder)");
                holder.SetActive(false);
                go = Object.Instantiate(prefab, holder.transform);
                go.name = "Coop remote cat";
                foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                    if (mb != null && mb.TryCast<CatCustomization>() == null) Object.DestroyImmediate(mb);
                foreach (var col in go.GetComponentsInChildren<Collider>(true)) Object.DestroyImmediate(col);
                foreach (var ag in go.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) Object.DestroyImmediate(ag);
                foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(rb);
                foreach (var t in go.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 2;   // the Ignore Raycast layer
                var anim = go.GetComponentInChildren<Animator>(true);
                if (anim != null) anim.fireEvents = false;   // its event receivers were stripped, so it must not fire events
                // Looks: the owner's choices, taken from the game's own material lists.
                var cc = go.GetComponentInChildren<CatCustomization>(true);
                if (cc != null)
                {
                    if (Valid(ctl.BodyMaterials, info.CatBody) && Valid(ctl.FurMaterials, info.CatBody)) cc.ChangeBodyColor(ctl.BodyMaterials[info.CatBody], ctl.FurMaterials[info.CatBody]);
                    if (Valid(ctl.EyesMaterials, info.CatEyes)) cc.ChangeEyesColor(ctl.EyesMaterials[info.CatEyes]);
                    // A hat the model has. The value comes from another player: 0 means none, 1..count picks a hat.
                    var hats = cc.hatObjects;
                    if (hats != null && info.CatHat >= 0 && info.CatHat <= hats.Count) cc.SetHatType(info.CatHat);
                }
                go.transform.SetParent(null, false);
                Object.Destroy(holder);
                var p = new Puppet { Root = go, Anim = anim, Look = LookOf(info), Custom = cc };
                ApplyShapes(p, info.CatShape);
                var tagGo = new GameObject("Name");
                tagGo.transform.SetParent(go.transform, false);
                tagGo.transform.localPosition = new Vector3(0, info.CatIsKitten ? 0.45f : 0.6f, 0);
                p.Tag = tagGo.AddComponent<TextMeshPro>();
                p.Tag.fontSize = 1.4f; p.Tag.alignment = TextAlignmentOptions.Center; p.Tag.color = Theme.Text; p.Tag.outlineWidth = 0.2f;
                p.Tag.rectTransform.sizeDelta = new Vector2(3, 1);
                go.SetActive(true);
                CoopLog.Info($"[cats] built a puppet: {p.Look}");
                return p;
            }
            catch (System.Exception e)
            {
                CoopLog.ErrorOnce("cat-build", "[cats] could not build a remote cat: " + e);
                if (go != null) Object.Destroy(go);   // do not leave anything half-built behind
                if (holder != null) Object.Destroy(holder);
                return null;
            }
        }

        static bool Valid(Il2CppSystem.Collections.Generic.List<Material> l, int i) => l != null && i >= 0 && i < l.Count && l[i] != null;
    }
}
