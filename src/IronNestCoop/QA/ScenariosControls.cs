using System.Collections.Generic;
using System.Linq;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static ArtilleryComputer Computer => Object.FindObjectOfType<ArtilleryComputer>();
        static FireMissionCardPrinter Printer => Object.FindObjectOfType<FireMissionCardPrinter>();

        /// <summary>Drag like a hand would: begin, move, release (the module only streams while dragging).</summary>
        static IEnumerable<object> DragDial(Bot bot, DialInteractable d, float value)
        {
            bool unlimited = d.dialMode == DialInteractable.DialMode.Unlimited;
            void Set(float v) { if (unlimited) d.SetAccumulatedValueUnlimited(v, true, true); else d.SetDialValue(v); }
            d.BeginDialDrag();
            float from = d.AccumulatedValue;
            for (int i = 1; i <= 5; i++)
            {
                Set(Mathf.Lerp(from, value, i / 5f));
                foreach (var x in bot.Wait(0.06)) yield return x;
            }
            Set(value);
            foreach (var x in bot.Wait(0.1)) yield return x;
            d.EndDialDrag();
        }

        static int CardCount() => Printer?.spawnedCardInstances?.Count ?? -1;

        /// <summary>The Steam status line in a mission: its name and time, the co-op part, the station only on request.</summary>
        static void PresenceChecks(Bot bot, string coopPart)
        {
            // the Discord Join button: its secret names a lobby, anything else is turned away; a Join that arrives
            // while already playing together asks to leave first instead of switching games
            bot.Check(Steam.DiscordPresence.LobbyOf("nm1:109775241234567890") == 109775241234567890UL && Steam.DiscordPresence.LobbyOf("nm1:-5") == 0
                && Steam.DiscordPresence.LobbyOf("nm1:12x") == 0 && Steam.DiscordPresence.LobbyOf("other:5") == 0 && Steam.DiscordPresence.LobbyOf(null) == 0,
                "a Discord join secret is read only when it is one of ours");
            int joins = Steam.DiscordPresence.JoinsReceived;
            Steam.DiscordPresence.OnEvent("{\"cmd\":\"DISPATCH\",\"data\":{\"secret\":\"nm1:109775241234567890\"},\"evt\":\"ACTIVITY_JOIN\"}");
            Steam.DiscordPresence.OnEvent("{\"cmd\":\"DISPATCH\",\"data\":{\"secret\":\"someone else's\"},\"evt\":\"ACTIVITY_JOIN\"}");
            Steam.DiscordPresence.OnEvent("{\"cmd\":\"SUBSCRIBE\",\"data\":{\"evt\":\"ACTIVITY_JOIN\"},\"evt\":null,\"nonce\":\"1\"}");   // the answer to our subscribe: no Join
            Steam.DiscordPresence.TakeJoin();
            bot.Check(Steam.DiscordPresence.JoinsReceived == joins + 1 && UI.Toasts.Recent.Count > 0 && UI.Toasts.Recent[UI.Toasts.Recent.Count - 1].StartsWith("Leave this game first"),
                "a Join from Discord while in a game asks to leave first (and a foreign secret is ignored)");
            bool keep = Settings.StatusStation;
            Settings.StatusStation = false;
            var infoPlain = Steam.PresenceInfo.Now();
            string plain = Steam.SteamPresence.Describe(infoPlain);
            Settings.StatusStation = true;
            var infoStation = Steam.PresenceInfo.Now();
            string withStation = Steam.SteamPresence.Describe(infoStation);
            Settings.StatusStation = keep;
            // Discord: the same information, as its two lines and a live clock from the mission start
            var (details, state) = Steam.DiscordPresence.Lines(infoStation);
            string act = Steam.DiscordPresence.Activity(infoStation, details, state);
            bot.Note($"Discord: details '{details}', state '{state}', activity {act}");
            bot.Check(details == infoPlain.Headline && act.Contains("\"timestamps\":{\"start\":") && act.Contains("\"party\":{") && act.Contains(",5]"),
                $"the Discord status: the mission, a clock from its start, the party ({act})");
            bot.Check(state != null && state.Contains(coopPart.StartsWith("hosting") ? "hosting co-op" : "in co-op") && state.IndexOf(infoStation.Station, System.StringComparison.OrdinalIgnoreCase) >= 0, $"the Discord state says where and the co-op ({state})");
            var m = MissionManager.Instance.CurrentMission;
            bot.Note($"Steam status: '{plain}' / with the station: '{withStation}' (mission id {m?.MissionID})");
            bot.Check(!plain.StartsWith("In a mission") && !plain.StartsWith(m?.MissionID ?? "?"), $"the status names the mission by its title ({plain})");
            bot.Check(plain.Contains(" min") || plain.Contains("just started"), $"the status says how long the mission has run ({plain})");
            bot.Check(plain.EndsWith(coopPart), $"the status says co-op and the players ({plain})");
            bot.Check(withStation.Length > plain.Length && !plain.Contains("turret") && !plain.Contains(" at "), $"the station shows only when turned on ({withStation})");
        }

        static IEnumerable<object> ControlsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Computer != null, 120, "guest mission loaded")) yield return x;
            bot.Note($"registry: {Controls.Instance.DialIds.Count()} dials/sliders, {Controls.Instance.ButtonCount} buttons");
            PresenceChecks(bot, "hosting co-op, 2 players");
            bot.Signal("ready");

            // 1. guest drags the range dial. The host only sees it: its own player must stay free (the game's drag
            // takes the dragging player's input; a replayed drag froze the host while the guest turned a dial)
            var broker = InteractionLockBroker.FindOrNull();
            int locksBefore = broker != null ? broker.ActiveRequestCount : -1, locksMax = locksBefore, skippedBefore = Controls.LocksSkipped;
            foreach (var x in bot.Until(() => { if (broker != null) locksMax = Mathf.Max(locksMax, broker.ActiveRequestCount); return bot.HasSignal("range-set"); }, 60, "guest set range dial")) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            float range = Computer.rangeDial.AccumulatedValue;
            bot.Check(Mathf.Abs(range - float.Parse(bot.SignalValue("range-set"))) < 0.01f, $"host sees the guest's range dial ({range:F3} vs {bot.SignalValue("range-set")})");
            bot.Note($"input locks on the host during the guest's drag: {locksBefore} before, at most {locksMax}; replayed drag locks skipped {Controls.LocksSkipped - skippedBefore}");
            bot.Check(broker != null && locksMax == locksBefore, $"the host's player stayed free while the guest dragged (input locks {locksBefore} -> max {locksMax})");

            // 2. host drags the charge dial
            foreach (var x in DragDial(bot, Computer.powderChargeDial, 4f)) yield return x;
            bot.Signal("charge-set", Computer.powderChargeDial.AccumulatedValue.ToString("R"));

            // 3. guest presses Calculate
            int cardsBefore = CardCount();
            int execBefore = Controls.Instance.ClicksExecuted;
            bot.Signal("press");
            foreach (var x in bot.Until(() => bot.HasSignal("pressed"), 30, "guest pressed calculate")) yield return x;
            foreach (var x in bot.Until(() => Controls.Instance.ClicksExecuted >= execBefore + 2, 10, "host executed the guest's press (down+up)")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Note($"cards on host: {cardsBefore} -> {CardCount()}");
            bot.Signal("cards", CardCount().ToString());
            // hover: a control that reacts to being looked at (the firing lever's clamp) must react on the guest too
            LookAtTarget hovered = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LookAtTarget>()))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b != null && b.gameObject.activeInHierarchy && Wiring.ListenersOf(b).Any(l => l.StartsWith("onLookAt:")) && !(Util.ScenePath.Of(b.transform) ?? "").Contains("Main Camera/")) { hovered = b; break; }
            }
            bot.Note($"host hovers {Util.ScenePath.Of(hovered?.transform)}: {(hovered == null ? "-" : string.Join(" | ", Wiring.ListenersOf(hovered)))}");
            if (hovered != null) { RealHover(hovered, true); foreach (var x in bot.Wait(1.5)) yield return x; RealHover(hovered, false); }   // the cursor manager's path
            bot.Signal("hovered", hovered == null ? "none" : "1");
            // a press whose effects are only presentation (animation/sound/shake): the guest must play them too
            LookAtTarget showy = null;
            foreach (var o in Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<LookAtTarget>()))
            {
                var b = o.TryCast<LookAtTarget>();
                if (b == null || !b.gameObject.activeInHierarchy || (b.onClickDown?.m_Calls?.m_RuntimeCalls?.Count ?? 0) > 0) continue;
                var ls = Util.Listeners.Of(b).Where(l => l.Event == "onClickDown").ToList();
                if (ls.Count > 0 && ls.All(l => l.TargetType == "Animator" || l.TargetType == "AudioSource" || l.TargetType == "StudioEventEmitter")) { showy = b; break; }
            }
            bot.Note($"host presses a show-only button: {Util.ScenePath.Of(showy?.transform)}");
            if (showy != null) foreach (var x in Press(bot, showy)) yield return x;
            bot.Signal("showy", showy == null ? "none" : "1");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> ControlsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Computer != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            PresenceChecks(bot, "in co-op, 2 players");

            foreach (var x in DragDial(bot, Computer.rangeDial, 12.5f)) yield return x;
            bot.Signal("range-set", Computer.rangeDial.AccumulatedValue.ToString("R"));

            var broker = InteractionLockBroker.FindOrNull();
            int locksBefore = broker != null ? broker.ActiveRequestCount : -1, locksMax = locksBefore;
            // regression: hovering a dial another player turns made this player's cursor grab it. While the
            // host's drag is replayed here, point the cursor at that dial and let the game try to adopt the drag.
            var dcm = Object.FindObjectOfType<DynamicCursorManager>();
            var chargeDial = Computer.powderChargeDial;
            var chargeTarget = chargeDial.GetComponent<Interactable>() ?? chargeDial.GetComponentInParent<Interactable>() ?? chargeDial.GetComponentInChildren<Interactable>();
            bool tried = false, adopted = false, began = false, pressRefused = false; float pointerMoved = -1;
            foreach (var x in bot.Until(() =>
            {
                if (broker != null) locksMax = Mathf.Max(locksMax, broker.ActiveRequestCount);
                if (!tried && dcm != null && chargeTarget != null && Controls.Replayed.Contains(chargeDial.Pointer))
                {
                    tried = true;
                    var keepHover = dcm._currentHover;
                    dcm._currentHover = chargeTarget;
                    dcm.TryAdoptExternalActiveDrag();
                    adopted = dcm.IsDragging;
                    if (adopted) dcm.NotifyExternalDragEnded(chargeDial.Cast<ICursorDraggable>());
                    // the cursor's own begin (its Update takes a hovered control that is being dragged): not either
                    if (!adopted)
                    {
                        dcm.BeginDrag(chargeDial.Cast<ICursorDraggable>());
                        began = dcm.IsDragging;
                        if (began) dcm.NotifyExternalDragEnded(chargeDial.Cast<ICursorDraggable>());
                    }
                    dcm._currentHover = keepHover;
                    // this player's pointer moves over the dial (the camera turns): the dial must not follow it
                    var cam = Camera.main;
                    if (cam != null)
                    {
                        var before = chargeDial.GetPointerWorldPointOnDialPlane();
                        var keepRot = cam.transform.rotation;
                        cam.transform.rotation = keepRot * Quaternion.Euler(4f, 7f, 0);
                        var after = chargeDial.GetPointerWorldPointOnDialPlane();
                        cam.transform.rotation = keepRot;
                        pointerMoved = (after - before).magnitude;
                    }
                    // a press on the dial the host holds: not a second drag, and its release does not end the host's
                    int refused = Controls.PressesRefused;
                    chargeDial.HandlePrimaryClickDown(chargeTarget);
                    chargeDial.HandlePrimaryClickUp(chargeTarget);
                    pressRefused = Controls.PressesRefused == refused + 1 && Controls.Replayed.Contains(chargeDial.Pointer);
                }
                return bot.HasSignal("charge-set");
            }, 60, "host set charge dial")) yield return x;
            bot.Note($"hover adoption test: tried {tried} (cursor manager {dcm != null}, dial target {chargeTarget != null}), skipped {Controls.AdoptionsSkipped}");
            bot.Check(tried && !adopted, $"hovering the dial the host turns does not grab it here (tried {tried}, adopted {adopted})");
            bot.Check(tried && pointerMoved >= 0 && pointerMoved < 1e-4f, $"while the host turns it, the dial does not follow this player's moving pointer (pointer moved {pointerMoved:F4} m on its plane)");
            bot.Check(tried && pressRefused, "a press on the dial the host holds is refused and leaves the host's drag going");
            bot.Check(tried && !began, $"the cursor does not start grabbing the dial the host holds either (the hand stayed closed) (began {began})");
            foreach (var x in bot.Wait(0.5)) yield return x;
            float charge = Computer.powderChargeDial.AccumulatedValue;
            bot.Check(Mathf.Abs(charge - float.Parse(bot.SignalValue("charge-set"))) < 0.01f, $"guest sees the host's charge dial ({charge:F3} vs {bot.SignalValue("charge-set")})");
            bot.Check(broker != null && locksMax == locksBefore, $"the guest's player stayed free while the host dragged (input locks {locksBefore} -> max {locksMax})");

            foreach (var x in bot.Until(() => bot.HasSignal("press"), 60, "host asks for a press")) yield return x;
            int cardsBefore = CardCount();
            var btn = Computer.calculateButton;
            btn.OnClickDown();
            foreach (var x in bot.Wait(0.15)) yield return x;
            btn.OnClickUp();
            bot.Check(Controls.Instance.ClicksForwarded >= 2, $"guest forwarded its press ({Controls.Instance.ClicksForwarded})");
            bot.Signal("pressed");
            foreach (var x in bot.Until(() => bot.HasSignal("cards"), 30, "host card count")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Note($"cards on guest: {cardsBefore} -> {CardCount()} (host has {bot.SignalValue("cards")}), received from the host {Items.Instance.CardsSpawned}");
            bot.Check(CardCount().ToString() == bot.SignalValue("cards"), "guest has the host's cards");
            bot.Check(Items.Instance.CardsSpawned == CardCount() - cardsBefore, "every new card here came from the host (no local calculation)");
            foreach (var x in bot.Shot("guest-controls")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("hovered"), 30, "host hovered a control")) yield return x;
            foreach (var x in bot.Until(() => Controls.Instance.HoversApplied >= 2, 5, null, soft: true)) yield return x;
            if (bot.SignalValue("hovered") != "none")
                bot.Check(Controls.Instance.HoversApplied >= 2, $"the host's hover (look at, look away) played here too ({Controls.Instance.HoversApplied})");
            foreach (var x in bot.Until(() => bot.HasSignal("showy"), 30, "host pressed a show-only button")) yield return x;
            foreach (var x in bot.Until(() => Controls.Instance.PresentationsReplayed > 0, 5, null, soft: true)) yield return x;
            if (bot.SignalValue("showy") != "none")
                bot.Check(Controls.Instance.PresentationsReplayed > 0, $"the host's press played its animation/sound here too ({Controls.Instance.PresentationsReplayed})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
