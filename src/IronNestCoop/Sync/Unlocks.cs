using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Wire;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Unlockable scene objects (records, decorations, ...). Locked objects are inactive, and players progress at
    /// different speeds, so in the host's world the host's unlocks count: guests are told the host's set and the
    /// game's own check answers from it while the session lasts (their saved progression is not touched).
    /// </summary>
    public sealed class Unlocks : SyncModule
    {
        public override string Name => "unlocks";
        public static Unlocks Instance { get; private set; }

        static HashSet<string> _host;   // guest: host's unlocked ids (null = not known yet)
        public int Count => _host?.Count ?? -1;

        public Unlocks() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Unlocks, OnSet);
        public override void OnSessionStart() => _host = null;
        public override void OnSessionEnd() { _host = null; Refresh(); }

        static IEnumerable<string> SceneIds()
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<UnlockableSceneObject>(), true)) { var u = o.TryCast<UnlockableSceneObject>(); if (u != null && !string.IsNullOrEmpty(u.ObjectID)) yield return u.ObjectID; }
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<DestroyWhenSceneObjectUnlocked>(), true)) { var d = o.TryCast<DestroyWhenSceneObjectUnlocked>(); if (d != null && !string.IsNullOrEmpty(d._objectId)) yield return d._objectId; }
        }

        public override void SendSnapshot(byte slot) => Send(slot);

        void Send(byte? slot)
        {
            var pm = ProgressionManager.Instance;
            if (pm == null) return;
            var ids = new HashSet<string>();
            foreach (var id in SceneIds()) if (pm.IsSceneObjectUnlocked(id)) ids.Add(id);
            var w = Coop.Writer(); w.U8(1); w.VarU((ulong)ids.Count);
            foreach (var id in ids) w.Str(id);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Unlocks, w, Net.Transport.Delivery.Reliable, one);
            else WorldReady.SendToReady(Msg.Unlocks, w);
        }

        void OnSet(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || r.U8() != 1) return;
            int n = r.Count(4096, "unlocked objects");
            var set = new HashSet<string>();
            for (int i = 0; i < n; i++) set.Add(r.Str(256));
            _host = set;
            CoopLog.Info($"[unlocks] using the host's {set.Count} unlocked scene objects");
            Refresh();
        }

        static void Refresh()
        {
            try { UnlockableSceneObject.RefreshAll(); }
            catch (System.Exception e) { CoopLog.Warn("[unlocks] refresh failed: " + e.Message); }
        }

        [HarmonyPatch(typeof(ProgressionManager), nameof(ProgressionManager.IsSceneObjectUnlocked))]
        static class Check
        {
            static bool Prefix(string objectId, ref bool __result)
            {
                try
                {
                    if (!Coop.IsGuest || _host == null || objectId == null) return true;
                    __result = _host.Contains(objectId);
                    return false;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Unlocks.Prefix line 70", e); return true; }
            }
        }

        /// <summary>Something got unlocked in the host's world mid-session: tell the guests.</summary>
        [HarmonyPatch(typeof(ProgressionManager), nameof(ProgressionManager.UnlockSceneObject))]
        static class Unlocked { static void Postfix() { try { if (Coop.IsHost) Instance?.Send(null); } catch (System.Exception e) { CoopLog.PatchFailed("Unlocks.Postfix line 84", e); } } }
    }
}
