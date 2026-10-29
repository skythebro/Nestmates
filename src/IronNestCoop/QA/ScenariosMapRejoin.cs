using System.Collections.Generic;
using System.IO;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    /// <summary>
    /// Rejoin test on the tactical map: while the guest is away the host sends a recon plane and fires shells at
    /// two different spots; the guest joins again and must see the very same map: every impact and its crater
    /// photo (turned the same way), the correction arrows, the measuring arms where the last shell left them, the
    /// recon strips. Both also save a picture taken straight down on the map (shots/map-host.png, map-guest.png).
    /// The guest stays away until the host says (a file in the run folder: bot signals need the session).
    /// </summary>
    public static partial class Scenarios
    {
        static string RunDir => LaunchArgs.ResultPath == null ? null : Path.GetDirectoryName(Path.GetDirectoryName(LaunchArgs.ResultPath));
        static string DoneFile => RunDir == null ? null : Path.Combine(RunDir, "maprejoin-host-done");

        /// <summary>Everything on the tactical map that must match, one part per line kind.</summary>
        static Dictionary<string, string> MapParts()
        {
            var all = ImpactsLine().Split(" | ").ToList();
            string Pick(System.Func<string, bool> f) { var l = all.Where(f).ToList(); l.Sort(); return l.Count == 0 ? "none" : string.Join(" | ", l); }
            return new Dictionary<string, string>
            {
                ["impacts"] = Pick(p => !p.StartsWith("photo ") && !p.StartsWith("arm ") && !p.StartsWith("arrow ") && p != "none"),
                ["crater photos"] = Pick(p => p.StartsWith("photo ")),
                ["measuring arms"] = Pick(p => p.StartsWith("arm ")),
                ["correction arrows"] = Pick(p => p.StartsWith("arrow ")),
                ["recon strips"] = StripsLine(),
                ["shells in flight"] = Shells().Count == 0 ? "none" : string.Join(" | ", Shells()),
            };
        }

        static string MapLine() => string.Join("\n", MapParts().Select(kv => kv.Key + "=" + kv.Value));

        static Dictionary<string, string> ParseMap(string s)
        {
            var d = new Dictionary<string, string>();
            foreach (var l in (s ?? "").Split('\n')) { int i = l.IndexOf('='); if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1); }
            return d;
        }

        /// <summary>A picture straight down on the tactical map (a camera of our own over the table, then removed).</summary>
        static IEnumerable<object> MapShot(Bot bot, string name)
        {
            // the table's own collider (renderer bounds are no use here: the iris tool reports tens of metres)
            if (Avatars.Table() is not Bounds map) { bot.Note("no tactical map to photograph"); yield break; }
            var go = new GameObject("qa-map-camera");
            var cam = go.AddComponent<Camera>();
            cam.depth = 100; cam.nearClipPlane = 0.01f; cam.fieldOfView = 40;
            float size = Mathf.Max(map.extents.x, map.extents.z);
            float up = size / Mathf.Tan(20 * Mathf.Deg2Rad) * 1.05f;
            go.transform.position = new Vector3(map.center.x, map.max.y, map.center.z) + Vector3.up * up;
            cam.nearClipPlane = Mathf.Max(0.01f, up - 1.2f);   // the ceiling and the lamps over the table cut away
            go.transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
            foreach (var x in bot.Wait(0.3)) yield return x;
            foreach (var x in bot.Shot(name)) yield return x;
            Object.Destroy(go);
        }

        static IEnumerable<object> MapRejoinHost(Bot bot)
        {
            if (DoneFile != null && File.Exists(DoneFile)) File.Delete(DoneFile);
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in bot.Until(() => MissionFlow.GameReady, 120, "game finished booting")) yield return x;
            var defs = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PunchcardDefinitionV2>()))
            { var d = o.TryCast<PunchcardDefinitionV2>(); if (d != null && d.ID == "ScoutPlane" && !d.name.Contains("(Clone)") && defs.Count < 1) defs.Add(d); }
            if (defs.Count > 0) ProgressionManager.Instance.UnlockPunchcards(defs.Cast<Il2CppSystem.Collections.Generic.IEnumerable<PunchcardDefinitionV2>>());
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "FireOnCall")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Console != null && Turret != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Until(() => Punchcards().Count > 0, 60, "punchcards dealt")) yield return x;
            if (MissionStatsTracker.Instance != null && MissionStatsTracker.Instance.RequisitionPoints < 500) MissionStatsTracker.Instance.AddRequisitionPoints(500, "qa");
            if (PowderChargeInventory.Instance != null && PowderChargeInventory.Instance.CurrentCharges < 10) PowderChargeInventory.Instance.CurrentCharges = 10;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Signal("ready");
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 1, 60, "guest left")) yield return x;

            // --- while the guest is away
            // 1. a recon plane
            var slot = Console?.RequisitionSlot;
            var card = Punchcards().FirstOrDefault(c => c.gameObject.activeInHierarchy && c.CurrentDefinition != null && c.CurrentDefinition.ID == "ScoutPlane");
            bot.Check(slot != null && card != null, "a requisition slot and the scout plane card");
            if (slot != null && card != null)
            {
                Music.PlaceInto(card.DraggableItem ?? card.GetComponent<DraggableItem>(), slot.itemSlot);
                foreach (var x in bot.Wait(3)) yield return x;
                string before = StripsLine();
                for (int attempt = 0; attempt < 3 && StripsLine() == before; attempt++)
                {
                    foreach (var x in RealPress(bot, slot.lever)) yield return x;
                    foreach (var x in bot.Until(() => StripsLine() != before, 20, null, soft: true)) yield return x;
                }
                bot.Check(StripsLine() != before, "the recon plane laid photo strips on the map");
                foreach (var x in bot.Wait(3)) yield return x;
            }

            // 2. two shells at two different spots
            ShellDefinition he = MissionSaveManager.FindShell("HE");
            if (he == null) foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>())) { he = o.TryCast<ShellDefinition>(); if (he != null) break; }
            for (int shot = 0; shot < 2; shot++)
            {
                var gun = ControlledGun;
                var rc = gun.artilleryReloadController;
                var cyl = Cylinder(rc, "BreechOpen");
                if (cyl != null && he != null) cyl.TryInsertShellRuntime(he, ShellSlotPool.ShellSource.Mission, out _);
                foreach (var x in ReloadByHand(bot, askHost: false)) yield return x;
                bot.Check(rc.CurrentState != null && rc.CurrentState.isReloadCompleteState, $"shot {shot + 1}: the gun is reloaded");
                // aim somewhere else each time: turn the turret, then set a range
                var dial = Turret.rotationDial;
                foreach (var x in DragDial(bot, dial, dial.AccumulatedValue + (shot == 0 ? 25f : -40f) * Mathf.Max(0.01f, Turret.dialDegreesPerTurretDegree))) yield return x;
                // the first lands on the map (a crater photo), the second beyond its edge (no photo there)
                gun.SetDesiredRange(Mathf.Lerp(gun.minRange, gun.maxRange, shot == 0 ? 0.08f : 0.65f));
                float still = 0;
                foreach (var x in bot.Until(() => { still = Mathf.Abs(Turret.rotationVelocity) < 0.01f && Mathf.Abs(gun.elevationChangeVelocity) < 0.01f ? still + Time.unscaledDeltaTime : 0; return still > 1.5f; }, 60, $"shot {shot + 1}: aimed", soft: true)) yield return x;
                int sent = Gunnery.Instance.ShotsSent;
                bot.Check(gun.CanFire, $"shot {shot + 1}: the gun can fire");
                gun.RequestFire();
                foreach (var x in bot.Until(() => Gunnery.Instance.ShotsSent > sent, 15, $"shot {shot + 1}: fired")) yield return x;
                bot.Note($"shot {shot + 1}: {Gunnery.Instance.LastShot}");
                double lands = ShellsEndAt();
                foreach (var x in bot.Until(() => MissionClock.TimeAsDouble > lands + 1, 120, $"shot {shot + 1}: landed")) yield return x;
                foreach (var x in bot.Wait(3)) yield return x;
            }
            bot.Note("host map before the guest returns:\n" + MapLine());
            bot.Note($"host impacts: {MapParts()["impacts"]}; photos: {MapParts()["crater photos"]}");

            // --- the guest may come back now
            if (DoneFile != null) File.WriteAllText(DoneFile, "done");
            foreach (var x in bot.Until(() => Coop.Session.PlayerCount == 2 && WorldReady.Slots.Contains((byte)1), 180, "guest back in my mission")) yield return x;
            foreach (var x in bot.Wait(5)) yield return x;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ImpactLocation>(), false)) { var il = o.TryCast<ImpactLocation>(); if (il != null) bot.Note(WorldEvents.DescribeImpact(il)); }
            foreach (var x in MapShot(bot, "map-host")) yield return x;
            bot.Signal("map", MapLine());
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 90, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> MapRejoinGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && bot.HasSignal("ready"), 200, "in mission")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(DoneFile != null, "a run folder to meet the host in");
            CoopRunner.HoldJoinUntil = float.MaxValue;
            Coop.Leave();
            bot.Note("left; staying away while the host fires");
            foreach (var x in bot.Until(() => DoneFile != null && File.Exists(DoneFile), 600, "the host is done firing")) yield return x;
            CoopRunner.HoldJoinUntil = 0;
            foreach (var x in bot.Until(() => Coop.IsGuest && WorldReady.LocalReady && MissionFlow.Phase == MissionManager.GamePhase.MissionActive, 180, "back in the host's mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("map"), 90, "host's map")) yield return x;
            var host = ParseMap(bot.SignalValue("map"));
            foreach (var x in bot.Until(() => MapLine() == bot.SignalValue("map"), 10, null, soft: true)) yield return x;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ImpactLocation>(), false)) { var il = o.TryCast<ImpactLocation>(); if (il != null) bot.Note(WorldEvents.DescribeImpact(il)); }
            foreach (var x in MapShot(bot, "map-guest")) yield return x;
            bot.Note($"host map objects restored here: {WorldEvents.MapObjectsRestored} ({WorldEvents.MapObjectKinds})");
            var mine = MapParts();
            foreach (var kv in mine)
            {
                host.TryGetValue(kv.Key, out var h);
                bot.Check(kv.Value == h, $"after joining again the {kv.Key} match the host's (here {kv.Value} / host {h})");
            }
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
