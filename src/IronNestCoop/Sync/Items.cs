using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Physical paperwork: fire-mission cards, map tokens and every other draggable item. Whoever moves an item
    /// owns that move: while dragging, its pose streams 10 times a second (others see it travel); on release the final placement
    /// goes out in the game's own save format (MissionSaveManager.Capture/RestoreDraggableSurfaceState: surface,
    /// slot or deck, pose, stacking). Cards are printed by the host only (a guest's Calculate press is forwarded)
    /// and created on guests from the host's card data, with the host's card id. Records are items too, except
    /// while in the record player's slot (Music owns that).
    /// A player's clipboard hangs off their own camera, so an item put there (F to pick up, or dragged onto it) is
    /// "held" by that player: others see it disappear until it is put down on a shared surface again.
    /// </summary>
    public sealed class Items : SyncModule
    {
        public override string Name => "items";
        public static Items Instance { get; private set; }

        enum Ev : byte { Pose = 1, Place = 2, CardSpawn = 3, Held = 4, CardCheck = 5 }

        readonly HashSet<IntPtr> _dragging = new();          // items the local hand is dragging
        readonly Dictionary<IntPtr, DraggableItem> _dirty = new();

        /// <summary>Last placement sent or applied per item, and the previous poll's sample (to send only settled changes).</summary>
        struct Snap { public int Loc; public Vector3 Pos; public Quaternion Rot; public bool Active; }
        readonly Dictionary<IntPtr, Snap> _known = new(), _prev = new();
        readonly List<DraggableItem> _all = new();
        float _nextPoll, _nextList;
        static readonly Dictionary<IntPtr, float> RemoteHeld = new();   // item pointer to the time until which a remote hand counts as holding it
        float _nextSend;

        public int PlacesSent, PlacesApplied, CardsSpawned, PosesApplied, HeldSent, HeldApplied;

        /// <summary>Items a remote player holds on their clipboard (hidden here): item pointer to holder slot.</summary>
        readonly Dictionary<IntPtr, (DraggableItem item, byte slot)> _heldByRemote = new();
        /// <summary>Hidden here because another player holds it on their clipboard (it still exists).</summary>
        public bool HeldByRemote(DraggableItem d) => d != null && _heldByRemote.ContainsKey(d.Pointer);
        /// <summary>Items we announced as held by us (no repeat while the clipboard moves with the camera).</summary>
        readonly HashSet<IntPtr> _heldSent = new();

        /// <summary>
        /// Printed cards when "everyone gets a copy" (host rule off): each player's copy lands on their own notepad
        /// and is theirs alone, so copies are never synced after they are printed (the ids are the same on every
        /// machine, so syncing one copy would move or hide everyone else's).
        /// </summary>
        static readonly HashSet<IntPtr> _copies = new();

        public Items() { Instance = this; }
        static readonly bool NoApply = LaunchArgs.Mutant("noitems");

        public override void Register(Session s) => s.On((ushort)Msg.Card, OnEvent);
        public override void OnSessionStart() { _dragging.Clear(); _dirty.Clear(); RemoteHeld.Clear(); _known.Clear(); _prev.Clear(); _all.Clear(); _nextList = 0; ReleaseAllHeld(); _heldSent.Clear(); _cardOwner.Clear(); _copies.Clear(); }

        public override void OnSessionEnd() => ReleaseAllHeld();

        /// <summary>A player who leaves puts down what they held: show it again where it was.</summary>
        public override void OnPlayerLeft(Player p)
        {
            foreach (var kv in new List<KeyValuePair<IntPtr, (DraggableItem item, byte slot)>>(_heldByRemote))
                if (kv.Value.slot == p.Slot) { Show(kv.Value.item); _heldByRemote.Remove(kv.Key); }
        }

        void ReleaseAllHeld()
        {
            foreach (var kv in _heldByRemote) Show(kv.Value.item);
            _heldByRemote.Clear();
        }

        static void Show(DraggableItem d) { if (d != null && !d.gameObject.activeSelf) d.gameObject.SetActive(true); }

        /// <summary>Under a player's own camera (clipboard, notepad): the same path exists on every machine, but
        /// on each it is that machine's player.</summary>
        static bool Personal(string path) => path != null && (path.Contains(":Main Camera/") || path.Contains("/Main Camera/"));

        static void ToMyNotepad(DraggableItem d)
        {
            // A printed card carries a MoveToSurfaceBridge that slides it onto the notepad once. A restore switches
            // that off (PrepareForMissionRestore), so we ask for the move ourselves.
            var surf = d.GetComponentInChildren<MoveToSurfaceBridge>(true) ?? d.GetComponentInParent<MoveToSurfaceBridge>();
            var bridge = surf == null ? (d.GetComponentInChildren<MoveToClipboardCenterBridge3D>(true) ?? d.GetComponentInParent<MoveToClipboardCenterBridge3D>()) : null;
            if (surf == null && bridge == null) { CoopLog.Warn($"[items] card {d.name} has no notepad mover; it stays at the printer"); return; }
            try { Coop.Apply("card to notepad", () => { if (surf != null) surf.MoveToDestinationNow(); else bridge.MoveToClipboardCenteredNow(); }); }
            catch (Exception e) { CoopLog.ErrorOnce("items-notepad", "[items] moving a card onto the notepad failed: " + e.Message); }
            CoopLog.Debug($"[items] card {d.name} moved onto my notepad");
        }

        // ------------------------------------------------------------------ the "completed" tick on a fire-mission card

        // Every player may hold a copy of the same card (same id). Ticking it off as completed is shared, so the crew
        // sees which fire missions are done; deleting a copy stays each player's own business.
        public int CardChecksApplied, CardChecksSent;

        static ToggleActive CardTick(FireMissionCard card)
        {
            foreach (var b in card.GetComponentsInChildren<LookAtTarget>(true))
                if (b.name.StartsWith("Compleated button")) return b.GetComponent<ToggleActive>();
            return null;
        }

        static bool TickShown(ToggleActive t) => t != null && t.target != null && t.target.activeSelf;

        bool SetCardDone(string id, bool done)
        {
            var card = ItemId.Find<FireMissionCard>(id);
            var tick = card == null ? null : CardTick(card);
            if (tick == null || TickShown(tick) == done) return false;
            Coop.Apply("card tick " + id, () => tick.Toggle());
            CoopLog.Debug($"[items] card {id} ticked {(done ? "completed" : "open")} by another player");
            return true;
        }

        internal void LocalCardTick(LookAtTarget b)
        {
            if (!Coop.InSession || (Coop.Applying && !Coop.IsHost) || !b.name.StartsWith("Compleated button")) return;
            var card = b.GetComponentInParent<FireMissionCard>();
            var tick = b.GetComponent<ToggleActive>();
            if (card == null || tick == null) return;
            var w = Begin(Ev.CardCheck); w.Str(ItemId.Of(card)); w.Bool(TickShown(tick));
            if (Coop.IsHost) WorldReady.SendToReady(Msg.Card, w); else Coop.SendToHost(Msg.Card, w);
            CardChecksSent++;
        }

        [HarmonyPatch(typeof(LookAtTarget), nameof(LookAtTarget.OnClickDown))]
        static class CardTickPatch
        {
            static void Postfix(LookAtTarget __instance) { try { Instance?.LocalCardTick(__instance); } catch (Exception e) { CoopLog.ErrorOnce("items-tick", "[items] sharing a card tick failed: " + e.Message); } }
        }

        static bool OnPersonalSurface(DraggableItem d) =>
            Personal(SurfacePath(d)) || (d.CurrentLocation == DraggableItem.ItemLocation.Slot && d.SlotRef != null && Personal(ScenePath.Of(d.SlotRef.transform)));

        static readonly int DeckLoc = (int)DraggableItem.ItemLocation.Deck;

        const float Far = 1e5f;
        static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z) && Mathf.Abs(p.x) < Far && Mathf.Abs(p.y) < Far && Mathf.Abs(p.z) < Far;
        static bool Finite(Quaternion q) => float.IsFinite(q.x) && float.IsFinite(q.y) && float.IsFinite(q.z) && float.IsFinite(q.w) && q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w > 1e-6f;
        static bool Finite(SaveVector3 v) => Finite(new Vector3(v.X, v.Y, v.Z));
        static bool Finite(SaveQuaternion v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
        static bool Finite(MissionSaveManager.DraggableSurfaceSaveState s) =>
            Finite(s.SurfaceLocalPosition) && Finite(s.WorldPosition) && Finite(s.LocalScale) && Finite(s.SurfaceLocalRotation) && Finite(s.WorldRotation)
            && float.IsFinite(s.StackingNormalOffset) && Mathf.Abs(s.StackingNormalOffset) < Far;

        static Snap SnapOf(DraggableItem d) => new Snap { Loc = (int)d.CurrentLocation, Pos = d.transform.position, Rot = d.transform.rotation, Active = d.gameObject.activeInHierarchy };
        static bool Same(Snap a, Snap b) => a.Loc == b.Loc && a.Active == b.Active && (a.Pos - b.Pos).sqrMagnitude < 1e-6f && Quaternion.Angle(a.Rot, b.Rot) < 0.5f;

        /// <summary>
        /// Settled-change poll: any code path may move an item (drops, printer animations, game logic), so every
        /// 0.25 s each item is compared with the last placement sent/applied; a change that has come to rest is sent.
        /// </summary>
        void Poll()
        {
            if (Time.unscaledTime >= _nextList && Perf.HeavyFrame())
            {
                _nextList = Time.unscaledTime + 5f;   // items that appear (deliveries, printed cards) are named when they arrive
                _all.Clear();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DraggableItem>(), true))
                {
                    var d = o.TryCast<DraggableItem>();
                    if (d != null && !Skip(d)) { _all.Add(d); if (!_known.ContainsKey(d.Pointer)) _known[d.Pointer] = SnapOf(d); }
                }
            }
            foreach (var d in _all)
            {
                if (d == null || _dragging.Contains(d.Pointer) || RemoteHeld.ContainsKey(d.Pointer) || _heldByRemote.ContainsKey(d.Pointer) || d.IsSliding || Skip(d)) continue;
                var now = SnapOf(d);
                bool settled = _prev.TryGetValue(d.Pointer, out var before) && Same(before, now);
                _prev[d.Pointer] = now;
                if (settled && _known.TryGetValue(d.Pointer, out var k) && !Same(k, now) && now.Active)
                {
                    // Inside a deck the game lays the cards out itself on every machine (as a fan), so only going
                    // in or out of the deck is worth sending. Sending shuffles within it made the decks fight.
                    if (k.Loc == DeckLoc && now.Loc == DeckLoc) { _known[d.Pointer] = now; continue; }
                    _dirty[d.Pointer] = d;
                }
            }
        }

        /// <summary>Not synced here: records in the record player's slot (Music places and removes those), and a cup
        /// on its way to a drinker's mouth and back (the drink is shown by Espresso, the cup ends where it was).</summary>
        static bool Skip(DraggableItem d) =>
            d == null || _copies.Contains(d.Pointer) || Espresso.InDrink(d) || (d.CurrentLocation == DraggableItem.ItemLocation.Slot && d.SlotRef != null
                          && d.SlotRef.GetComponentInParent<RecordPlayerController>() != null && d.GetComponentInChildren<RecordItem>() != null);

        static NetWriter Begin(Ev ev) { var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); return w; }

        /// <summary>
        /// All drag surfaces, the sender's actual surface first: the game's restore picks the FIRST surface whose
        /// tag matches, several surfaces share a tag, and the search order differs between machines.
        /// </summary>
        static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DragSurface> Surfaces(string preferredPath)
        {
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DragSurface>(), true);
            var list = new List<DragSurface>(found.Length);
            DragSurface first = null;
            var pref = preferredPath == null ? null : ScenePath.Find(preferredPath);
            if (pref != null) first = pref.GetComponent<DragSurface>();
            if (first != null) list.Add(first);
            foreach (var o in found) { var ds = o.TryCast<DragSurface>(); if (ds != null && (first == null || ds.Pointer != first.Pointer)) list.Add(ds); }
            var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DragSurface>(list.Count);
            for (int i = 0; i < list.Count; i++) arr[i] = list[i];
            return arr;
        }

        static string SurfacePath(DraggableItem d) => d?.surfaceRef != null ? ScenePath.Of(d.surfaceRef.transform) : null;

        // ------------------------------------------------------------------ sending

        static readonly bool NoMoveToDeck = LaunchArgs.Mutant("nomovetodeck");   // used by the tests to prove the dice test's gather check really depends on MoveToDeck

        /// <summary>The deck on the surface a placement names (the card's own, else the one on that surface).</summary>
        static DraggableItemDeckArea DeckFor(DraggableItem d, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<DragSurface> surfaces)
        {
            if (d.deckRef != null) return d.deckRef;
            var surface = surfaces != null && surfaces.Length > 0 ? surfaces[0] : null;
            if (surface == null || DraggableItemDeckArea.AllDecks == null) return null;
            foreach (var deck in DraggableItemDeckArea.AllDecks)
                if (deck != null && deck.GetComponentInParent<DragSurface>()?.Pointer == surface.Pointer) return deck;
            return null;
        }

        public override void Tick(float dt)
        {
            if (!Coop.InSession || !(WorldReady.LocalReady || Coop.IsHost)) return;
            ItemId.EnsureScanned();
            foreach (var k in new List<IntPtr>(RemoteHeld.Keys)) if (Time.unscaledTime > RemoteHeld[k]) RemoteHeld.Remove(k);
            if (Time.unscaledTime >= _nextPoll && !Coop.Applying) { _nextPoll = Time.unscaledTime + 0.25f; Poll(); }
            if (Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 0.1f;
            foreach (var kv in new List<KeyValuePair<IntPtr, DraggableItem>>(_dirty))
            {
                var d = kv.Value;
                if (d == null) { _dirty.Remove(kv.Key); continue; }
                if (d.IsBeingDragged && _dragging.Contains(kv.Key))
                {
                    var w = Begin(Ev.Pose); w.Str(ItemId.Of(d));
                    var t = d.transform; w.Vec3(t.position.x, t.position.y, t.position.z); w.Quat(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w);
                    Coop.Send(Msg.Card, w, Delivery.Unreliable);
                    continue;
                }
                _dirty.Remove(kv.Key);
                SendPlace(d, null);
            }
        }

        void SendPlace(DraggableItem d, byte? slot)
        {
            if (OnPersonalSurface(d)) { SendHeld(d, slot); return; }
            _heldSent.Remove(d.Pointer);
            var state = MissionSaveManager.CaptureDraggableSurfaceState(d);
            if (state == null) return;
            _known[d.Pointer] = SnapOf(d);
            var w = Begin(Ev.Place); w.Str(ItemId.Of(d)); w.Str(SurfacePath(d)); w.Str(GameJson.Write(state));
            // in a slot: the game's restore only re-slots via the item's own SlotRef, which the receiver may not have
            bool slotted = d.CurrentLocation == DraggableItem.ItemLocation.Slot && d.SlotRef != null;
            w.Str(slotted ? ScenePath.Of(d.SlotRef.transform) : null);
            Music.WriteRot(w, slotted ? Music.LocalRotation(d, d.SlotRef) : new Quaternion(0, 0, 0, 0));
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Card, w, Delivery.Reliable, one);
            else { Coop.Send(Msg.Card, w); CoopLog.Debug($"[items] sent {ItemId.Of(d)} {d.CurrentLocation} on {SurfacePath(d) ?? "-"} at {d.transform.position:F3}"); }
            PlacesSent++;
        }

        /// <summary>This player holds the item (their clipboard): others hide it.</summary>
        void SendHeld(DraggableItem d, byte? slot)
        {
            _known[d.Pointer] = SnapOf(d);
            if (slot == null && !_heldSent.Add(d.Pointer)) return;   // already announced; the clipboard just moved
            var w = Begin(Ev.Held); w.Str(ItemId.Of(d)); w.U8(Coop.Session.LocalSlot);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Card, w, Delivery.Reliable, one);
            else { Coop.Send(Msg.Card, w); CoopLog.Debug($"[items] holding {ItemId.Of(d)} on my clipboard"); }
            HeldSent++;
        }

        void MarkDirty(DraggableItem d)
        {
            if (!Coop.InSession || Coop.Applying || Skip(d)) return;
            _dirty[d.Pointer] = d;
        }

        public override void SendSnapshot(byte slot)
        {
            // printed cards first (they must exist before they can be placed), then every item's placement
            var printer = UnityEngine.Object.FindObjectOfType<FireMissionCardPrinter>();
            if (printer?.spawnedCardInstances != null)
                foreach (var go in printer.spawnedCardInstances)
                {
                    var card = go?.GetComponent<FireMissionCard>();
                    if (card != null) SendCard(card, slot);
                }
            foreach (var o in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<DraggableItem>()))
            {
                var d = o.TryCast<DraggableItem>();
                if (d != null && !Skip(d) && d.gameObject.activeInHierarchy) SendPlace(d, slot);
            }
            // items other guests hold are hidden here, but the newcomer must hide them too
            foreach (var kv in _heldByRemote)
            {
                var d = kv.Value.item;
                if (d == null || kv.Value.slot == slot) continue;
                var w = Begin(Ev.Held); w.Str(ItemId.Of(d)); w.U8(kv.Value.slot);
                Coop.Session.Send((ushort)Msg.Card, w, Delivery.Reliable, slot);
            }
        }

        /// <summary>Cards printed for each player (host rule "cards only to the computer operator").</summary>
        readonly Dictionary<string, byte> _cardOwner = new();

        /// <summary>Card data to everyone (or one joiner): a copy for each, or the one card held by <c>holder</c>.</summary>
        void SendCard(FireMissionCard card, byte? slot)
        {
            var data = card.CaptureMissionState();
            if (data == null) return;
            var di = card.GetComponent<DraggableItem>();
            bool copy = di != null && _copies.Contains(di.Pointer);
            byte holder = _cardOwner.TryGetValue(ItemId.Of(card), out var o) ? o : (byte)0;
            var w = Begin(Ev.CardSpawn); w.Str(SurfacePath(di)); w.Str(GameJson.Write(data));
            w.Bool(copy); w.U8(holder);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Card, w, Delivery.Reliable, one);
            else WorldReady.SendToReady(Msg.Card, w);
        }

        // ------------------------------------------------------------------ receiving

        void OnEvent(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            if (!(WorldReady.LocalReady || Coop.IsHost)) return;
            var ev = (Ev)r.U8();
            switch (ev)
            {
                case Ev.Pose:
                {
                    var d = ItemId.Find<DraggableItem>(r.Str(2048));
                    var p = new Vector3(r.F32(), r.F32(), r.F32());
                    var q = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
                    if (!Finite(p) || !Finite(q)) return;   // from another player: NaN or far away would break the item
                    if (d == null || _dragging.Contains(d.Pointer)) return;   // never fight the local hand
                    RemoteHeld[d.Pointer] = Time.unscaledTime + 1f;
                    d.transform.SetPositionAndRotation(p, q);
                    PosesApplied++;
                    break;
                }
                case Ev.Place:
                {
                    string id = r.Str(2048), surface = r.Str(2048), json = r.Str(1 << 20), slotPath = r.Str(2048);
                    var slotRot = Music.ReadRot(r);
                    var d = ItemId.Find<DraggableItem>(id);
                    if (d == null) { CoopLog.Debug($"[items] unknown item {id}"); return; }
                    if (NoApply) return;   // used by the tests to prove that applying remote placements matters
                    if (_dragging.Contains(d.Pointer)) return;
                    var state = GameJson.Read<MissionSaveManager.DraggableSurfaceSaveState>(json);
                    if (state == null || !Finite(state)) return;
                    if (state.Location == DeckLoc && (int)d.CurrentLocation == DeckLoc && slotPath == null && SurfacePath(d) == surface)
                    { _known[d.Pointer] = SnapOf(d); _prev[d.Pointer] = _known[d.Pointer]; return; }   // already in that deck: its layout is local
                    RemoteHeld.Remove(d.Pointer);
                    if (_heldByRemote.Remove(d.Pointer)) Show(d);   // put down again by whoever held it
                    _heldSent.Remove(d.Pointer);
                    var surfaces = Surfaces(surface);
                    var slot = slotPath == null ? null : ScenePath.Find(slotPath)?.GetComponent<ItemSlot>();
                    if (slot != null) Coop.Apply("slot " + id, () => Music.PlaceInto(d, slot, slotRot));
                    else if (state.Location == DeckLoc && !NoMoveToDeck && DeckFor(d, surfaces) is DraggableItemDeckArea deck)
                    {
                        // Into a deck: do it the way the game does (MoveToDeck, as the host's "all back into the deck" and
                        // a drop on the fan do). The save restore put the card in the deck where it lay but the fan
                        // never took it in, so after the host gathered the cards the guest's stayed spread out.
                        Coop.Apply("to deck " + id, () => d.MoveToDeck(deck));
                    }
                    else Coop.Apply("place " + id, () => MissionSaveManager.RestoreDraggableSurfaceState(d, state, surfaces));
                    _known[d.Pointer] = SnapOf(d); _prev[d.Pointer] = _known[d.Pointer];   // no echo
                    if (c.Delivery == Delivery.Reliable && PlacesApplied > 0) CoopLog.Debug($"[items] applied {id} from {c.FromSlot}: {d.CurrentLocation} on {SurfacePath(d) ?? "-"} (sent {surface ?? "-"}) at {d.transform.position:F3}");
                    PlacesApplied++;
                    break;
                }
                case Ev.Held:
                {
                    string id = r.Str(2048); byte holder = r.U8();
                    var d = ItemId.Find<DraggableItem>(id);
                    if (d == null) { CoopLog.Debug($"[items] unknown held item {id}"); return; }
                    // a player can only say they hold something themselves (else anyone could hide any item)
                    if (!c.FromHost && holder != c.FromSlot) { CoopLog.Debug($"[items] slot {c.FromSlot} claimed {id} is held by slot {holder}: ignored"); return; }
                    if (NoApply || _dragging.Contains(d.Pointer)) return;
                    RemoteHeld.Remove(d.Pointer);
                    _dirty.Remove(d.Pointer);
                    _heldSent.Remove(d.Pointer);
                    _heldByRemote[d.Pointer] = (d, holder);
                    Coop.Apply("held " + id, () => d.gameObject.SetActive(false));
                    _known[d.Pointer] = SnapOf(d); _prev[d.Pointer] = _known[d.Pointer];
                    CoopLog.Debug($"[items] {id} is on player {holder + 1}'s clipboard");
                    HeldApplied++;
                    break;
                }
                case Ev.CardCheck:
                {
                    string id = r.Str(2048); bool done = r.Bool();
                    if (SetCardDone(id, done)) CardChecksApplied++;
                    if (Coop.IsHost) { var w = Begin(Ev.CardCheck); w.Str(id); w.Bool(done); WorldReady.SendToReady(Msg.Card, w); }   // relay it to the other players
                    break;
                }
                case Ev.CardSpawn:
                {
                    if (!c.FromHost) return;
                    string cardSurface = r.Str(2048);
                    var data = GameJson.Read<FireMissionCardSaveData>(r.Str(1 << 20));
                    bool copy = r.Bool(); byte holder = r.U8();
                    var printer = UnityEngine.Object.FindObjectOfType<FireMissionCardPrinter>();
                    if (data == null || printer == null) return;
                    if (ItemId.Find(data.Id) != null) return;   // already have it (snapshot after live spawn)
                    var surfaces = Surfaces(cardSurface);
                    Coop.Apply("card " + data.Id, () => printer.RestoreCard(data, surfaces));
                    _nextList = 0;   // pick the new card up in the poll list
                    // the id index refreshes at most once a second: take the card the printer just made
                    var spawned = printer.spawnedCardInstances;
                    var last = spawned != null && spawned.Count > 0 ? spawned[spawned.Count - 1] : null;
                    var nc = last != null ? last.GetComponent<DraggableItem>() : null;
                    nc ??= ItemId.Find<DraggableItem>(data.Id);
                    if (nc != null)
                    {
                        _known[nc.Pointer] = SnapOf(nc); _prev[nc.Pointer] = _known[nc.Pointer];
                        if (copy) _copies.Add(nc.Pointer);   // ours: it moves itself onto our notepad
                        else if (holder != Coop.Session.LocalSlot) { _heldByRemote[nc.Pointer] = (nc, holder); nc.gameObject.SetActive(false); }   // the operator holds it
                        _cardOwner[data.Id] = holder;
                        // ours (a copy, or we are the operator): a printed card slides onto the player's notepad when it
                        // spawns; a restored one stays where the host's was, so send it there the game's own way
                        if (copy || holder == Coop.Session.LocalSlot) ToMyNotepad(nc);
                    }
                    CoopLog.Debug($"[items] card {data.Id} arrived: {(copy ? "my own copy" : holder == Coop.Session.LocalSlot ? "for me (operator)" : $"held by player {holder + 1}")}");
                    CardsSpawned++;
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(DraggableItem), nameof(DraggableItem.BeginDragFromManager))]
        static class DragBegin
        {
            static void Postfix(DraggableItem __instance)
            {
                try
                {
                    if (Instance == null) return;
                    Instance._dragging.Add(__instance.Pointer);
                    Instance.MarkDirty(__instance);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Items.Postfix line 414", e); }
            }
        }

        [HarmonyPatch(typeof(DraggableItem), "EndDragInternal")]   // EndDragFromManager is only 0x15 bytes, too short to detour
        static class DragEnd
        {
            static void Postfix(DraggableItem __instance)
            {
                try
                {
                    if (Instance == null) return;
                    Instance._dragging.Remove(__instance.Pointer);
                    Instance.MarkDirty(__instance);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Items.Postfix line 425", e); }
            }
        }

        /// <summary>Every location change (surface, slot, deck), whoever caused it here.</summary>
        [HarmonyPatch(typeof(DraggableItem), nameof(DraggableItem.SetState))]
        static class StateChange { static void Postfix(DraggableItem __instance) { try { Instance?.MarkDirty(__instance); } catch (System.Exception e) { CoopLog.PatchFailed("Items.Postfix line 443", e); } } }

        /// <summary>A remote hand holds this item: its own follow/settle logic must not pull it back.</summary>
        [HarmonyPatch(typeof(DraggableItem), "Update")]
        static class HeldUpdate { static bool Prefix(DraggableItem __instance) { try { return RemoteHeld.Count == 0 || !RemoteHeld.ContainsKey(__instance.Pointer); } catch (System.Exception e) { CoopLog.PatchFailed("Items.Prefix line 447", e); return true; } } }

        /// <summary>Cards are printed by the host (a guest's Calculate press runs there) and sent as data.</summary>
        [HarmonyPatch(typeof(FireMissionCardPrinter), nameof(FireMissionCardPrinter.HandleCalculationSuccess))]
        static class Print
        {
            static bool Prefix()
            {
                try { return !GraphGate.Suppress; }
                catch (System.Exception e) { CoopLog.PatchFailed("Items.Prefix line 445", e); return true; }
            }

            static void Postfix(FireMissionCardPrinter __instance)
            {
                try
                {
                    if (!Coop.HostEmits || Instance == null) return;
                    var list = __instance.spawnedCardInstances;
                    var card = list != null && list.Count > 0 ? list[list.Count - 1]?.GetComponent<FireMissionCard>() : null;
                    if (card == null) return;
                    byte presser = Controls.PressingSlot;   // set while the host executes a guest's forwarded press
                    CoopLog.Debug($"[items] card printed (pressed by slot {presser}, operator rule {HostRules.On(Rule.CardsToOperator)})");
                    var di = card.GetComponent<DraggableItem>();
                    bool toOperator = HostRules.On(Rule.CardsToOperator);
                    if (toOperator)
                    {
                        // one card, held by the operator: everyone gets the object, hidden except for the operator,
                        // and it appears for all when the operator puts it down on the table or hands it over
                        Instance._cardOwner[ItemId.Of(card)] = presser;
                    }
                    else if (di != null) _copies.Add(di.Pointer);   // everyone gets a copy of their own
                    Instance.SendCard(card, null);   // before hiding it: a hidden card captures no data
                    if (toOperator && presser != 0 && di != null)
                    {
                        Instance._heldByRemote[di.Pointer] = (di, presser);
                        card.gameObject.SetActive(false);
                    }
                    Instance._nextList = 0;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Items.Postfix line 447", e); }
            }
        }
    }
}
