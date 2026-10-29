using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Events;

namespace IronNestCoop.QA
{
    /// <summary>
    /// "Who calls this method?" for scene wiring: finds every UnityEvent on every loaded component whose
    /// persistent listeners call <c>method</c>. Scene wiring is invisible in the code dump (CallerCount 0),
    /// so this is how buttons, levers and animation hooks are found at runtime. QA/RE use only.
    /// </summary>
    public static class Wiring
    {
        static readonly Dictionary<string, Type> TypeCache = new();
        static Assembly _game;

        static Type ManagedType(string il2cppFullName)
        {
            if (TypeCache.TryGetValue(il2cppFullName, out var t)) return t;
            _game ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
            t = _game?.GetType(il2cppFullName.Replace('/', '+'));
            TypeCache[il2cppFullName] = t;
            return t;
        }

        public struct Hit { public Component Owner; public string Event; public string Target; }

        public static List<Hit> Callers(string method, string targetTypeContains = null)
        {
            var hits = new List<Hit>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<MonoBehaviour>(), true))
            {
                var mb = o.TryCast<MonoBehaviour>();
                if (mb == null) continue;
                var type = ManagedType(mb.GetIl2CppType().FullName);
                if (type == null) continue;
                object typed;
                try { typed = Activator.CreateInstance(type, mb.Pointer); } catch { continue; }
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                    UnityEventBase ev;
                    try { ev = p.GetValue(typed) as UnityEventBase; } catch { continue; }
                    if (ev == null) continue;
                    int n;
                    try { n = ev.GetPersistentEventCount(); } catch { continue; }
                    for (int i = 0; i < n; i++)
                    {
                        if (ev.GetPersistentMethodName(i) != method) continue;
                        var target = ev.GetPersistentTarget(i);
                        string tname = target == null ? "null" : target.GetIl2CppType().Name + " on " + target.name;
                        if (targetTypeContains != null && !tname.Contains(targetTypeContains)) continue;
                        hits.Add(new Hit { Owner = mb, Event = p.Name, Target = tname });
                    }
                }
            }
            return hits;
        }

        /// <summary>Every persistent listener on the component's own UnityEvents: "event: Type.Method on object".</summary>
        public static List<string> ListenersOf(MonoBehaviour mb)
        {
            var list = new List<string>();
            var type = ManagedType(mb.GetIl2CppType().FullName);
            if (type == null) return list;
            object typed;
            try { typed = Activator.CreateInstance(type, mb.Pointer); } catch { return list; }
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!typeof(UnityEventBase).IsAssignableFrom(p.PropertyType) || p.GetIndexParameters().Length > 0) continue;
                UnityEventBase ev;
                try { ev = p.GetValue(typed) as UnityEventBase; } catch { continue; }
                if (ev == null) continue;
                int n;
                try { n = ev.GetPersistentEventCount(); } catch { continue; }
                for (int i = 0; i < n; i++)
                {
                    var target = ev.GetPersistentTarget(i);
                    list.Add($"{p.Name}: {(target == null ? "null" : target.GetIl2CppType().Name)}.{ev.GetPersistentMethodName(i)} on {(target == null ? "-" : target.name)}");
                }
            }
            return list;
        }

        public static string Describe(List<Hit> hits) =>
            hits.Count == 0 ? "none" : string.Join("; ", hits.Select(h => $"{h.Owner.GetIl2CppType().Name} '{Util.ScenePath.Of(h.Owner.transform)}'.{h.Event} -> {h.Target}"));
    }
}
