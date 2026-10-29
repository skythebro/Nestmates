using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        /// <summary>Cards this player can see (rule on: the operator's card is hidden for everyone else).</summary>
        static int VisibleCards() => Cards().Count(c => c.gameObject.activeInHierarchy);

        /// <summary>The card is where its player reads it: on their own notepad (under their camera), shown.</summary>
        static bool OnMyNotepad(Component c) => c != null && c.gameObject.activeInHierarchy && (Util.ScenePath.Of(c.transform) ?? "").Contains("Main Camera");
        static int NotepadCards() => Cards().Count(c => OnMyNotepad(c));

        static List<FireMissionCard> Cards()
        {
            var list = new List<FireMissionCard>();
            var pr = Printer;
            if (pr?.spawnedCardInstances == null) return list;
            foreach (var go in pr.spawnedCardInstances) { var c = go == null ? null : (go.GetComponent<FireMissionCard>() ?? go.GetComponentInChildren<FireMissionCard>(true)); if (c != null) list.Add(c); }
            return list;
        }

        /// <summary>Location + position relative to its surface (clipboards hang off each player's own camera,
        /// so world positions differ between players while the card sits in the same spot on the clipboard).</summary>
        static string Where(DraggableItem d)
        {
            if (d == null) return "missing";
            var p = d.surfaceRef != null ? d.surfaceRef.transform.InverseTransformPoint(d.transform.position) : d.transform.position;
            return $"{d.CurrentLocation}@{(d.surfaceRef != null ? d.surfaceRef.name : "-")}|{p.x:F3};{p.y:F3};{p.z:F3}";
        }

        static bool SamePlace(string a, string b, float tol = 0.02f)
        {
            var x = a.Split('|'); var y = b.Split('|');
            if (x.Length != 2 || y.Length != 2 || x[0] != y[0]) return false;
            var p = x[1].Split(';').Select(float.Parse).ToArray(); var q = y[1].Split(';').Select(float.Parse).ToArray();
            return Vector3.Distance(new Vector3(p[0], p[1], p[2]), new Vector3(q[0], q[1], q[2])) < tol;
        }

        /// <summary>Slide an item 12 cm along its surface and settle it there, as a drop would.</summary>
        static void Nudge(DraggableItem d)
        {
            var surf = d.surfaceRef;
            var t = d.transform;
            t.position += (surf != null ? surf.transform.right : Vector3.right) * 0.12f;
            if (surf != null) d.SettleOnSurface(surf); else d.MoveToSurface(false, true);
        }

        static IEnumerable<object> ItemsHost(Bot bot)
        {
            Settings.CardsToComputerOnly = false;   // a failed earlier run may have left the rule on in this copy's config
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, LaunchArgs.Mission ?? "Artillery Introduction")) yield return x;   // its computer prints right away
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1) && Computer != null, 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            int before = Cards().Count;
            bot.Signal("ready", before.ToString());

            // 1. the guest presses Calculate: one card, printed here, appears there with the same id
            foreach (var x in bot.Until(() => Cards().Count > before, 30, "host printed the guest's card")) yield return x;
            foreach (var x in bot.Wait(2.5)) yield return x;   // printing animation settles
            var card = Cards().Last();
            var cd = card.GetComponent<DraggableItem>();
            bot.Signal("card", ItemId.Of(card));
            bot.Note($"host card {ItemId.Of(card)} at {Where(cd)}");
            bot.Check(OnMyNotepad(card), $"rule off: the host's copy lies on the host's own notepad ({Util.ScenePath.Of(card.transform)})");

            // 2. rule off = everyone has their own copy: the guest moving theirs leaves the host's where it is
            string mine = Where(cd);
            foreach (var x in bot.Until(() => bot.HasSignal("ticked"), 60, "guest ticked its copy")) yield return x;
            {
                ToggleActive t = null;
                foreach (var bb in card.GetComponentsInChildren<LookAtTarget>(true)) if (bb.name.StartsWith("Compleated button")) t = bb.GetComponent<ToggleActive>();
                bool want = bot.SignalValue("ticked") == "1";
                foreach (var x in bot.Until(() => t != null && t.target != null && t.target.activeSelf == want, 5, null, soft: true)) yield return x;
                bot.Check(want && t != null && t.target != null && t.target.activeSelf, $"the guest ticked its copy completed and the host's copy shows the tick (guest {want}, host {t?.target?.activeSelf})");
            }
            foreach (var x in bot.Until(() => bot.HasSignal("moved"), 60, "guest moved its copy")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            bot.Check(card.gameObject.activeInHierarchy && SamePlace(Where(cd), mine), $"the host's own copy stays put and visible when the guest moves theirs ({Where(cd)} vs {mine})");

            // 3. the host moves another item (a map token if there is one)
            var token = Object.FindObjectsOfType(Il2CppType.Of<DraggableItem>()).Select(o => o.TryCast<DraggableItem>())
                .Where(d => d != null && d.gameObject.activeInHierarchy && d.CurrentLocation == DraggableItem.ItemLocation.Surface && d.GetComponent<FireMissionCard>() == null && d.GetComponentInChildren<RecordItem>() == null)
                .OrderByDescending(d => MissionSaveManager.IsMapToken(d)).FirstOrDefault();
            bot.Check(token != null, "an item to move exists");
            if (token != null)
            {
                Nudge(token);
                foreach (var x in bot.Wait(1)) yield return x;
                bot.Note($"host moved {token.name} ({ItemId.Of(token)}, map token {MissionSaveManager.IsMapToken(token)}) to {Where(token)}");
                bot.Signal("hostmoved", ItemId.Of(token) + "^" + Where(token));
            }
            // 4. host rule: cards only to the computer operator
            foreach (var x in bot.Until(() => bot.HasSignal("ready2"), 60, "guest checked moves")) yield return x;
            Settings.CardsToComputerOnly = true;
            int hostBefore = VisibleCards();
            bot.Signal("press2");
            foreach (var x in bot.Until(() => bot.HasSignal("got2"), 60, "guest got its operator card")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            bot.Check(VisibleCards() == hostBefore, $"rule on, guest pressed: the guest holds the card, the host sees none ({hostBefore} -> {VisibleCards()})");
            foreach (var x in DragDial(bot, Computer.rangeDial, 18f)) yield return x;   // a new target
            foreach (var x in bot.Wait(0.8)) yield return x;
            Computer.calculateButton.OnClickDown();
            foreach (var x in bot.Wait(0.15)) yield return x;
            Computer.calculateButton.OnClickUp();
            foreach (var x in bot.Until(() => VisibleCards() == hostBefore + 1, 15, "rule on, host pressed: the host got its card")) yield return x;
            bot.Signal("hostpressed");
            foreach (var x in bot.Until(() => bot.HasSignal("verdict"), 60, "guest verdict")) yield return x;
            Settings.CardsToComputerOnly = false;
            MissionManager.Instance.EndOperationAndReturnToMenu();
            bot.Signal("done");
            foreach (var x in bot.Wait(1)) yield return x;
        }

        static IEnumerable<object> ItemsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady && Computer != null && bot.HasSignal("ready"), 180, "in mission")) yield return x;
            foreach (var x in bot.Wait(1)) yield return x;
            int before = Cards().Count;
            bot.Check(before.ToString() == bot.SignalValue("ready"), $"same cards before printing ({before} vs {bot.SignalValue("ready")})");
            foreach (var x in DragDial(bot, Computer.rangeDial, 12.5f)) yield return x;       // a firing solution needs a range
            foreach (var x in DragDial(bot, Computer.powderChargeDial, 4f)) yield return x;   // ... and a powder charge
            foreach (var x in bot.Wait(0.8)) yield return x;
            var btn = Computer.calculateButton;
            btn.OnClickDown();
            foreach (var x in bot.Wait(0.15)) yield return x;
            btn.OnClickUp();

            foreach (var x in bot.Until(() => bot.HasSignal("card"), 60, "host printed")) yield return x;
            string id = bot.SignalValue("card");
            FireMissionCard card = null;
            foreach (var x in bot.Until(() => (card = ItemId.Find<FireMissionCard>(id)) != null, 10, "the host's card exists here with the host's id")) yield return x;
            bot.Check(Cards().Count == before + 1, $"exactly one new card here ({before} -> {Cards().Count})");
            var cd = card.GetComponent<DraggableItem>();
            foreach (var x in bot.Until(() => OnMyNotepad(card), 6, null, soft: true)) yield return x;
            bot.Check(OnMyNotepad(card), $"rule off: the guest's copy lies on the guest's own notepad ({Util.ScenePath.Of(card.transform)})");
            bot.Note($"guest card {id} at {Where(cd)}");

            // the "completed" tick on the guest's own copy shows on the host's copy too
            LookAtTarget tickBtn = null;
            foreach (var bb in card.GetComponentsInChildren<LookAtTarget>(true)) if (bb.name.StartsWith("Compleated button")) tickBtn = bb;
            bot.Check(tickBtn != null, "the card has its completed button");
            if (tickBtn != null) foreach (var x in RealPress(bot, tickBtn)) yield return x;
            foreach (var x in bot.Wait(0.5)) yield return x;
            var tickT = tickBtn?.GetComponent<ToggleActive>();
            bot.Signal("ticked", tickT != null && tickT.target != null && tickT.target.activeSelf ? "1" : "0");
            Nudge(cd);
            foreach (var x in bot.Wait(3)) yield return x;   // let the settle slide finish
            bot.Signal("moved", Where(cd));

            foreach (var x in bot.Until(() => bot.HasSignal("hostmoved"), 60, "host moved an item")) yield return x;
            var parts = bot.SignalValue("hostmoved").Split('^');
            foreach (var x in bot.Wait(1)) yield return x;
            var mine = ItemId.Find<DraggableItem>(parts[0]);
            bot.Check(mine != null, $"the host's item {parts[0]} exists here");
            bot.Check(mine != null && SamePlace(Where(mine), parts[1]), $"the host's move shows here ({Where(mine)} vs {parts[1]})");
            bot.Note($"items: places sent {Items.Instance.PlacesSent}, applied {Items.Instance.PlacesApplied}, cards {Items.Instance.CardsSpawned}");

            // host rule: cards only to the computer operator
            bot.Signal("ready2");
            foreach (var x in bot.Until(() => bot.HasSignal("press2"), 60, "host turned the operator rule on")) yield return x;
            int mineBefore = VisibleCards();
            foreach (var x in bot.Wait(3)) yield return x;   // the printer finishes the previous card first
            for (int attempt = 0; attempt < 3 && VisibleCards() == mineBefore; attempt++)
            {
                // a new solution each time (a player dials a new target), and a press can be a "dead press"
                foreach (var x in DragDial(bot, Computer.rangeDial, 14f + attempt)) yield return x;
                foreach (var x in bot.Wait(0.8)) yield return x;
                Computer.calculateButton.OnClickDown();
                foreach (var x in bot.Wait(0.15)) yield return x;
                Computer.calculateButton.OnClickUp();
                foreach (var x in bot.Until(() => VisibleCards() > mineBefore, 6, null, soft: true)) yield return x;
            }
            bot.Check(VisibleCards() == mineBefore + 1, $"rule on, guest pressed: the guest got exactly its card ({mineBefore} -> {VisibleCards()})");
            foreach (var x in bot.Until(() => NotepadCards() >= 1 && Cards().Where(c => c.gameObject.activeInHierarchy).Any(c => OnMyNotepad(c)), 6, null, soft: true)) yield return x;
            bot.Check(Cards().Any(c => OnMyNotepad(c)), "rule on, guest pressed: the operator's card lies on the guest's notepad");
            bot.Signal("got2");
            foreach (var x in bot.Until(() => bot.HasSignal("hostpressed"), 60, "host pressed")) yield return x;
            foreach (var x in bot.Wait(2)) yield return x;
            bot.Check(VisibleCards() == mineBefore + 1, $"rule on, host pressed: the host holds it, the guest sees no new card ({mineBefore + 1} -> {VisibleCards()})");
            bot.Signal("verdict");
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 60, "host done")) yield return x;
        }
    }
}
