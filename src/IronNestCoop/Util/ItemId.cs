using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestCoop.Util
{
    /// <summary>
    /// Stable identity for things that move between parents (records, cards, map tokens). A scene path changes
    /// when an item moves into a slot, so: (1) the game's own MissionSaveIdentity.Id when it has one; else
    /// (2) "origin:" + scene + "//" + the item's name, when no other item in that scene has the name (the cups,
    /// the coffee can, most records): the same on every machine wherever each player's own game has put it;
    /// else (3) the item's path when the scene was searched (items whose names repeat, like map tokens).
    /// Paths alone did not agree: the base's items are where each player last left them (one player's cup in
    /// the machine's slot, the other's on the counter), so only items still in the same place synced.
    /// </summary>
    public static class ItemId
    {
        static readonly Dictionary<string, MissionSaveIdentity> SaveIds = new();
        static readonly Dictionary<IntPtr, string> OriginOf = new();
        static readonly Dictionary<string, GameObject> ByOrigin = new();
        static int _sig = int.MinValue;
        // things a scene spawner delivered at runtime (records in a delivery): named by spawner + delivery number
        static readonly Dictionary<IntPtr, string> SpawnedOf = new();
        static readonly Dictionary<string, GameObject> BySpawned = new();
        static float _lastSaveIdScan = -10f;

        static int SceneSignature()
        {
            int h = UnityEngine.SceneManagement.SceneManager.sceneCount;
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                h = h * 31 + UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).handle;
            return h;
        }

        /// <summary>Record origin paths of every draggable item once per scene set (call often; cheap when unchanged).</summary>
        public static void EnsureScanned()
        {
            int sig = SceneSignature();
            if (sig == _sig) return;
            _sig = sig;
            OriginOf.Clear(); ByOrigin.Clear(); SaveIds.Clear(); NameCount.Clear(); Aliases.Clear();
            var items = new List<GameObject>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DraggableItem>(), true))
            {
                var d = o.TryCast<DraggableItem>();
                if (d == null) continue;
                items.Add(d.gameObject);
                string k = NameKey(d.gameObject);
                NameCount[k] = NameCount.TryGetValue(k, out var c) ? c + 1 : 1;
            }
            foreach (var go in items) Register(go);
        }

        // how many draggable items of each scene share a name (from the last search)
        static readonly Dictionary<string, int> NameCount = new();
        static string NameKey(GameObject go) => go.scene.name + "//" + go.name;

        static string Register(GameObject go)
        {
            if (OriginOf.TryGetValue(go.Pointer, out var id)) return id;
            // names only for items the scene itself holds (the same on every machine); copies made at runtime are
            // "(Clone)"s, and how many there are can differ
            bool unique = !LaunchArgs.Mutant("pathitemids") && !go.name.Contains("(Clone)") && NameCount.TryGetValue(NameKey(go), out var count) && count == 1;
            id = unique ? "origin:" + NameKey(go) : "origin:" + ScenePath.Of(go.transform);
            int n = 1;
            while (ByOrigin.ContainsKey(id)) id = "origin:" + ScenePath.Of(go.transform) + "#" + (++n);
            OriginOf[go.Pointer] = id; ByOrigin[id] = go;
            return id;
        }

        /// <summary>
        /// A scene spawner delivered <paramref name="root"/> (the n-th delivery of that spawner this mission, the same
        /// on every machine: guests replay the host's spawns). Its items are named after the delivery, not after
        /// wherever each machine first happened to see them (a record already on someone's clipboard).
        /// </summary>
        public static void RegisterSpawned(GameObject root, string delivery)
        {
            if (root == null) return;
            foreach (var k in new List<IntPtr>(SpawnedOf.Keys)) { var id = SpawnedOf[k]; if (!BySpawned.TryGetValue(id, out var g) || g == null) { SpawnedOf.Remove(k); BySpawned.Remove(id); } }
            foreach (var d in root.GetComponentsInChildren<DraggableItem>(true))
            {
                if (d == null) continue;
                var rel = new List<string>();
                for (var t = d.transform; t != null && t.Pointer != root.transform.Pointer; t = t.parent) rel.Insert(0, t.name);
                string id = "spawn:" + delivery + "/" + string.Join("/", rel);
                int n = 1; string baseId = id;
                while (BySpawned.TryGetValue(id, out var other) && other != null && other.Pointer != d.gameObject.Pointer) id = baseId + "#" + (++n);
                SpawnedOf[d.gameObject.Pointer] = id; BySpawned[id] = d.gameObject;
            }
        }

        static GameObject ItemObject(Component c)
        {
            var d = c.GetComponent<DraggableItem>() ?? c.GetComponentInParent<DraggableItem>();
            return d != null ? d.gameObject : c.gameObject;
        }

        /// <summary>
        /// Punchcards are dealt at runtime from the (host's) deck: id = definition id + occurrence in deck order,
        /// which matches on every machine once the decks match (Requisition module).
        /// </summary>
        static string PunchcardId(Component c)
        {
            var pc = c.GetComponent<PunchcardRuntime>() ?? c.GetComponentInParent<PunchcardRuntime>();
            if (pc == null || pc.CurrentDefinition == null) return null;
            Cards();
            if (!_cardIds.ContainsKey(pc.Pointer)) Cards(miss: true);
            return _cardIds.TryGetValue(pc.Pointer, out var id) ? id : null;
        }

        // The deck's cards and their ids: GetAllCards searches the whole scene, and the item and deck order syncs
        // name cards several times a second. Worked out at most once a second, or again (once per frame) when a
        // card is asked for that the last search didn't have.
        static int _cardsFrame = -1;
        static float _cardsAt = -10f;
        static readonly Dictionary<IntPtr, string> _cardIds = new();
        static readonly Dictionary<string, Transform> _cardById = new();

        static void Cards(bool miss = false)
        {
            if (_cardsFrame == Time.frameCount) return;
            if (!miss && Time.unscaledTime - _cardsAt < 1f) return;
            _cardsFrame = Time.frameCount; _cardsAt = Time.unscaledTime;
            _cardIds.Clear(); _cardById.Clear();
            var cards = RequisitionConsoleManager.Instance?.GetAllCards();
            if (cards == null) return;
            var seen = new Dictionary<string, int>();
            foreach (var k in cards)
            {
                if (k == null || k.CurrentDefinition == null) continue;
                string def = k.CurrentDefinition.ID;
                seen.TryGetValue(def, out int n); seen[def] = n + 1;
                string id = $"punchcard:{def}#{n}";
                _cardIds[k.Pointer] = id; _cardById[id] = k.transform;
            }
        }

        static Transform FindPunchcard(string id)
        {
            Cards();
            if (!_cardById.TryGetValue(id, out var t) || t == null) { Cards(miss: true); _cardById.TryGetValue(id, out t); }
            return t != null ? t : null;
        }

        public static string Of(Component c)
        {
            if (c == null) return null;
            var pid = PunchcardId(c);
            if (pid != null) return pid;
            var sid = c.GetComponent<MissionSaveIdentity>() ?? c.GetComponentInParent<MissionSaveIdentity>();
            if (sid != null && !string.IsNullOrEmpty(sid.Id)) return sid.Id;
            var item = ItemObject(c);
            if (SpawnedOf.TryGetValue(item.Pointer, out var spawned)) return spawned;
            EnsureScanned();
            return Register(item);
        }

        public static Transform Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (id.StartsWith("punchcard:")) return FindPunchcard(id);
            if (id.StartsWith("spawn:")) return BySpawned.TryGetValue(id, out var sg) && sg != null ? sg.transform : null;
            if (id.StartsWith("origin:"))
            {
                EnsureScanned();
                if (ByOrigin.TryGetValue(id, out var go) && go != null) return go.transform;
                return Alias(id)?.transform;
            }
            if (SaveIds.TryGetValue(id, out var hit) && hit != null && hit.Id == id) return hit.transform;
            if (Time.unscaledTime - _lastSaveIdScan < 1f) return null;   // a full scan at most once a second
            _lastSaveIdScan = Time.unscaledTime;
            SaveIds.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MissionSaveIdentity>(), true))
            {
                var m = o.TryCast<MissionSaveIdentity>();
                if (m != null && !string.IsNullOrEmpty(m.Id)) SaveIds[m.Id] = m;
            }
            return SaveIds.TryGetValue(id, out hit) ? hit.transform : null;
        }

        // ids another machine made that this one doesn't have (one side named an item by its path, the other by its
        // name, or the item sits elsewhere here): the one item of that scene with that name, if exactly one
        static readonly Dictionary<string, GameObject> Aliases = new();

        static GameObject Alias(string id)
        {
            if (Aliases.TryGetValue(id, out var known)) return known != null ? known : null;
            string rest = id.Substring("origin:".Length), scene, name;
            int slashes = rest.IndexOf("//", StringComparison.Ordinal), colon = rest.IndexOf(':');
            if (slashes >= 0 && (colon < 0 || slashes < colon)) { scene = rest.Substring(0, slashes); name = rest.Substring(slashes + 2); }
            else if (colon >= 0)
            {
                scene = rest.Substring(0, colon);
                string path = System.Text.RegularExpressions.Regex.Replace(rest.Substring(colon + 1), @"#\d+$", "");
                name = path.Substring(path.LastIndexOf('/') + 1);
            }
            else { Aliases[id] = null; return null; }
            GameObject hit = null; int hits = 0;
            foreach (var g in ByOrigin.Values)
                if (g != null && g.name == name && g.scene.name == scene && (hit == null || hit.Pointer != g.Pointer)) { hit = g; hits++; }
            var found = hits == 1 ? hit : null;
            if (Aliases.Count < 4096) Aliases[id] = found;   // remembered until the next search, misses too
            return found;
        }

        public static T Find<T>(string id) where T : Component
        {
            var t = Find(id);
            return t == null ? null : (t.GetComponent<T>() ?? t.GetComponentInChildren<T>());
        }
    }
}
