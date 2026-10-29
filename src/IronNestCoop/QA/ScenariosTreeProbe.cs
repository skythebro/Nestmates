using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using TMPro;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>
        /// One copy: the object tree under every object whose path contains one of -coop-filter's parts (';'
        /// separated), with components, on/off, shown text, and the persistent listeners of each component's events.
        /// "type:Name" parts pick the objects carrying that component. Writes treeprobe.txt next to the result.
        /// </summary>
        static IEnumerable<object> TreeProbe(Bot bot)
        {
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => Computer != null, 120, "mission ready")) yield return x;
            foreach (var x in bot.Wait(8)) yield return x;
            var parts = (LaunchArgs.Filter ?? "Settings menu").Split(';').Where(p => p.Length > 0).ToList();
            var roots = new List<Transform>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Transform>(), true))
            {
                var t = o.TryCast<Transform>();
                if (t == null || string.IsNullOrEmpty(t.gameObject.scene.name)) continue;
                string path = Util.ScenePath.Of(t) ?? "";
                bool hit = parts.Any(p => p.StartsWith("type:")
                    ? t.GetComponents<Component>().Any(c => c != null && c.GetIl2CppType().Name == p.Substring(5))
                    : path.EndsWith("/" + p) || path.EndsWith(":" + p));
                if (hit) roots.Add(t);
            }
            var sb = new StringBuilder();
            foreach (var r in roots.OrderBy(r => Util.ScenePath.Of(r)))
            {
                sb.AppendLine("##### " + Util.ScenePath.Of(r));
                Dump(r, 0, sb);
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(LaunchArgs.ResultPath), "treeprobe.txt"), sb.ToString());
            bot.Note($"{roots.Count} roots dumped for '{LaunchArgs.Filter}'");
            bot.Check(roots.Count > 0, "roots found");
            MissionManager.Instance.EndOperationAndReturnToMenu();
        }

        static void Dump(Transform t, int depth, StringBuilder sb)
        {
            if (depth > 14) return;
            var comps = new List<string>();
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null) continue;
                string n = c.GetIl2CppType().Name;
                if (n == "Transform") continue;
                var tmp = c.TryCast<TMP_Text>();
                if (tmp != null) n += $"\"{(tmp.text ?? "").Replace("\n", "\\n").Substring(0, System.Math.Min(40, (tmp.text ?? "").Length))}\"";
                var rt = c.TryCast<RectTransform>();
                if (rt != null) n = $"Rect({rt.rect.width:F0}x{rt.rect.height:F0})";
                var mb = c.TryCast<MonoBehaviour>();
                if (mb != null)
                {
                    if (!mb.enabled) n += "(off)";
                    try
                    {
                        var ls = Util.Listeners.Of(mb);
                        if (ls.Count > 0) n += "{" + string.Join("; ", ls.Select(l => $"{l.Event}->{l.TargetName}.{l.TargetType}.{l.Method}")) + "}";
                    }
                    catch { }
                    // a UI button (UnityEngine.UI, not the game's assembly): its click listeners directly
                    var btn = c.TryCast<UnityEngine.UI.Button>();
                    if (btn != null)
                        try
                        {
                            var ev = btn.onClick; var parts = new List<string>();
                            for (int i = 0; i < ev.GetPersistentEventCount(); i++)
                            { var tg = ev.GetPersistentTarget(i); parts.Add($"{(tg == null ? "-" : tg.name)}.{(tg == null ? "-" : tg.GetIl2CppType().Name)}.{ev.GetPersistentMethodName(i)}"); }
                            n += "{onClick->" + string.Join("; ", parts) + "}";
                        }
                        catch (System.Exception e) { n += "{onClick: " + e.Message + "}"; }
                }
                comps.Add(n);
            }
            sb.Append(new string(' ', depth * 2)).Append(t.gameObject.activeSelf ? "" : "[off] ").Append(t.name).Append("  ").AppendLine(string.Join(", ", comps));
            for (int i = 0; i < t.childCount; i++) Dump(t.GetChild(i), depth + 1, sb);
        }
    }
}
