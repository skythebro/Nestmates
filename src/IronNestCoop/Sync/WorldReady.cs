using System.Collections.Generic;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Per-mission readiness handshake. A guest reports "ready" once its mission scene finished
    /// loading (MissionManager.StartMissionRuntime). Until then the host sends it no world updates;
    /// on "ready" every module sends that guest a full snapshot. This one path covers slow loaders,
    /// restarts and players who join in the middle of a mission.
    /// </summary>
    public sealed class WorldReady : SyncModule
    {
        public override string Name => "ready";
        public static WorldReady Instance { get; private set; }

        /// <summary>Guest: our mission scene is loaded for the current epoch.</summary>
        public static bool LocalReady { get; private set; }
        static readonly HashSet<byte> ReadySlots = new();
        public static bool IsReady(byte slot) => ReadySlots.Contains(slot);
        static int _readyEpoch = -1;

        public WorldReady() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.WorldReady, OnReady);

        public override void OnSessionStart() { LocalReady = false; ReadySlots.Clear(); }
        public override void OnSessionEnd() { LocalReady = false; ReadySlots.Clear(); }
        public override void OnPlayerLeft(Player p) => ReadySlots.Remove(p.Slot);

        public static IEnumerable<byte> Slots => ReadySlots;

        /// <summary>Host: a new mission epoch began; nobody is ready for it yet.</summary>
        public static void NewEpoch(int epoch)
        {
            if (_readyEpoch == epoch) return;
            _readyEpoch = epoch;
            ReadySlots.Clear();
            LocalReady = false;
        }

        public static void Unready() { LocalReady = false; if (Coop.IsHost) ReadySlots.Clear(); }

        /// <summary>Host: send to every guest whose mission is loaded.</summary>
        public static void SendToReady(Msg type, NetWriter w, Delivery d = Delivery.Reliable)
        {
            if (!Coop.IsHost) return;
            foreach (var slot in ReadySlots) Coop.Session.Send((ushort)type, w, d, slot);
        }

        void OnReady(in MsgContext c, NetReader r)
        {
            if (!Coop.IsHost) return;
            int epoch = (int)r.VarU();
            if (epoch != MissionFlow.Instance.Epoch || MissionFlow.Phase != MissionManager.GamePhase.MissionActive)
            {
                CoopLog.Info($"[ready] slot {c.FromSlot} ready for stale epoch {epoch}");
                return;
            }
            ReadySlots.Add(c.FromSlot);
            CoopLog.Info($"[ready] slot {c.FromSlot} loaded mission epoch {epoch}; sending snapshots");
            foreach (var m in Coop.AllModules)
            {
                try { m.SendSnapshot(c.FromSlot); }
                catch (System.Exception e) { CoopLog.Error($"[{m.Name}] snapshot failed: {e}"); }
            }
        }

        [HarmonyPatch(typeof(MissionManager), nameof(MissionManager.StartMissionRuntime))]
        static class RuntimePatch
        {
            static void Postfix()
            {
                try
                {
                    if (!Coop.IsGuest) return;
                    LocalReady = true;
                    var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch);
                    Coop.SendToHost(Msg.WorldReady, w);
                    CoopLog.Info($"[ready] mission scene loaded, told host (epoch {MissionFlow.Instance.Epoch})");
                }
                catch (System.Exception e) { CoopLog.PatchFailed("WorldReady.Postfix line 76", e); }
            }
        }
    }
}
