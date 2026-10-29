using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>Order-independent fingerprint of every unit: id, position (cm), state, health.</summary>
        static string WorldHash(out int count)
        {
            var rows = new List<string>();
            var fm = FireMission.Instance;
            if (fm?.Entities != null)
                foreach (var kv in fm.Entities)
                {
                    var e = kv.Value;
                    if (e == null) continue;
                    rows.Add($"{e.ID}|{Mathf.RoundToInt(e.Position.x * 100)},{Mathf.RoundToInt(e.Position.y * 100)},{Mathf.RoundToInt(e.Position.z * 100)}|{(int)e.State}|{e.Health}");
                }
            rows.Sort(StringComparer.Ordinal);
            count = rows.Count;
            using var sha = System.Security.Cryptography.SHA1.Create();
            return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", rows))))[..12];
        }

        static int UnitCount() { WorldHash(out int n); return n; }

        static List<MapEntity> Units()
        {
            var list = new List<MapEntity>();
            var fm = FireMission.Instance;
            if (fm?.Entities != null) foreach (var kv in fm.Entities) if (kv.Value != null) list.Add(kv.Value);
            return list;
        }

        static IEnumerable<object> StartMission(Bot bot, string missionId)
        {
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var op = MissionFlow.FindOperation("Base_Campaign");
            var mission = MissionFlow.FindMission(op, missionId);
            if (mission == null && op != null)
                bot.Note("missions in Base_Campaign: " + string.Join(", ", System.Linq.Enumerable.Select(System.Linq.Enumerable.Where(op.Missions.ToArray(), n => n?.Mission != null), n => n.Mission.MissionID)));
            bot.Check(mission != null, $"mission {missionId} exists");
            if (mission == null) yield break;
            MissionManager.Instance.StartOperation(op, mission);
            foreach (var x in bot.Until(() => FireMission.Instance != null && MissionFlow.Phase == MissionManager.GamePhase.MissionActive, 120, "mission scene loaded")) yield return x;
        }

        /// <summary>Waits until the unit count has not changed for `quiet` seconds.</summary>
        static IEnumerable<object> UntilStable(Bot bot, double quiet, double timeout, string what)
        {
            int last = -1; double since = bot.Now, end = bot.Now + timeout;
            while (bot.Now < end)
            {
                int n = UnitCount();
                if (n != last) { last = n; since = bot.Now; }
                else if (n > 0 && bot.Now - since >= quiet) { bot.Note($"{what}: {n} units stable"); yield break; }
                yield return null;
            }
            bot.Check(false, $"{what}: unit count never settled (last {last})");
        }

        static IEnumerable<object> EntitiesHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in UntilStable(bot, 6, 120, "mission spawns")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Signal("hash1", WorldHash(out int n1));
            bot.Note($"host world: {n1} units");
            foreach (var x in bot.Until(() => bot.HasSignal("hash1-checked"), 30, "guest compared world 1")) yield return x;

            // Reproduce the reported bug: random reinforcement spawns relative to a unit, then combat.
            var fm = FireMission.Instance;
            var anchor = Units().FirstOrDefault(e => e != null && e.Location != null);
            bot.Check(anchor != null, "found an anchor unit");
            if (anchor == null) yield break;
            var rng = new System.Random();
            for (int i = 0; i < 2; i++)
            {
                var world = anchor.Location.transform.position + new Vector3((float)rng.NextDouble() * 2 - 1, 0, (float)rng.NextDouble() * 2 - 1) * 0.3f;
                var e = fm.CreateMapEntity("BotArtillery" + i, anchor.Name, 100 + i, world, anchor.Role, 1, 0, 1, MapEntityStates.None, anchor.Icon);
                fm.RegisterMapEntity(e);
            }
            var victim = Units().FirstOrDefault(e => e != null && e.Location != null && e.ID != anchor.ID && e.IsAlive);
            var shell = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>()).Select(o => o.TryCast<ShellDefinition>()).FirstOrDefault(s => s != null && s.Damage > 0);
            if (victim != null && shell != null) victim.Location.ApplyDamage(shell, 1);
            bot.Note($"spawned 2 random units near {anchor.ID}; damaged {victim?.ID} with {shell?.name}");
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Signal("hash2", WorldHash(out int n2));
            bot.Note($"host world after reinforcements: {n2} units");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> EntitiesGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "followed host into mission")) yield return x;
            WorldEntities.Instance.ResetCounters();
            foreach (var x in bot.Until(() => bot.HasSignal("hash1"), 180, "host world hash 1")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            string h1 = WorldHash(out int n1);
            bot.Check(h1 == bot.SignalValue("hash1"), $"world identical after mission spawns ({n1} units, {h1} vs host {bot.SignalValue("hash1")})");
            bot.Signal("hash1-checked");
            foreach (var x in bot.Until(() => bot.HasSignal("hash2"), 60, "host world hash 2")) yield return x;
            foreach (var x in bot.Wait(1.5)) yield return x;
            string h2 = WorldHash(out int n2);
            bot.Check(h2 == bot.SignalValue("hash2"), $"world identical after random reinforcements + damage ({n2} units, {h2} vs host {bot.SignalValue("hash2")})");
            var we = WorldEntities.Instance;
            bot.Note($"guest stats: spawned {we.Spawned}, digests {we.DigestChecks}, mismatches {we.DigestMismatches}, missing {we.DigestMissing}, corrected {we.Corrected}, graph calls skipped {GraphGate.Skipped}");
            bot.Check(GraphGate.Skipped > 0, "guest really suppressed its own mission logic (mode check)");
            bot.Check(we.DigestChecks >= 3, "digest checks ran");
            bot.Check(we.DigestMismatches == 0 && we.DigestMissing == 0, "no digest corrections were needed (replication alone kept the worlds equal)");
            foreach (var x in bot.Shot("guest-entities")) yield return x;
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
