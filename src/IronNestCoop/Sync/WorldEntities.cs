using System;
using System.Collections.Generic;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Map entities (enemy and friendly units) are host-authoritative. The host captures each
    /// spawn, move, state change and health change at FireMission's choke points and sends the
    /// result; guests apply it with the same game calls. A digest of every unit every second
    /// catches anything that slipped through and corrects it within a second, not minutes.
    /// </summary>
    public sealed class WorldEntities : SyncModule
    {
        public override string Name => "entities";
        public static WorldEntities Instance { get; private set; }

        struct MoveArgs { public Vector3 World; public bool Continuous; public float Span; public double Start, End; }

        readonly Dictionary<string, MoveArgs> _lastMove = new();   // host: last move per unit, for snapshots
        readonly HashSet<string> _announced = new();                // host: units whose spawn was sent
        int _announcedEpoch = -1;
        float _nextDigest;

        /// <summary>Guest diagnostics for tests and the debug overlay.</summary>
        public int DigestMismatches, DigestMissing, DigestChecks, Spawned, Corrected, RevealsApplied;
        public float WorstErrorSeen;

        public WorldEntities() { Instance = this; }

        public override void Register(Session s)
        {
            s.On((ushort)Msg.EntitySpawn, OnSpawn);
            s.On((ushort)Msg.EntityMove, OnMove);
            s.On((ushort)Msg.EntityState, OnState);
            s.On((ushort)Msg.EntityDigest, OnDigest);
            s.On((ushort)Msg.EntityResync, OnResyncRequest);
            s.On((ushort)Msg.EntityReveal, OnReveal);
        }

        public override void OnSessionStart() { _lastMove.Clear(); _announced.Clear(); _announcedEpoch = -1; ResetCounters(); }

        public void ResetCounters() { DigestMismatches = DigestMissing = DigestChecks = Spawned = Corrected = RevealsApplied = 0; WorstErrorSeen = 0; }

        static FireMission FM => FireMission.Instance;
        static bool GuestReady => Coop.IsGuest && WorldReady.LocalReady;

        internal static IEnumerable<MapEntity> All()
        {
            var fm = FM;
            if (fm == null || fm.Entities == null) yield break;
            foreach (var kv in fm.Entities) if (kv.Value != null) yield return kv.Value;
        }

        static MapEntity Find(string id)
        {
            var fm = FM;
            if (fm == null || id == null) return null;
            return fm.Entities != null && fm.Entities.ContainsKey(id) ? fm.Entities[id] : null;
        }

        // ------------------------------------------------------------------ encoding

        static void WriteEntity(NetWriter w, MapEntity e)
        {
            var d = MissionSaveManager.CaptureMapEntity(e);
            w.Str(d.Id); w.Str(d.RawId); w.Str(d.NameKey); w.Str(d.NameRaw); w.VarI(d.IdIndex);
            w.Str(d.Icon); w.Str(d.IconRawName); w.VarI(d.Role);
            w.Vec3(e.Position.x, e.Position.y, e.Position.z);
            w.VarI(d.State); w.VarI(d.MaxHealth); w.VarI(d.Armour); w.VarI(d.Stars); w.VarI(d.Scale); w.VarI(d.Health);
            int n = d.ImmuneShells?.Count ?? 0;
            w.VarU((ulong)n);
            for (int i = 0; i < n; i++) w.Str(d.ImmuneShells[i]);
            w.Bool(d.VisualActive);
            V3(w, d.VisualLocalPosition); V3(w, d.VisualRootLocalPosition);
            var q = d.VisualLocalRotation;
            w.Quat(q.X, q.Y, q.Z, q.W);
        }

        static void V3(NetWriter w, SaveVector3 v) => w.Vec3(v.X, v.Y, v.Z);

        static SaveVector3 RV3(NetReader r) => new SaveVector3 { X = r.F32(), Y = r.F32(), Z = r.F32() };

        static MapEntitySaveData ReadEntity(NetReader r, out Vector3 pos)
        {
            var d = new MapEntitySaveData
            {
                Id = r.Str(256), RawId = r.Str(256), NameKey = r.Str(256), NameRaw = r.Str(1024), IdIndex = (int)r.VarI(),
                Icon = r.Str(256), IconRawName = r.Str(256), Role = (int)r.VarI(),
            };
            pos = new Vector3(r.F32(), r.F32(), r.F32());
            d.Position = new SaveVector3 { X = pos.x, Y = pos.y, Z = pos.z };
            d.State = (int)r.VarI(); d.MaxHealth = (int)r.VarI(); d.Armour = (int)r.VarI(); d.Stars = (int)r.VarI(); d.Scale = (int)r.VarI(); d.Health = (int)r.VarI();
            int n = r.Count(64, "immune shells");
            d.ImmuneShells = new Il2CppSystem.Collections.Generic.List<string>();
            for (int i = 0; i < n; i++) d.ImmuneShells.Add(r.Str(256));
            d.VisualActive = r.Bool();
            d.VisualLocalPosition = RV3(r); d.VisualRootLocalPosition = RV3(r);
            d.VisualLocalRotation = new SaveQuaternion { X = r.F32(), Y = r.F32(), Z = r.F32(), W = r.F32() };
            return d;
        }

        // ------------------------------------------------------------------ host capture

        internal void HostSpawned(MapEntity e)
        {
            if (!Coop.IsHost || e == null) return;
            EpochCheck();
            // Mirror the game exactly: a duplicate ID is not registered (the dictionary keeps the first).
            var kept = Find(e.ID);
            if (kept == null || kept.Pointer != e.Pointer)
            {
                CoopLog.Info($"[entities] host: duplicate unit id '{e.ID}' was not registered by the game; not replicated");
                return;
            }
            _announced.Add(e.ID);
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            WriteEntity(w, e);
            WorldReady.SendToReady(Msg.EntitySpawn, w);
            CoopLog.Debug($"[entities] host spawn {e.ID} at {e.Position}");
        }

        internal void HostMoved(MapEntity e, Vector3 world, bool continuous, float span, double start, double end)
        {
            if (!Coop.IsHost || e == null) return;
            _lastMove[e.ID] = new MoveArgs { World = world, Continuous = continuous, Span = span, Start = start, End = end };
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.Str(e.ID); w.U8(0);
            w.Vec3(world.x, world.y, world.z); w.Bool(continuous); w.F32(span); w.F64(start); w.F64(end);
            WorldReady.SendToReady(Msg.EntityMove, w);
        }

        internal void HostMoveEnded(MapEntity e, bool cancelled)
        {
            if (!Coop.IsHost || e == null) return;
            _lastMove.Remove(e.ID);
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.Str(e.ID); w.U8(cancelled ? (byte)2 : (byte)1);
            w.Vec3(e.Position.x, e.Position.y, e.Position.z);
            WorldReady.SendToReady(Msg.EntityMove, w);
        }

        internal void HostStateChanged(MapEntity e)
        {
            if (!Coop.IsHost || e == null) return;
            EpochCheck();
            // The game sets the initial state inside RegisterMapEntity, before our spawn postfix:
            // the spawn message carries that state, so do not send a state for an unannounced unit.
            if (!_announced.Contains(e.ID)) return;
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.Str(e.ID); w.VarI((int)e.State); w.VarI(e.Health);
            WorldReady.SendToReady(Msg.EntityState, w);
        }

        void EpochCheck()
        {
            int ep = MissionFlow.Instance.Epoch;
            if (ep == _announcedEpoch) return;
            _announcedEpoch = ep;
            _announced.Clear();
            _lastMove.Clear();
        }

        public override void SendSnapshot(byte slot)
        {
            EpochCheck();
            var w = new NetWriter(4096);
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            var list = new List<MapEntity>(All());
            w.VarU((ulong)list.Count);
            foreach (var e in list) WriteEntity(w, e);
            w.VarU((ulong)_lastMove.Count);
            foreach (var kv in _lastMove)
            {
                w.Str(kv.Key);
                var m = kv.Value;
                w.Vec3(m.World.x, m.World.y, m.World.z); w.Bool(m.Continuous); w.F32(m.Span); w.F64(m.Start); w.F64(m.End);
            }
            Coop.Session.Send((ushort)Msg.EntityResync, w, Delivery.Reliable, slot);
            CoopLog.Info($"[entities] snapshot of {list.Count} units ({_lastMove.Count} moving) -> slot {slot}");
        }

        public override void Tick(float dt)
        {
            if (!Coop.IsHost || FM == null || Time.unscaledTime < _nextDigest) return;
            _nextDigest = Time.unscaledTime + 1f;
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            var list = new List<MapEntity>(All());
            w.VarU((ulong)list.Count);
            foreach (var e in list)
            {
                w.Str(e.ID);
                w.Vec3(e.Position.x, e.Position.y, e.Position.z);
                w.VarI((int)e.State); w.VarI(e.Health);
            }
            WorldReady.SendToReady(Msg.EntityDigest, w, Delivery.Unreliable);
            if (!LaunchArgs.Mutant("noreveal")) SendReveals();
        }

        // ------------------------------------------------------------------ map icons (spotting)

        // A unit's map icon only shows once recon has spotted it, and then stays where it was last seen. The game
        // decides that in each unit's own scan (EntityLocation), which needs mission logic that only runs on the
        // host, so guests never spotted anything. Once a second the host sends every unit's icon state and
        // position, and guests put their icons the same way with the game's own restore call.

        internal static bool IconShown(EntityLocation loc) => loc.VisualRoot != null && loc.VisualRoot.activeSelf;

        internal static Vector3 IconPos(EntityLocation loc) => loc.VisualRoot != null ? loc.VisualRoot.transform.position : loc.transform.position;

        void SendReveals()
        {
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            var list = new List<EntityLocation>();
            foreach (var e in All()) if (e.Location != null) list.Add(e.Location);
            w.VarU((ulong)list.Count);
            foreach (var loc in list)
            {
                var p = IconPos(loc);
                w.Str(loc.Entity.ID); w.Bool(IconShown(loc)); w.Vec3(p.x, p.y, p.z);
            }
            WorldReady.SendToReady(Msg.EntityReveal, w, Delivery.Unreliable);
        }

        void OnReveal(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || !ForCurrentEpoch(r)) return;
            int n = r.Count(2048, "unit icons");
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(256);
                bool shown = r.Bool();
                var pos = new Vector3(r.F32(), r.F32(), r.F32());
                if (!(float.IsFinite(pos.x) && float.IsFinite(pos.y) && float.IsFinite(pos.z))) continue;
                var loc = Find(id)?.Location;
                if (loc == null) continue;
                if (IconShown(loc) == shown && (!shown || (IconPos(loc) - pos).sqrMagnitude < 1e-4f)) continue;
                Coop.Apply("icon " + id, () => { loc.RestoreVisualState(shown, pos); RevealsApplied++; });
                CoopLog.Debug($"[entities] icon {id} {(shown ? "shown" : "hidden")} like the host");
            }
        }

        // ------------------------------------------------------------------ guest apply

        bool ForCurrentEpoch(NetReader r) => (int)r.VarU() == MissionFlow.Instance.Epoch && GuestReady && FM != null;

        void OnSpawn(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || !ForCurrentEpoch(r)) return;
            var d = ReadEntity(r, out var pos);
            ApplySpawn(d, pos);
        }

        void ApplySpawn(MapEntitySaveData d, Vector3 pos)
        {
            var existing = Find(d.Id);
            if (existing != null) { ApplyCorrection(existing, pos, (MapEntityStates)d.State, d.Health); return; }
            Coop.Apply("spawn " + d.Id, () =>
            {
                var e = MissionSaveManager.RestoreMapEntity(d);
                e.Position = pos;
                FM.RegisterMapEntity(e);
                if (e.Location != null) e.Location.RestoreVisualState(d.VisualActive, pos);
                Spawned++;
            });
        }

        void OnMove(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || !ForCurrentEpoch(r)) return;
            var e = Find(r.Str(256));
            byte kind = r.U8();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            if (e == null) { RequestResync("move for unknown unit"); return; }
            if (kind == 0)
            {
                bool cont = r.Bool(); float span = r.F32(); double start = r.F64(), end = r.F64();
                Coop.Apply("move " + e.ID, () => FM.MoveMapEntity(e, pos, cont, span, start, end));
            }
            else if (kind == 1) Coop.Apply("move done " + e.ID, () => { FM.CompleteEntityMovement(e); ApplyCorrection(e, pos, e.State, e.Health); });
            else Coop.Apply("move cancel " + e.ID, () => { FM.CancelEntityMovement(e); ApplyCorrection(e, pos, e.State, e.Health); });
        }

        void OnState(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || !ForCurrentEpoch(r)) return;
            var e = Find(r.Str(256));
            var state = (MapEntityStates)(int)r.VarI();
            int health = (int)r.VarI();
            if (e == null) { RequestResync("state for unknown unit"); return; }
            Coop.Apply("state " + e.ID, () =>
            {
                e.Health = health;
                if (e.State != state) FM.SetEntityState(e, state);
            });
        }

        void OnResyncRequest(in MsgContext c, NetReader r)
        {
            if (Coop.IsHost) { SendSnapshot(c.FromSlot); return; }  // a guest asked
            if (!c.FromHost || (int)r.VarU() != MissionFlow.Instance.Epoch || FM == null) return;
            int n = r.Count(2048, "entities");
            var seen = new HashSet<string>();
            for (int i = 0; i < n; i++)
            {
                var d = ReadEntity(r, out var pos);
                seen.Add(d.Id);
                ApplySpawn(d, pos);
            }
            int m = r.Count(2048, "entity moves");
            for (int i = 0; i < m; i++)
            {
                var e = Find(r.Str(256));
                var world = new Vector3(r.F32(), r.F32(), r.F32());
                bool cont = r.Bool(); float span = r.F32(); double start = r.F64(), end = r.F64();
                if (e != null && end > MissionClock.TimeAsDouble)
                    Coop.Apply("resume move " + e.ID, () => FM.MoveMapEntity(e, world, cont, span, start, end));
            }
            int extra = 0;
            foreach (var e in All()) if (!seen.Contains(e.ID)) extra++;
            CoopLog.Info($"[entities] snapshot applied: {n} units, {m} moving, {extra} local-only units");
        }

        float _lastResyncAsk;

        void RequestResync(string why)
        {
            if (Time.unscaledTime - _lastResyncAsk < 2f) return;
            _lastResyncAsk = Time.unscaledTime;
            CoopLog.Info($"[entities] asking host for a resync ({why})");
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch);
            Coop.SendToHost(Msg.EntityResync, w);
        }

        void OnDigest(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || !ForCurrentEpoch(r)) return;
            int n = r.Count(2048, "entities in the digest");
            DigestChecks++;
            int missing = 0;
            for (int i = 0; i < n; i++)
            {
                string id = r.Str(256);
                var hostPos = new Vector3(r.F32(), r.F32(), r.F32());
                var state = (MapEntityStates)(int)r.VarI();
                int hp = (int)r.VarI();
                var e = Find(id);
                if (e == null) { missing++; continue; }
                bool moving = (state & MapEntityStates.Moving) != 0;
                float err = (e.Position - hostPos).magnitude;
                WorstErrorSeen = Mathf.Max(WorstErrorSeen, moving ? 0 : err);
                // Moving units interpolate on the shared mission clock; only settle-time errors count.
                if ((!moving && err > 0.01f) || e.State != state || e.Health != hp)
                {
                    DigestMismatches++;
                    CoopLog.Info($"[entities] digest mismatch {id}: pos err {err:F3}, state {e.State} vs {state}, hp {e.Health} vs {hp} -> corrected");
                    ApplyCorrection(e, moving ? e.Position : hostPos, state, hp);
                }
            }
            if (missing > 0) { DigestMissing += missing; RequestResync($"{missing} units missing locally"); }
        }

        void ApplyCorrection(MapEntity e, Vector3 pos, MapEntityStates state, int hp)
        {
            Coop.Apply("correct " + e.ID, () =>
            {
                bool moved = (e.Position - pos).sqrMagnitude > 1e-6f;
                e.Position = pos;
                e.Health = hp;
                if (e.State != state) FM.SetEntityState(e, state);
                if (moved && e.Location != null) { e.Location.RestoreVisualState(e.Location.gameObject.activeSelf, pos); e.Location.OnEntityMoved(); Corrected++; }
            });
        }

        // ------------------------------------------------------------------ patches (host capture)

        [HarmonyPatch(typeof(FireMission), nameof(FireMission.RegisterMapEntity))]
        static class RegisterPatch { static void Postfix(MapEntity entity) { try { if (Coop.HostEmits) Instance?.HostSpawned(entity); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 344", e); } } }

        [HarmonyPatch(typeof(FireMission), nameof(FireMission.MoveMapEntity), typeof(MapEntity), typeof(Vector3), typeof(bool), typeof(float), typeof(double), typeof(double))]
        static class MovePatch
        {
            static void Postfix(MapEntity entity, Vector3 worldPos, bool continousMovement, float timespan, double startedAt, double endsAt)
            { try { if (Coop.HostEmits) Instance?.HostMoved(entity, worldPos, continousMovement, timespan, startedAt, endsAt); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 349", e); } }
        }

        [HarmonyPatch(typeof(FireMission), nameof(FireMission.CompleteEntityMovement), typeof(MapEntity))]
        static class CompletePatch { static void Postfix(MapEntity entity) { try { if (Coop.HostEmits) Instance?.HostMoveEnded(entity, false); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 354", e); } } }

        [HarmonyPatch(typeof(FireMission), nameof(FireMission.CompleteEntityMovement), typeof(MapEntity), typeof(FireMission.RunningMovement))]
        static class CompleteInternalPatch { static void Postfix(MapEntity entity) { try { if (Coop.HostEmits) Instance?.HostMoveEnded(entity, false); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 357", e); } } }

        [HarmonyPatch(typeof(FireMission), nameof(FireMission.CancelEntityMovement))]
        static class CancelPatch { static void Postfix(MapEntity entity) { try { if (Coop.HostEmits) Instance?.HostMoveEnded(entity, true); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 360", e); } } }

        [HarmonyPatch(typeof(FireMission), nameof(FireMission.SetEntityState))]
        static class StatePatch { static void Postfix(MapEntity entity) { try { if (Coop.HostEmits) Instance?.HostStateChanged(entity); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 363", e); } } }

        [HarmonyPatch(typeof(EntityLocation), nameof(EntityLocation.ApplyDamage))]
        static class DamagePatch
        {
            static bool Prefix() => !GraphGate.Suppress;   // guests never resolve damage
            static void Postfix(EntityLocation __instance) { try { if (Coop.HostEmits) Instance?.HostStateChanged(__instance.Entity); } catch (System.Exception e) { CoopLog.PatchFailed("WorldEntities.Postfix line 369", e); } }
        }
    }
}
