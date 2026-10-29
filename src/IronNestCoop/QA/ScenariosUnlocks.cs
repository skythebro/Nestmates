using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static UnlockableSceneObject Unlockable(string id) =>
            Object.FindObjectsOfType(Il2CppType.Of<UnlockableSceneObject>(), true).Select(o => o.TryCast<UnlockableSceneObject>()).FirstOrDefault(u => u != null && u.ObjectID == id);

        static IEnumerable<object> UnlocksHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var pm = ProgressionManager.Instance;
            var locked = Object.FindObjectsOfType(Il2CppType.Of<UnlockableSceneObject>(), true).Select(o => o.TryCast<UnlockableSceneObject>())
                .Where(u => u != null && !string.IsNullOrEmpty(u.ObjectID) && !pm.IsSceneObjectUnlocked(u.ObjectID) && u.GetComponentInChildren<RecordItem>(true) != null).ToList();
            bot.Note($"locked records here: {locked.Count}");
            bot.Check(locked.Count > 0, "a locked record exists to unlock");
            if (locked.Count == 0) yield break;
            var target = locked[0];
            bot.Note($"host unlocks {target.ObjectID} ({target.name})");
            pm.UnlockSceneObject(target.ObjectID);
            UnlockableSceneObject.RefreshAll();
            foreach (var x in bot.Wait(0.5)) yield return x;
            bot.Check(target.gameObject.activeSelf, "the record is out on the host's shelf");
            bot.Signal("unlocked", target.ObjectID);
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> UnlocksGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("unlocked"), 180, "host unlocked a record")) yield return x;
            string id = bot.SignalValue("unlocked");
            var u = Unlockable(id);
            foreach (var x in bot.Until(() => u != null && u.gameObject.activeSelf, 10, "the host's unlocked record is out on the shelf here")) yield return x;
            bot.Note($"guest uses the host's {Unlocks.Instance.Count} unlocks");
            // the guest's own saved progression is untouched: ask the game's save data, not the patched check
            var pm = ProgressionManager.Instance;
            bool own = pm.UserProgression?.UnlockedSceneObjects?.Contains(id) ?? false;
            bot.Check(!own, $"the guest's own progression did not gain {id}");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
