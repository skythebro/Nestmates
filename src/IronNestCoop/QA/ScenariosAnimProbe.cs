using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>
        /// One copy: every Animator in the turret with its parameters, so the animator mirror can be designed from
        /// facts (how many, which parameter types, which are driven continuously). Writes local/animprobe.txt.
        /// </summary>
        /// <summary>One copy: every component under objects whose path contains -coop-filter (default the requisition
        /// console), with type names, so an unknown control can be identified. Writes compprobe.txt.</summary>
        static IEnumerable<object> CompProbe(Bot bot)
        {
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => Computer != null, 120, "mission ready")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            string filter = LaunchArgs.Filter ?? "Requisition Console";
            var lines = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<MonoBehaviour>(), true))
            {
                var mb = o.TryCast<MonoBehaviour>();
                if (mb == null) continue;
                string path = Util.ScenePath.Of(mb.transform);
                if (path == null) continue;
                if (filter == "near-requisition")
                {
                    var slot = RequisitionConsoleManager.Instance?.RequisitionSlot;
                    if (slot == null) continue;
                    float d = Vector3.Distance(mb.transform.position, slot.transform.position);
                    string tn = mb.GetIl2CppType().Name;
                    if (d > 0.6f || tn == "StudioEventEmitter" || tn.Contains("Text") || tn.Contains("Localised") || tn.Contains("Punchcard") || tn == "DraggableItem") continue;
                    lines.Add($"{d:F2} m | {path} | {tn} | active {mb.isActiveAndEnabled}");
                    continue;
                }
                if (filter.StartsWith("types:")) { if (!filter.Substring(6).Split(',').Contains(mb.GetIl2CppType().Name)) continue; }
                else if (filter.StartsWith("type:")) { if (mb.GetIl2CppType().Name != filter.Substring(5)) continue; }
                else if (!path.Contains(filter)) continue;
                lines.Add($"{path} | {mb.GetIl2CppType().Name} | active {mb.isActiveAndEnabled}");
            }
            if (filter.StartsWith("types:"))   // prefabs that carry them (spawned later in a mission)
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>()))
                {
                    var mb = o.TryCast<MonoBehaviour>();
                    if (mb == null || (mb.gameObject.scene.name != null && mb.gameObject.scene.name != "")) continue;
                    if (!filter.Substring(6).Split(',').Contains(mb.GetIl2CppType().Name)) continue;
                    var t = mb.transform; var chain = new List<string>();
                    while (t != null) { chain.Insert(0, t.name); t = t.parent; }
                    lines.Add($"PREFAB: {string.Join("/", chain)} | {mb.GetIl2CppType().Name}");
                }
            if (filter.StartsWith("type:") || filter.StartsWith("asset:"))
                foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<MonoBehaviour>()))
                {
                    var mb = o.TryCast<MonoBehaviour>();
                    if (mb == null) continue;
                    if (filter.StartsWith("type:") && mb.GetIl2CppType().Name != filter.Substring(5)) continue;
                    if (filter.StartsWith("asset:"))
                    {
                        if (mb.gameObject.scene.name != null && mb.gameObject.scene.name != "") continue;   // prefabs only
                        var r = mb.transform; while (r.parent != null) r = r.parent;
                        if (!r.name.Contains(filter.Substring(6))) continue;
                    }
                    var t = mb.transform; var chain = new List<string>();
                    while (t != null) { chain.Insert(0, t.name); t = t.parent; }
                    lines.Add($"ASSET/ALL: {string.Join("/", chain)} | {mb.GetIl2CppType().Name} | scene '{mb.gameObject.scene.name}'");
                }
            lines.Sort();
            System.IO.File.WriteAllLines(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(LaunchArgs.ResultPath), "compprobe.txt"), lines);
            bot.Note($"{lines.Count} components under '{filter}'");
            bot.Check(lines.Count > 0, "components found");
            MissionManager.Instance.EndOperationAndReturnToMenu();
        }

        static IEnumerable<object> AnimProbe(Bot bot)
        {
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => Computer != null, 120, "mission ready")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;

            // the real input entry points exist in the interop proxy (compile-time check, run once)
            var any = Object.FindObjectOfType<LookAtTarget>();
            bot.Note($"real input entry points callable: UpdateHover and HandleClickDownFromManager on {any?.name}");

            var anims = Object.FindObjectsOfType(Il2CppType.Of<Animator>(), true).Select(o => o.TryCast<Animator>()).Where(a => a != null).ToList();
            var first = new Dictionary<string, string>();
            string Sample(Animator a)
            {
                var sb = new StringBuilder();
                if (!a.isActiveAndEnabled || a.runtimeAnimatorController == null) return "inactive";
                for (int i = 0; i < a.parameterCount; i++)
                {
                    var p = a.GetParameter(i);
                    sb.Append(p.type switch
                    {
                        AnimatorControllerParameterType.Bool => a.GetBool(p.nameHash) ? "1" : "0",
                        AnimatorControllerParameterType.Int => a.GetInteger(p.nameHash).ToString(),
                        AnimatorControllerParameterType.Float => a.GetFloat(p.nameHash).ToString("F2"),
                        _ => "t",
                    }).Append(',');
                }
                for (int l = 0; l < a.layerCount; l++) sb.Append('|').Append(a.GetCurrentAnimatorStateInfo(l).fullPathHash);
                return sb.ToString();
            }
            foreach (var a in anims) first[Util.ScenePath.Of(a.transform)] = Sample(a);
            foreach (var x in bot.Wait(5)) yield return x;   // what changes by itself in 5 s (idle animation, driven floats)

            var lines = new List<string>();
            int changing = 0, triggers = 0, floats = 0, bools = 0, ints = 0;
            foreach (var a in anims)
            {
                string path = Util.ScenePath.Of(a.transform);
                string now = Sample(a);
                bool moved = first.TryGetValue(path, out var was) && was != now;
                if (moved) changing++;
                var ps = new List<string>();
                if (a.runtimeAnimatorController != null)
                    for (int i = 0; i < a.parameterCount; i++)
                    {
                        var p = a.GetParameter(i);
                        ps.Add($"{p.name}:{p.type}");
                        if (p.type == AnimatorControllerParameterType.Trigger) triggers++;
                        else if (p.type == AnimatorControllerParameterType.Float) floats++;
                        else if (p.type == AnimatorControllerParameterType.Bool) bools++;
                        else ints++;
                    }
                lines.Add($"{path} | ctrl {a.runtimeAnimatorController?.name} | active {a.isActiveAndEnabled} | layers {a.layerCount} | {(moved ? "CHANGES BY ITSELF" : "still")} | {string.Join(" ", ps)}");
            }
            lines.Sort();
            System.IO.File.WriteAllLines(System.IO.Path.Combine(LaunchArgs.ResultPath != null ? System.IO.Path.GetDirectoryName(LaunchArgs.ResultPath) : ".", "animprobe.txt"), lines);
            bot.Note($"{anims.Count} animators: {bools} bool, {ints} int, {floats} float, {triggers} trigger parameters; {changing} change by themselves in 5 s");
            bot.Check(anims.Count > 0, "animators found");
            if (any != null && LaunchArgs.Mutant("never")) { any.UpdateHover(true, true); any.HandleClickDownFromManager(any.Cast<Interactable>()); }
            MissionManager.Instance.EndOperationAndReturnToMenu();
        }
    }
}
