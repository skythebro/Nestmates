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
        static TurretController Turret => TurretController.Instance;
        static GunController ControlledGun => Turret?.guns?[Mathf.Clamp(Turret.controlledGunIndex, 0, Turret.guns.Count - 1)];

        /// <summary>Every shell in flight: "start>target@endsAt", sorted.</summary>
        static List<string> Shells()
        {
            var list = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ShellVisual>(), true))
            {
                var s = o.TryCast<ShellVisual>();
                if (s == null || s.endsAt <= 0) continue;   // pooled template, never launched
                list.Add($"{s.startLocalPos.x:F3},{s.startLocalPos.y:F3}>{s.targetLocalPos.x:F3},{s.targetLocalPos.y:F3}@{s.endsAt:F2}");
            }
            list.Sort();
            return list;
        }

        /// <summary>The map's arms: where they are, whether one is held by a shell or springing back.</summary>
        static string ArmsLine()
        {
            var parts = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TrajectorySystem.TrajectoryTarget>(), false))
            {
                var tt = o.TryCast<TrajectorySystem.TrajectoryTarget>();
                if (tt == null) continue;
                var lp = tt.transform.localPosition;
                parts.Add($"{tt.transform.parent?.name} at {lp.x:F2};{lp.y:F2};{lp.z:F2}{(tt.IsClaimed ? " claimed" : "")}");
            }
            parts.Sort();
            return parts.Count == 0 ? "none" : string.Join(" | ", parts);
        }

        /// <summary>Every impact on the map: where, which way round, whether its scouting photo strips show.</summary>
        static string ImpactsLine()
        {
            var parts = new List<string>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ImpactLocation>(), false))
            {
                var i = o.TryCast<ImpactLocation>();
                if (i == null) continue;
                var t = i.transform;
                int strips = 0;
                if (i.ScoutingStrips != null) foreach (var g in i.ScoutingStrips) if (g != null && g.activeInHierarchy && !g.name.StartsWith("FMOD")) strips++;   // not the reveal sounds (invisible; a restore silences them)
                int seen = 0;   // what shows of it (crater photo, range text, arrow ...)
                foreach (var rr in i.GetComponentsInChildren<Renderer>(false)) if (rr != null && rr.enabled) seen++;
                foreach (var g in i.GetComponentsInChildren<UnityEngine.UI.Graphic>(false)) if (g != null && g.enabled) seen++;
                parts.Add($"{t.position.x:F2};{t.position.z:F2} rot {t.eulerAngles.y:F0}/{t.eulerAngles.z:F0} strips {strips} blocked {i.ScoutingStripsBlocked} shows {seen}");
            }
            // the crater photos: each turned by its own dice when it appears (may be moved onto the map by then)
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<RandomUIRotation>(), false))
            {
                var r = o.TryCast<RandomUIRotation>();
                if (r == null || string.IsNullOrEmpty(r.gameObject.scene.name)) continue;
                var t = r.transform; var e = t.localEulerAngles;
                parts.Add($"photo {r.name} at {t.position.x:F2};{t.position.z:F2} turned {e.x:F0}/{e.y:F0}/{e.z:F0}");
            }
            // the map's drafting arms: they follow a shell in flight and spring back when it lands (the arm
            // used to stay out on the client and could not be used again)
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TrajectorySystem.TrajectoryTarget>(), false))
            {
                var tt = o.TryCast<TrajectorySystem.TrajectoryTarget>();
                if (tt == null) continue;
                var lp = tt.transform.localPosition;
                parts.Add($"arm {tt.transform.parent?.name} at {lp.x:F2};{lp.y:F2};{lp.z:F2}");
            }
            // the correction arrow of a miss (its random error is rolled per impact)
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ImpactVisualCorrections>(), false))
            {
                var c = o.TryCast<ImpactVisualCorrections>();
                if (c == null || c.arrowRoot == null || !c.arrowRoot.activeInHierarchy) continue;
                parts.Add($"arrow error {c._directionErrorOffsetDeg:F1}");
            }
            parts.Sort();
            return parts.Count == 0 ? "none" : string.Join(" | ", parts);
        }

        static double ShellsEndAt()
        {
            double end = 0;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<ShellVisual>(), true)) { var s = o.TryCast<ShellVisual>(); if (s != null && s.endsAt > end) end = s.endsAt; }
            return end;
        }

        static string GunLine()
        {
            var t = Turret;
            if (t == null) return "no turret";
            return $"angle {t._CurrentAngle_k__BackingField:F2}, elevations " + string.Join("/", Enumerable.Range(0, t.guns.Count).Select(i => t.guns[i]._CurrentElevation_k__BackingField.ToString("F2")));
        }

        static string TurretDiag()
        {
            var t = Turret; var d = t.rotationDial;
            return $"angle {t._CurrentAngle_k__BackingField:F2} desired {t._DesiredRotation_k__BackingField:F2} vel {t.rotationVelocity:F3} dial {(d == null ? "none" : d.AccumulatedValue.ToString("F2") + (d.IsDragging ? " dragging" : ""))} " +
                   $"speedDial {t.isUsingSpeedDial} dragActive {t.rotationDialDragActive} backdrive {t.backdriveRotationDial} ratio {t.dialDegreesPerTurretDegree:F2} enabled {t.enabled} dialActive {(d != null && d.isActiveAndEnabled)}";
        }

        static string FireDiag()
        {
            var g = ControlledGun; var b = g.fireButton;
            return $"gun {g.gunName} canFire {g.CanFire} stage {g.fireSequenceStage} hasFired {g.hasFired} reloading {g.isReloading} button {(b == null ? "none" : $"{b.name} active {b.isActive} go {b.gameObject.activeInHierarchy} path {Util.ScenePath.Of(b.transform)}")}; " +
                   $"clicks executed {Controls.Instance.ClicksExecuted}, forwarded {Controls.Instance.ClicksForwarded}, shells {ShellVisual.ActiveInstances?.Count}";
        }

        static float[] GunNumbers()
        {
            var t = Turret;
            return new[] { t._CurrentAngle_k__BackingField }.Concat(Enumerable.Range(0, t.guns.Count).Select(i => t.guns[i]._CurrentElevation_k__BackingField)).ToArray();
        }

        /// <summary>Drag a slider like a hand would. The bot has no cursor, so it holds the slider's cursor-driven
        /// Update off (the same switch the mod uses for remote hands) and sets values directly.</summary>
        static IEnumerable<object> DragSlider(Bot bot, LinearSliderInteractable sl, float value, double hold = 0)
        {
            Controls.HeldSliders.Add(sl.Pointer);
            try { foreach (var x in DragSliderInner(bot, sl, value, hold)) yield return x; }
            finally { Controls.HeldSliders.Remove(sl.Pointer); }
        }

        static IEnumerable<object> DragSliderInner(Bot bot, LinearSliderInteractable sl, float value, double hold)
        {
            sl.BeginSliderDrag();
            float from = sl.Value;
            for (int i = 1; i <= 5; i++)
            {
                sl.SetSliderValue(Mathf.Lerp(from, value, i / 5f));
                foreach (var x in bot.Wait(0.06)) yield return x;
            }
            sl.SetSliderValue(value);
            // a spring-return lever is a rate control: it only acts while held (and springs back on release)
            if (hold > 0) bot.Signal("holding-" + sl.name, value.ToString("R"));
            for (double t = 0; t < hold; t += 0.1) { sl.SetSliderValue(value); foreach (var x in bot.Wait(0.1)) yield return x; }
            foreach (var x in bot.Wait(0.1)) yield return x;
            sl.EndSliderDrag();
        }

        /// <summary>What the powder station looks like: each charge button (lit/usable + its animator bools) and each
        /// dispenser lever (animator state + bools). Players saw the levers not stay out on the client.</summary>
        static string PowderLook(PowderChargeController p)
        {
            static string Anim(GameObject go)
            {
                var a = go == null ? null : go.GetComponentInChildren<Animator>();
                if (a == null || !a.isActiveAndEnabled || a.runtimeAnimatorController == null) return "-";
                var sb = new System.Text.StringBuilder();
                sb.Append(a.GetCurrentAnimatorStateInfo(0).shortNameHash).Append('/');
                for (int i = 0; i < a.parameterCount; i++)
                {
                    var prm = a.GetParameter(i);
                    if (prm.type == AnimatorControllerParameterType.Bool && prm.name != "IsHoverdOver") sb.Append(prm.name).Append(a.GetBool(prm.nameHash) ? "+" : "-");
                }
                return sb.ToString();
            }
            var parts = new List<string>();
            for (int i = 0; i < p.chargeButtons.Count; i++) { var b = p.chargeButtons[i]; parts.Add($"button{i} {(b == null ? "_" : (b.isActive ? "lit " : "off ") + Anim(b.gameObject))}"); }
            for (int i = 0; i < p.chargeDispensers.Count; i++) parts.Add($"lever{i} {Anim(p.chargeDispensers[i])}");
            parts.Add($"load {(p.loadChargesButton != null && p.loadChargesButton.isActive ? "lit" : "off")}");
            return string.Join(" | ", parts);
        }

        static int _powderAsks;

        static PowderChargeController PowderOf(ArtilleryReloadController rc)
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PowderChargeController>()))
            { var p = o.TryCast<PowderChargeController>(); if (p != null && p.reloadController != null && p.reloadController.Pointer == rc.Pointer) return p; }
            return null;
        }

        static IEnumerable<object> Press(Bot bot, LookAtTarget b)
        {
            b.OnClickDown();
            foreach (var x in bot.Wait(0.15)) yield return x;
            b.OnClickUp();
        }

        static CylinderShellSelector Cylinder(ArtilleryReloadController rc, string key)
        {
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<CylinderShellSelector>()))
            {
                var c = o.TryCast<CylinderShellSelector>();
                if (c != null && c.loadStateKey == key && c.artilleryReloadController != null && c.artilleryReloadController.Pointer == rc.Pointer) return c;
            }
            return null;
        }

        /// <summary>Guest: walk the reload sequence like a player, pressing each state's button.</summary>
        static IEnumerable<object> ReloadByHand(Bot bot, bool askHost = true)
        {
            var rc = ControlledGun.artilleryReloadController;
            double deadline = Time.unscaledTimeAsDouble + 150;
            int stuck = 0;
            while (Time.unscaledTimeAsDouble < deadline)
            {
                var st = rc.CurrentState;
                if (st == null || st.isReloadCompleteState) yield break;
                int before = rc.currentStateIndex;
                string did;
                if (st.stateKey == "SelectPowderCharge")
                {
                    PowderChargeController pc = null;
                    foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PowderChargeController>()))
                    { var p = o.TryCast<PowderChargeController>(); if (p != null && p.reloadController != null && p.reloadController.Pointer == rc.Pointer) pc = p; }
                    if (pc == null) { did = "no powder controller"; }
                    else
                    {
                        // charges are taken one at a time: the next button only activates after the previous one
                        for (int n = 0; n < 2; n++)
                        {
                            LookAtTarget next = null;
                            for (int i = 0; i < pc.chargeButtons.Count && next == null; i++) if (pc.chargeButtons[i] != null && pc.chargeButtons[i].isActive) next = pc.chargeButtons[i];
                            if (next == null) break;
                            int had = pc.currentSelectedCharges;
                            foreach (var x in Press(bot, next)) yield return x;
                            foreach (var x in bot.Until(() => pc.currentSelectedCharges > had, 5, null, soft: true)) yield return x;
                            foreach (var x in bot.Wait(0.8)) yield return x;
                        }
                        foreach (var x in bot.Until(() => pc.loadChargesButton.isActive, 8, null, soft: true)) yield return x;
                        foreach (var x in bot.Wait(1.5)) yield return x;
                        if (askHost)   // (not when the host reloads alone)
                        {
                            // the levers pulled so far must stay out, and the next charge light on, for both players
                            string ask = (++_powderAsks) + "#";
                            bot.Signal("powderlook", ask + PowderLook(pc));
                            foreach (var x in bot.Until(() => (bot.SignalValue("powderlook-host") ?? "").StartsWith(ask), 10, null, soft: true)) yield return x;
                            string hostLook = (bot.SignalValue("powderlook-host") ?? "").StartsWith(ask) ? bot.SignalValue("powderlook-host").Substring(ask.Length) : "(no answer)";
                            bot.Check(PowderLook(pc) == hostLook, $"the powder levers and lights look the same (here {PowderLook(pc)} / host {hostLook})");
                            if (PowderLook(pc) != hostLook)
                                for (int i = 0; i < pc.chargeButtons.Count; i++)
                                    if (pc.chargeButtons[i] != null)
                                        foreach (var an in pc.chargeButtons[i].GetComponentsInChildren<Animator>(true))
                                            bot.Note($"button{i} animator {Util.ScenePath.Of(an.transform)}: {Animators.Instance.Describe(an)}");
                        }
                        foreach (var x in Press(bot, pc.loadChargesButton)) yield return x;
                        did = $"charges {pc.currentSelectedCharges} + load (load button was {(pc.loadChargesButton.isActive ? "active" : "inactive")})";
                    }
                }
                else if (st.advanceButton != null) { foreach (var x in Press(bot, st.advanceButton)) yield return x; did = "pressed " + st.advanceButton.name; }
                else if (Cylinder(rc, st.stateKey) is CylinderShellSelector cyl)
                {
                    string contents = string.Join(",", Enumerable.Range(0, cyl.bullets.Count).Select(i => cyl.bullets[i] == null ? "-" : "S"));
                    var btn = cyl.lastSlotAHasShell || stuck > 0 ? cyl.loadButton : cyl.moveButton;
                    if (!cyl.lastSlotAHasShell && stuck == 0 && cyl.moveButton != null) { foreach (var x in Press(bot, cyl.moveButton)) yield return x; foreach (var x in bot.Wait(2.5)) yield return x; }
                    foreach (var x in Press(bot, cyl.loadButton)) yield return x;
                    did = $"cylinder {cyl.name} [{contents}] slotA {cyl.lastSlotAHasShell}: load";
                }
                else did = "waiting (auto-advance)";
                bot.Note($"guest reload: {before} {st.stateKey}: {did}");
                foreach (var x in bot.Until(() => rc.currentStateIndex != before, 10, null, soft: true)) yield return x;
                if (rc.currentStateIndex == before && ++stuck >= 3) { bot.Note($"guest reload stuck at {before} {st.stateKey}"); yield break; }
            }
        }

        static IEnumerable<object> GunneryHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Turret != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            // control: does the turret turn at all here when the host itself drags the dial?
            bot.Note("host before: " + TurretDiag());
            var hd = Turret.rotationDial;
            float a0 = Turret._CurrentAngle_k__BackingField;
            foreach (var x in DragDial(bot, hd, hd.AccumulatedValue + 2f * Mathf.Max(0.01f, Turret.dialDegreesPerTurretDegree))) yield return x;
            bot.Note("host after own drag: " + TurretDiag());
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Note($"host 3 s after own drag: {TurretDiag()} (moved {Mathf.DeltaAngle(a0, Turret._CurrentAngle_k__BackingField):F2})");
            float start = Turret._CurrentAngle_k__BackingField;
            bot.Signal("ready");

            // 1. guest aims (rotation dial); the host's turret must follow and both must agree once it settles
            foreach (var x in bot.Until(() => bot.HasSignal("aimed"), 60, "guest turned the rotation dial")) yield return x;
            bot.Note("host when guest done: " + TurretDiag());
            foreach (var x in bot.Until(() => Mathf.Abs(Mathf.DeltaAngle(Turret._CurrentAngle_k__BackingField, start)) > 1f, 30, "host turret starts turning")) yield return x;
            float still = 0;
            foreach (var x in bot.Until(() => { still = Mathf.Abs(Turret.rotationVelocity) < 0.01f && Enumerable.Range(0, Turret.guns.Count).All(i => Mathf.Abs(Turret.guns[i].elevationChangeVelocity) < 0.01f) ? still + Time.unscaledDeltaTime : 0; return still > 1.5f; }, 40, "turret settled")) yield return x;
            float angle = Turret._CurrentAngle_k__BackingField;
            bot.Check(Mathf.Abs(Mathf.DeltaAngle(angle, start)) > 1f, $"guest's dial turned the host's turret ({start:F2} -> {angle:F2})");
            bot.Signal("settled-n", string.Join(";", GunNumbers().Select(f => f.ToString("R"))));
            bot.Signal("settled", GunLine());

            // 2. the guest reloads by hand (every press forwarded here and executed on the real gun)
            var rc = ControlledGun.artilleryReloadController;
            bot.Note($"reload states: {string.Join(",", Enumerable.Range(0, rc.reloadStates.Count).Select(i => rc.reloadStates[i].stateKey))}; current {rc.currentStateIndex}");
            // the tutorial cylinder starts empty: put one round in (as resupply would); guests must get it too
            var hcyl = Cylinder(rc, "BreechOpen");
            ShellDefinition he = MissionSaveManager.FindShell("HE");
            if (he == null) foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<ShellDefinition>())) { he = o.TryCast<ShellDefinition>(); if (he != null) break; }
            bool empty = hcyl != null && Enumerable.Range(0, hcyl.bullets.Count).All(i => hcyl.bullets[i] == null);
            bool inserted = hcyl != null && he != null && hcyl.TryInsertShellRuntime(he, ShellSlotPool.ShellSource.Mission, out _);
            if (empty) bot.Check(inserted, $"host inserted a {he?.ShellId} round into {hcyl?.name}");
            else bot.Note($"cylinder already holds rounds; extra insert {(inserted ? "done" : "refused (full)")}");
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Signal("inserted", Gunnery.CurrentDigest);
            foreach (var x in bot.Until(() => bot.HasSignal("reloading"), 60, "guest starts reloading")) yield return x;
            int lastState = -1; float nextDiag = 0; string lookAnswered = null;
            foreach (var x in bot.Until(() =>
            {
                string asked = bot.SignalValue("powderlook");
                if (asked != null && asked.Contains("#") && asked.Substring(0, asked.IndexOf('#') + 1) != lookAnswered && PowderOf(rc) is PowderChargeController hostPowder)
                {
                    lookAnswered = asked.Substring(0, asked.IndexOf('#') + 1);
                    bot.Signal("powderlook-host", lookAnswered + PowderLook(hostPowder));
                }
                if (rc.currentStateIndex != lastState) { lastState = rc.currentStateIndex; bot.Note($"host reload state {lastState} {rc.reloadStates[lastState].stateKey}"); }
                if (Time.unscaledTime >= nextDiag)
                {
                    nextDiag = Time.unscaledTime + 4;
                    var pcs = string.Join(" ", Object.FindObjectsOfType(Il2CppType.Of<PowderChargeController>()).Select(o => o.TryCast<PowderChargeController>()).Where(p => p != null).Select(p => $"{p.name}(rc {(p.reloadController != null && p.reloadController.Pointer == rc.Pointer ? "mine" : "other")}):{p.currentSelectedCharges} btns[{string.Join("", Enumerable.Range(0, p.chargeButtons.Count).Select(i => p.chargeButtons[i] == null ? "_" : p.chargeButtons[i].isActive ? (p.chargeButtons[i].gameObject.activeInHierarchy ? "A" : "a") : "i"))}] load {(p.loadChargesButton == null ? "_" : p.loadChargesButton.isActive ? "A" : "i")}"));
                    bot.Note($"host powder: {pcs}; inventory {PowderChargeInventory.Instance?.CurrentCharges}; clicks executed {Controls.Instance.ClicksExecuted}");
                }
                return rc.CurrentState != null && rc.CurrentState.isReloadCompleteState;
            }, 180, "host gun reloaded by the guest")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            var g0 = ControlledGun;
            bot.Note($"host after reload: elevation {g0._CurrentElevation_k__BackingField:F2} desired {g0.internalDesiredElevation:F2} reloadElev {g0.reloadElevation:F2} targetingReload {g0.isTargetingReloadElevation} hold {g0.externalHoldAtReloadAfterComplete} lock {g0.externalReloadLoweringLocked} range {g0._CurrentRange_k__BackingField:F1} [{g0.minRange:F0}..{g0.maxRange:F0}]");
            var hp = g0.highPressureSystemManager;
            var valves = string.Join(" ", Object.FindObjectsOfType(Il2CppType.Of<ValveController>()).Select(o => o.TryCast<ValveController>()).Where(v => v != null).Select(v => $"{v.systemId}:{v.currentDamage01:F2}"));
            bot.Note($"host pressure: gun system '{g0.systemIdForAutoFind}' manager {(hp == null ? "none" : hp.name + " value " + hp.GetFloatValue().ToString("F2"))}; valves {valves}; engine {Machinery.Instance.EngineLine}");
            bot.Signal("elevate");
            LinearSliderInteractable hes = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<GunElevationSliderBinding>()))
            { var b = o.TryCast<GunElevationSliderBinding>(); if (b != null && b.gun != null && b.gun.Pointer == g0.Pointer) hes = b.desiredSlider; }
            if (hes != null)
            {
                string key = "holding-" + hes.name;
                foreach (var x in bot.Until(() => bot.HasSignal(key), 30, "guest holding the elevation lever")) yield return x;
                foreach (var x in bot.Wait(0.4)) yield return x;   // first streamed values in
                float want = float.Parse(bot.SignalValue(key)), lo = float.MaxValue, hi = float.MinValue;
                for (int f = 0; f < 30; f++) { lo = Mathf.Min(lo, hes.Value); hi = Mathf.Max(hi, hes.Value); foreach (var x in bot.Wait(0.033)) yield return x; }
                bot.Check(lo > want * 0.9f && hi < want * 1.1f, $"host lever follows the guest's hand during the drag, every frame ({lo:F2}..{hi:F2}, guest holds {want:F2})");
            }
            foreach (var x in bot.Until(() => bot.HasSignal("elevated"), 30, "guest raised the gun")) yield return x;
            bot.Note($"host elevation speed {g0._CurrentElevationSpeed_k__BackingField:F3} (base {g0.elevationChangeSpeed:F2}), pressure-attenuated {g0.attenuateSliderBySystemPressure}");
            float est = 0;
            foreach (var x in bot.Until(() => { est = Mathf.Abs(g0.elevationChangeVelocity) < 0.01f && g0._CurrentElevation_k__BackingField > 0.5f ? est + Time.unscaledDeltaTime : 0; return est > 1f; }, 30, "host gun elevated and settled", soft: true)) yield return x;
            bot.Note($"host after guest lever: elevation {g0._CurrentElevation_k__BackingField:F2} desired {g0.internalDesiredElevation:F2} range {g0._CurrentRange_k__BackingField:F1}");
            bot.Check(g0._CurrentElevation_k__BackingField > 0.5f, $"guest's elevation lever raised the host's gun ({g0._CurrentElevation_k__BackingField:F2}, range {g0._CurrentRange_k__BackingField:F1})");
            bot.Signal("elev-n", g0._CurrentElevation_k__BackingField.ToString("R"));
            bot.Check(ControlledGun.CanFire, $"host gun can fire (state {rc.currentStateIndex}, chambered {ControlledGun.ChamberedShellBlueprint?.shellDefinition?.ShellId})");
            bot.Signal("chambered", Gunnery.CurrentDigest);

            // 3. guest presses fire; the host fires once; the guest shows the very same shell
            int sentBefore = Gunnery.Instance.ShotsSent;
            foreach (var x in bot.Until(() => bot.HasSignal("fired"), 60, "guest pressed fire")) yield return x;
            foreach (var x in bot.Until(() => Gunnery.Instance.ShotsSent > sentBefore, 10, "host fired and sent the shot")) yield return x;
            bot.Note("host shot: " + Gunnery.Instance.LastShot);
            bot.Signal("shot", Gunnery.Instance.LastShot);
            var mine = Shells();
            bot.Note($"host shells in flight: {string.Join(" | ", mine)}");
            bot.Signal("shells", string.Join("|", mine));
            foreach (var x in bot.Until(() => bot.HasSignal("compared"), 60, "guest compared shells")) yield return x;

            // 4. after the round lands, reload state and gun state still agree
            double lands = ShellsEndAt();
            foreach (var x in bot.Until(() => MissionClock.TimeAsDouble > lands + 1, 90, $"shell landed (mission time {lands:F1})")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Signal("after", Gunnery.CurrentDigest + "#" + ControlledGun.artilleryReloadController.currentStateIndex);
            bot.Signal("impacts", ImpactsLine());
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            // 5. the map's arms stay where the shell left them until someone clicks them (the host clicked
            // them, and on the guest they stayed out)
            bot.Note("arms before the click: " + ArmsLine());
            var armButtons = new List<LookAtTarget>();
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<LookAtTarget>(), false))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b != null && b.GetComponentInChildren<TrajectorySystem.TrajectoryTarget>(true) != null) armButtons.Add(b);
            }
            bot.Check(armButtons.Count > 0, $"the map's arms can be clicked ({armButtons.Count})");
            foreach (var b in armButtons) foreach (var x in RealPress(bot, b)) yield return x;
            foreach (var x in bot.Wait(4)) yield return x;
            bot.Note("arms after the click: " + ArmsLine());
            bot.Signal("arms", ArmsLine());
            foreach (var x in bot.Until(() => bot.HasSignal("armsverdict"), 30, "guest compared the arms")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> GunneryGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Turret != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;

            var dial = Turret.rotationDial;
            bot.Check(dial != null, "turret has a rotation dial");
            if (dial != null) foreach (var x in DragDial(bot, dial, dial.AccumulatedValue + 3f * Mathf.Max(0.01f, Turret.dialDegreesPerTurretDegree))) yield return x;
            // raise the controlled gun too (its elevation slider), so a shot has a real flight
            GunElevationSliderBinding eb = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<GunElevationSliderBinding>()))
            { var b = o.TryCast<GunElevationSliderBinding>(); if (b != null && b.gun != null && b.gun.Pointer == ControlledGun.Pointer) eb = b; }
            var es = eb?.desiredSlider;
            bot.Note($"elevation slider: {(es == null ? "none" : $"{es.name} value {es.Value:F3}")}");

            bot.Note("guest after drag: " + TurretDiag());
            bot.Signal("aimed");
            foreach (var x in bot.Until(() => bot.HasSignal("settled"), 60, "host turret settled")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            var t = Turret;
            var host = bot.SignalValue("settled");
            bot.Note($"aim: host {host} | guest {GunLine()}");
            var hn = bot.SignalValue("settled-n").Split(';').Select(float.Parse).ToArray();
            var gn = GunNumbers();
            bot.Note($"elevation during reload (gun holds reload elevation?): {host}");
            bool same = hn.Length == gn.Length && Mathf.Abs(Mathf.DeltaAngle(hn[0], gn[0])) < 0.05f;
            for (int i = 1; same && i < gn.Length; i++) same = Mathf.Abs(hn[i] - gn[i]) < 0.03f;
            bot.Check(same, $"guest turret angle and gun elevations equal the host's ({GunLine()} vs {host})");

            bot.Note($"auto-reload manager in this mission: {Object.FindObjectOfType<AutoReloadManager>() != null}");
            foreach (var x in bot.Until(() => bot.HasSignal("inserted"), 60, "host inserted a round")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            bot.Check(Gunnery.CurrentDigest == bot.SignalValue("inserted"), $"guest cylinder has the host's round ({Gunnery.CurrentDigest} vs {bot.SignalValue("inserted")})");
            bot.Signal("reloading");
            foreach (var x in ReloadByHand(bot)) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("elevate"), 200, "host reloaded")) yield return x;
            for (int i = 0; i < Turret.guns.Count; i++) Turret.guns[i].attenuateSliderBySystemPressure = false;   // same fixture as the host
            bool springs = es != null && es.GetComponent<LinearSliderAutoRetractor>() != null;
            bot.Note($"elevation lever {es?.name}: spring-return {springs}");
            bot.Note($"lever output range {es?.minOutputValue:F2}..{es?.maxOutputValue:F2}, value {es?.Value:F3}");
            if (es != null) foreach (var x in DragSlider(bot, es, Mathf.Lerp(es.minOutputValue, es.maxOutputValue, 0.5f), hold: 2.5)) yield return x;
            bot.Note($"guest lever after release {es?.Value:F3}, gun {ControlledGun._CurrentElevation_k__BackingField:F2}");
            bot.Signal("elevated");
            foreach (var x in bot.Until(() => bot.HasSignal("elev-n"), 60, "host elevation")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            float he2 = float.Parse(bot.SignalValue("elev-n")), ge2 = ControlledGun._CurrentElevation_k__BackingField;
            bot.Check(Mathf.Abs(he2 - ge2) < 0.03f, $"guest gun elevation equals the host's ({ge2:F3} vs {he2:F3})");
            foreach (var x in bot.Until(() => bot.HasSignal("chambered"), 60, "host ready to fire")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            bot.Check(Gunnery.CurrentDigest == bot.SignalValue("chambered"), $"guest gun state equals the host's after the reload ({Gunnery.CurrentDigest} vs {bot.SignalValue("chambered")})");
            bot.Note($"guest: reload states applied {Gunnery.Instance.ReloadsApplied}, full resyncs {Gunnery.Instance.Resyncs}, digest mismatches {Gunnery.Instance.DigestMismatches}");

            int shellsBefore = Shells().Count;
            bot.Note("guest fire: " + FireDiag());
            var btn = ControlledGun.fireButton;
            bot.Check(btn != null, "gun has a fire button");
            if (btn != null)
            {
                btn.OnClickDown();
                foreach (var x in bot.Wait(0.15)) yield return x;
                btn.OnClickUp();
            }
            bot.Signal("fired");
            foreach (var x in bot.Until(() => bot.HasSignal("shells"), 30, "host shells")) yield return x;
            foreach (var x in bot.Until(() => Gunnery.Instance.ShotsReplayed >= 1, 10, "guest replayed the host's shot")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("shot"), 10, "host shot data")) yield return x;
            bot.Check(Gunnery.Instance.LastShot == bot.SignalValue("shot"), $"guest launched exactly the host's shot ({Gunnery.Instance.LastShot} vs {bot.SignalValue("shot")})");
            foreach (var x in bot.Wait(0.3)) yield return x;
            var mine = Shells();
            var theirs = bot.SignalValue("shells").Split('|').Where(s => s.Length > 0).ToList();
            bot.Note($"guest shells: {string.Join(" | ", mine)}");
            bot.Check(shellsBefore == 0, $"no shell in flight before firing ({shellsBefore})");
            if (theirs.Count > 0 || mine.Count > 0)
                bot.Check(mine.SequenceEqual(theirs), $"guest shows exactly the host's shells in flight ({string.Join(" | ", mine)} vs {string.Join(" | ", theirs)})");
            foreach (var x in bot.Shot("guest-shell")) yield return x;
            bot.Signal("compared");

            foreach (var x in bot.Until(() => bot.HasSignal("after"), 90, "host after landing")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            var after = Gunnery.CurrentDigest + "#" + ControlledGun.artilleryReloadController.currentStateIndex;
            bot.Check(after == bot.SignalValue("after"), $"gun state still equal after the shot ({after} vs {bot.SignalValue("after")})");
            bot.Check(Gunnery.Instance.DigestMismatches == 0, $"no gun digest mismatches ({Gunnery.Instance.DigestMismatches})");
            // the impact on the map: same place, same photo strips showing, same way round
            foreach (var x in bot.Until(() => bot.HasSignal("impacts"), 20, null, soft: true)) yield return x;
            bot.Check(ImpactsLine() == bot.SignalValue("impacts"), $"the impact and its photo strip look the same (here {ImpactsLine()} / host {bot.SignalValue("impacts")})");
            bot.Note($"entity digest mismatches {WorldEntities.Instance.DigestMismatches}, corrections {WorldEntities.Instance.Corrected}");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("arms"), 60, "host clicked the arms")) yield return x;
            foreach (var x in bot.Until(() => ArmsLine() == bot.SignalValue("arms"), 4, null, soft: true)) yield return x;
            bot.Check(ArmsLine() == bot.SignalValue("arms"), $"the host clicked the map's arms back: here too (here {ArmsLine()} / host {bot.SignalValue("arms")})");
            bot.Signal("armsverdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
