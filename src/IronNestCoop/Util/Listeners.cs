using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Events;

namespace IronNestCoop.Util
{
    /// <summary>
    /// The persistent (scene-wired) listeners of a component's UnityEvents. Scene wiring is invisible in the code
    /// dump, so this is how the mod tells what a button actually does (e.g. "only zooms a sheet into view").
    /// </summary>
    public static class Listeners
    {
        public struct L { public string Event, TargetType, Method, TargetName; }

        static readonly Dictionary<string, Type> TypeCache = new();
        static Assembly _game;

        static Type ManagedType(string il2cppFullName)
        {
            if (TypeCache.TryGetValue(il2cppFullName, out var t)) return t;
            if (_game == null)
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "Assembly-CSharp") { _game = a; break; }
            t = _game?.GetType(il2cppFullName.Replace('/', '+'));
            TypeCache[il2cppFullName] = t;
            return t;
        }

        public static List<L> Of(MonoBehaviour mb)
        {
            var list = new List<L>();
            if (mb == null) return list;
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
                    list.Add(new L
                    {
                        Event = p.Name, Method = ev.GetPersistentMethodName(i),
                        TargetType = target == null ? null : target.GetIl2CppType().Name, TargetName = target == null ? null : target.name,
                    });
                }
            }
            return list;
        }
    }
}
