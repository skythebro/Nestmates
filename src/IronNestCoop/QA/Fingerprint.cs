using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// What a player sees of the turret, as comparable text: every animator's bool/int parameters and settled
    /// state, every button's lit/active state, lamps and lights, and which parts of the room are shown. Two
    /// machines that show the same thing produce the same fingerprint; a difference names the object.
    /// </summary>
    public static class Fingerprint
    {
        // each player's own things, or things that move by themselves differently on each machine
        static readonly string[] Skip =
        {
            "Main Camera", "IronNestCoop", "Coop ", ":Canvas", "/Canvas", "Main Menu", "Camera", "NewsPaper", "Loading Bar", "Fade",
            "Cat", "Tutorial Cutaway", "cinamatic", "Mission Load", "Console UI prompt", "Hold Enter",
        };
        static readonly HashSet<string> SkipParams = new() { "IsHoverdOver", "IsWorn", "typing" };

        public static bool Skipped(string path) => path == null || Skip.Any(path.Contains);

        public static Dictionary<string, string> Capture()
        {
            var fp = new Dictionary<string, string>();
            var sb = new StringBuilder();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Animator>(), false))
            {
                var a = o.TryCast<Animator>();
                if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) continue;
                string path = Util.ScenePath.Of(a.transform);
                if (Skipped(path)) continue;
                sb.Clear();
                for (int i = 0; i < a.parameterCount; i++)
                {
                    var p = a.GetParameter(i);
                    if (SkipParams.Contains(p.name)) continue;
                    if (p.type == AnimatorControllerParameterType.Bool) sb.Append(p.name).Append('=').Append(a.GetBool(p.nameHash) ? 1 : 0).Append(' ');
                    else if (p.type == AnimatorControllerParameterType.Int) sb.Append(p.name).Append('=').Append(a.GetInteger(p.nameHash)).Append(' ');
                }
                // the state it rests in (a one-shot that is still playing shows as the state it plays)
                var st = a.IsInTransition(0) ? a.GetNextAnimatorStateInfo(0) : a.GetCurrentAnimatorStateInfo(0);
                sb.Append("state ").Append(st.shortNameHash);
                fp["anim " + path] = sb.ToString();
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), false))
            {
                var b = o.TryCast<LookAtTarget>();
                string path = b == null ? null : Util.ScenePath.Of(b.transform);
                if (Skipped(path)) continue;
                fp["button " + path] = b.GetActive() ? "active" : "inactive";
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<SwingLightFlicker>(), false))
            {
                var l = o.TryCast<SwingLightFlicker>();
                string path = l == null ? null : Util.ScenePath.Of(l.transform);
                if (Skipped(path)) continue;
                // what decides whether it shines (flicker aside): switched by hand, emergency override, power
                fp["lamp " + path] = $"{(l.IsManuallyOff() ? "switched off" : "on")} override {(l._emergencyPlayerOverride ? 1 : 0)} power {(l.IsEffectivelyPowered() ? 1 : 0)}";
            }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Light>(), false))
            {
                var l = o.TryCast<Light>();
                string path = l == null ? null : Util.ScenePath.Of(l.transform);
                if (Skipped(path)) continue;
                if (l.GetComponentInParent<SwingLightFlicker>() != null) continue;   // flickers at random; its "lamp" entry says on/off
                fp["light " + path] = l.enabled ? "on" : "off";
            }
            // which parts of the room show: renderers that are switched on, counted per area (3 path levels)
            var shown = new Dictionary<string, int>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<Renderer>(), false))
            {
                var r = o.TryCast<Renderer>();
                if (r == null || !r.enabled) continue;
                if (r.GetComponentInParent<SwingLightFlicker>() != null) continue;   // bulbs flicker at random
                string path = Util.ScenePath.Of(r.transform);
                if (Skipped(path)) continue;
                int cut = -1;
                for (int i = 0, n = 0; i < path.Length; i++) if (path[i] == '/' && ++n == 3) { cut = i; break; }
                string area = cut > 0 ? path.Substring(0, cut) : path;
                shown[area] = shown.TryGetValue(area, out var c) ? c + 1 : 1;
            }
            foreach (var kv in shown) fp["shown " + kv.Key] = kv.Value + " parts";
            return fp;
        }

        public static string Encode(Dictionary<string, string> fp) => string.Join("\n", fp.OrderBy(k => k.Key).Select(k => k.Key + "\t" + k.Value));

        public static Dictionary<string, string> Decode(string s)
        {
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var line in s.Split('\n'))
            {
                int t = line.IndexOf('\t');
                if (t > 0) d[line.Substring(0, t)] = line.Substring(t + 1);
            }
            return d;
        }

        /// <summary>"what: here X, there Y" for every key that differs (keys in <paramref name="ignore"/> left out).</summary>
        public static List<string> Diff(Dictionary<string, string> here, Dictionary<string, string> there, ICollection<string> ignore = null)
        {
            var list = new List<string>();
            foreach (var k in here.Keys.Union(there.Keys))
            {
                if (ignore != null && ignore.Contains(k)) continue;
                here.TryGetValue(k, out var a); there.TryGetValue(k, out var b);
                if (a != b) list.Add($"{k}: here {a ?? "(none)"}, host {b ?? "(none)"}");
            }
            list.Sort();
            return list;
        }

        public static IEnumerable<string> Keys(List<string> diffs) => diffs.Select(d => d.Substring(0, d.IndexOf(": here ")));
    }
}
