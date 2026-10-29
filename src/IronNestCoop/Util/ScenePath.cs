using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace IronNestCoop.Util
{
    /// <summary>
    /// Stable cross-machine names for scene objects: "Scene:Root/Child[1]/Grandchild", where [n] is the
    /// index among same-named siblings. Both machines load the same scenes, so paths match.
    /// </summary>
    public static class ScenePath
    {
        public static string Of(Transform t)
        {
            if (t == null) return null;
            var sb = new StringBuilder();
            Build(t, sb);
            return t.gameObject.scene.name + ":" + sb;
        }

        static void Build(Transform t, StringBuilder sb)
        {
            if (t.parent != null) { Build(t.parent, sb); sb.Append('/'); }
            sb.Append(t.name);
            int dup = 0;
            var p = t.parent;
            if (p != null)
            {
                for (int i = 0; i < p.childCount; i++)
                {
                    var c = p.GetChild(i);
                    if (c == t) break;
                    if (c.name == t.name) dup++;
                }
            }
            else
            {
                var roots = t.gameObject.scene.GetRootGameObjects();
                foreach (var r in roots) { if (r.transform == t) break; if (r.name == t.name) dup++; }
            }
            if (dup > 0) sb.Append('[').Append(dup).Append(']');
        }

        public static Transform Find(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            int colon = path.IndexOf(':');
            if (colon < 0) return null;
            var scene = SceneManager.GetSceneByName(path.Substring(0, colon));
            if (!scene.IsValid() || !scene.isLoaded) return null;
            var parts = path.Substring(colon + 1).Split('/');
            Transform cur = null;
            foreach (var raw in parts)
            {
                string name = raw; int want = 0;
                int br = raw.LastIndexOf('[');
                if (br > 0 && raw.EndsWith("]") && int.TryParse(raw.Substring(br + 1, raw.Length - br - 2), out var n)) { name = raw.Substring(0, br); want = n; }
                Transform next = null; int seen = 0;
                if (cur == null)
                {
                    foreach (var r in scene.GetRootGameObjects())
                        if (r.name == name && seen++ == want) { next = r.transform; break; }
                }
                else
                {
                    for (int i = 0; i < cur.childCount; i++)
                    {
                        var c = cur.GetChild(i);
                        if (c.name == name && seen++ == want) { next = c; break; }
                    }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }
    }
}
