using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>One copy: log what every button in the turret is wired to (coopqa.py solo buttonprobe).</summary>
    public static partial class Scenarios
    {
        static IEnumerable<object> ButtonProbe(Bot bot)
        {
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            int n = 0;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), true))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b == null) continue;
                var anim = b.animator;
                string a = anim == null ? "no animator" : $"animator on {anim.name} ({anim.runtimeAnimatorController?.name})";
                int rtDown = b.onClickDown?.m_Calls?.m_RuntimeCalls?.Count ?? -1, rtUp = b.onClickUp?.m_Calls?.m_RuntimeCalls?.Count ?? -1;
                bot.Note($"BUTTON {Util.ScenePath.Of(b.transform)} | {a} | runtime listeners down {rtDown} up {rtUp} | {string.Join(" | ", Wiring.ListenersOf(b))}");
                n++;
            }
            bot.Note($"{n} buttons listed");
        }
    }
}
