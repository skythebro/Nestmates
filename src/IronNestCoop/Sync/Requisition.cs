using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using Il2CppInterop.Runtime;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Wire;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// The requisition console. Each player's game deals the punchcard deck from that player's own unlocks, so in the
    /// host's world the host's deck counts: guests rebuild theirs from the host's list (card ids, uses left), and the
    /// host re-sends whenever its deck changes (a card used up or redeemed). A requisition itself runs on the host
    /// only (the lever is a forwarded button): points, the card's effects (shells, charges, turret moves, scout
    /// planes) and the card's consumption all come back through the usual state.
    /// </summary>
    public sealed class Requisition : SyncModule
    {
        public override string Name => "requisition";
        public static Requisition Instance { get; private set; }

        string _lastSent;
        float _nextPoll;
        public int Rebuilds;

        // Guest: uses of each card when this guest first saw the host's deck this mission, and uses spent since.
        int _usesEpoch = -1;
        readonly Dictionary<string, int> _firstUses = new(), _spent = new();
        public static IReadOnlyDictionary<string, int> Spent => Instance._spent;

        public Requisition() { Instance = this; }

        public override void Register(Session s)
        {
            s.On((ushort)Msg.Requisition, OnDeck);
            s.On((ushort)Msg.DeckOrder, OnOrder);
        }
        public override void OnSessionStart() { _lastSent = null; _orderSent.Clear(); _orderHost.Clear(); }

        static RequisitionConsoleManager Console => RequisitionConsoleManager.Instance;

        /// <summary>The deck as "id:uses" in deck order (test hook too).</summary>
        public static string DeckKey()
        {
            var cards = Console?.GetAllCards();
            if (cards == null) return "";
            var sb = new StringBuilder();
            foreach (var c in cards) if (c?.CurrentDefinition != null) sb.Append(c.CurrentDefinition.ID).Append(':').Append(c.CurrentDefinition.RemainingUses).Append(',');
            return sb.ToString();
        }

        public override void Tick(float dt)
        {
            TickOrder();
            if (!Coop.IsHost || Console == null || Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + 1f;
            string key = DeckKey();
            if (key != _lastSent) { _lastSent = key; Send(null); }
        }

        public override void SendSnapshot(byte slot)
        {
            Send(slot);
            foreach (var d in Decks()) SendOrder(d, slot);
        }

        void Send(byte? slot)
        {
            var cards = Console?.GetAllCards();
            if (cards == null) return;
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch);
            int n = 0; foreach (var c in cards) if (c?.CurrentDefinition != null) n++;
            w.VarU((ulong)n);
            foreach (var c in cards) if (c?.CurrentDefinition != null) { w.Str(c.CurrentDefinition.ID); w.VarI(c.CurrentDefinition.RemainingUses); }
            if (slot is byte one) Coop.Session.Send((ushort)Msg.Requisition, w, Net.Transport.Delivery.Reliable, one);
            else WorldReady.SendToReady(Msg.Requisition, w);
        }

        static Dictionary<string, PunchcardDefinitionV2> Definitions()
        {
            var d = new Dictionary<string, PunchcardDefinitionV2>();
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<PunchcardDefinitionV2>()))
            {
                var def = o.TryCast<PunchcardDefinitionV2>();
                if (def == null || string.IsNullOrEmpty(def.ID)) continue;
                bool original = !def.name.Contains("(Clone)");
                if (!d.ContainsKey(def.ID) || original) d[def.ID] = def;   // prefer the asset over mission copies
            }
            return d;
        }

        void OnDeck(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            int n = r.Count(256, "deck cards");
            var ids = new List<(string id, int uses)>();
            var key = new StringBuilder();
            for (int i = 0; i < n; i++) { var id = r.Str(256); int uses = (int)r.VarI(); ids.Add((id, uses)); key.Append(id).Append(':').Append(uses).Append(','); }
            TrackSpent(ids);
            var con = Console;
            if (con == null || DeckKey() == key.ToString()) return;
            var defs = Definitions();
            var list = new Il2CppSystem.Collections.Generic.List<PunchcardDefinitionV2>();
            foreach (var (id, _) in ids)
            {
                if (defs.TryGetValue(id, out var def)) list.Add(def);
                else CoopLog.Warn($"[requisition] the host's punchcard {id} is unknown here");
            }
            Coop.Apply("rebuild deck", () => con.RebuildDeck(list, true));
            // uses left as on the host (the rebuilt cards are fresh copies)
            var cards = con.GetAllCards();
            if (cards != null)
                for (int i = 0; i < cards.Length && i < ids.Count; i++)
                    if (cards[i]?.CurrentDefinition != null && cards[i].CurrentDefinition.ID == ids[i].id) cards[i].CurrentDefinition.RemainingUses = ids[i].uses;
            Rebuilds++;
            CoopLog.Info($"[requisition] deck rebuilt from the host's: {key}");
        }

        void TrackSpent(List<(string id, int uses)> ids)
        {
            if (_usesEpoch != MissionFlow.Instance.Epoch) { _usesEpoch = MissionFlow.Instance.Epoch; _firstUses.Clear(); _spent.Clear(); }
            var seen = new HashSet<string>();
            foreach (var (id, uses) in ids)
            {
                if (!seen.Add(id)) continue;   // first copy of a duplicate card stands for the id
                if (!_firstUses.TryGetValue(id, out var first)) _firstUses[id] = first = uses;
                _spent[id] = System.Math.Max(0, first - uses);
            }
            foreach (var kv in _firstUses) if (!seen.Contains(kv.Key)) _spent[kv.Key] = kv.Value;   // used up: left the deck
        }

        // ------------------------------------------------------------------ the order of the fanned deck

        // Which card lies where in the fan is the deck's own list (DraggableItemDeckArea.items): every machine laid
        // out its own order (dealt in a different order, a card dropped back in at another spot, "reset punchcards"),
        // so the same slots held different cards. The host's order is the source of truth; a guest who drags a card to another
        // place in the fan asks the host, which re-sends the order to everyone.
        static readonly bool NoDeckOrder = LaunchArgs.Mutant("nodeckorder");   // used by the tests to prove the dice test depends on the shared order
        readonly Dictionary<string, string> _orderSent = new();   // deck to order last sent (host) or reported (guest)
        readonly Dictionary<string, string> _orderHost = new();   // guest: deck to order last received from the host
        float _nextOrder;
        public int OrdersApplied;

        static IEnumerable<DraggableItemDeckArea> Decks()
        {
            var all = DraggableItemDeckArea.AllDecks;
            if (all == null) yield break;
            foreach (var d in all)
                if (d != null && d.isActiveAndEnabled && !string.IsNullOrEmpty(d.gameObject.scene.name)) yield return d;
        }

        static List<string> OrderOf(DraggableItemDeckArea d)
        {
            var ids = new List<string>();
            if (d.items == null) return ids;
            foreach (var it in d.items) if (it != null) ids.Add(Util.ItemId.Of(it) ?? it.name);
            return ids;
        }

        static bool AnyDragging(DraggableItemDeckArea d)
        {
            if (d.items == null) return false;
            foreach (var it in d.items) if (it != null && it.IsDragging) return true;
            return false;
        }

        static string Same(IEnumerable<string> ids) { var l = new List<string>(ids); l.Sort(System.StringComparer.Ordinal); return string.Join("\n", l); }

        /// <summary>Decks where this player dropped a card back into the fan (AddBack) since the last report.</summary>
        static readonly HashSet<string> _playerDropped = new();

        void TickOrder()
        {
            if (NoDeckOrder || !Coop.InSession || Time.unscaledTime < _nextOrder || (!Coop.IsHost && !WorldReady.LocalReady)) return;
            _nextOrder = Time.unscaledTime + 0.5f;
            foreach (var d in Decks())
            {
                if (AnyDragging(d)) continue;
                string path = Util.ScenePath.Of(d.transform);
                var order = OrderOf(d);
                string key = string.Join(Sep, order);
                if (Coop.IsHost)
                {
                    if (!_orderSent.TryGetValue(path, out var was) || was != key) { _orderSent[path] = key; SendOrder(d, null); }
                    continue;
                }
                if (!_orderHost.TryGetValue(path, out var host) || host == key) { _playerDropped.Remove(path); continue; }
                if (_playerDropped.Remove(path) && Same(order) == Same(host.Split(Sep)))
                {
                    // this player moved a card inside the fan: ask the host (its answer comes back as the order)
                    _orderHost[path] = key;
                    SendOrder(d, null);
                }
                // laid out differently here (dealt in another order, a card left the fan on the host and the host's
                // fan closed up): take the host's list; a card only this fan still has is let go after a moment
                else ApplyOrder(d, new List<string>(host.Split(Sep)), "the host", exact: true);
            }
        }

        const char Sep = '\n';

        void SendOrder(DraggableItemDeckArea d, byte? slot)
        {
            var ids = OrderOf(d);
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.Str(Util.ScenePath.Of(d.transform));
            w.VarU((ulong)ids.Count);
            foreach (var id in ids) w.Str(id);
            CoopLog.Debug($"[requisition] deck order sent ({ids.Count} cards{(slot is byte to ? " to slot " + to : "")})");
            if (slot is byte one) Coop.Session.Send((ushort)Msg.DeckOrder, w, Net.Transport.Delivery.Reliable, one);
            else if (Coop.IsHost) WorldReady.SendToReady(Msg.DeckOrder, w);
            else Coop.SendToHost(Msg.DeckOrder, w);
        }

        void OnOrder(in MsgContext c, NetReader r)
        {
            if (NoDeckOrder || (int)r.VarU() != MissionFlow.Instance.Epoch || (!Coop.IsHost && !WorldReady.LocalReady)) return;
            if (!Coop.IsHost && !c.FromHost) return;
            string path = r.Str(2048);
            int n = r.Count(256, "cards in the deck order");
            var want = new List<string>();
            for (int i = 0; i < n; i++) want.Add(r.Str(256));
            var d = Util.ScenePath.Find(path)?.GetComponent<DraggableItemDeckArea>();
            if (d == null || d.items == null) { CoopLog.Warn($"[requisition] deck {path} not found here"); return; }
            if (!Coop.IsHost) { _orderHost[path] = string.Join(Sep, want); _nextOrder = 0; return; }   // applied by TickOrder (not mid-drag)
            if (Same(OrderOf(d)) != Same(want) || AnyDragging(d)) return;   // the deck differs from the host's order only by cards the host no longer has, or the host is dragging
            ApplyOrder(d, want, "slot " + c.FromSlot);
        }

        readonly Dictionary<System.IntPtr, float> _extraSince = new();   // guest: card in my fan that the host's fan lacks, since
        readonly Dictionary<System.IntPtr, float> _missingSince = new();   // guest: card in the host's fan that mine lacks, since
        static readonly bool OldDeckRule = LaunchArgs.Mutant("olddeckrule");   // used by the tests to prove the dice test's gather check depends on the new deck rule

        void ApplyOrder(DraggableItemDeckArea d, List<string> want, string from, bool exact = false)
        {
            if (string.Join(Sep, OrderOf(d)) == string.Join(Sep, want)) { if (exact) { _extraSince.Clear(); _missingSince.Clear(); } return; }
            var pool = new List<DraggableItem>();
            foreach (var it in d.items) if (it != null) pool.Add(it);
            var ordered = new List<DraggableItem>();
            foreach (var id in want)
            {
                int k = pool.FindIndex(it => (Util.ItemId.Of(it) ?? it.name) == id);
                if (k >= 0) { ordered.Add(pool[k]); pool.RemoveAt(k); continue; }
                if (!exact) continue;
                // in the host's fan, not in mine: take it in only if it already lies on this deck (not in a hand or
                // a slot), and only once the host has said so for a second (a card this player just took out is
                // still in the host's order until the host hears of the move)
                var card = Util.ItemId.Find<DraggableItem>(id);
                // "in the deck" by the host's word (the item sync set it: the host's reset gathers every card at once
                // and slides them into the fan, so they arrive still lying where they were) or already over the deck
                bool inDeck = card != null && ((!OldDeckRule && card.CurrentLocation == DraggableItem.ItemLocation.Deck) || d.Overlaps(card));
                if (card == null || card.IsDragging || card.CurrentLocation == DraggableItem.ItemLocation.Slot || !inDeck) { _missingSince.Remove(card?.Pointer ?? System.IntPtr.Zero); continue; }
                if (!_missingSince.TryGetValue(card.Pointer, out var gone)) _missingSince[card.Pointer] = gone = Time.unscaledTime;
                if (Time.unscaledTime - gone >= 1f) { ordered.Add(card); _missingSince.Remove(card.Pointer); }
            }
            float now = Time.unscaledTime;
            bool waiting = false;
            foreach (var extra in pool)
            {
                if (!exact) { ordered.Add(extra); continue; }
                if (!_extraSince.TryGetValue(extra.Pointer, out var since)) _extraSince[extra.Pointer] = since = now;
                if (now - since < 1f || extra.IsDragging) { ordered.Add(extra); waiting = true; }   // maybe just dropped here
                else _extraSince.Remove(extra.Pointer);
            }
            if (exact && !waiting) _extraSince.Clear();
            var key = new List<string>(); foreach (var it in ordered) key.Add(Util.ItemId.Of(it) ?? it.name);
            if (string.Join(Sep, key) == string.Join(Sep, OrderOf(d))) return;
            Coop.Apply("deck order", () =>
            {
                d.items.Clear();
                foreach (var it in ordered) d.items.Add(it);
                d.LayoutFan(true);
            });
            OrdersApplied++;
            CoopLog.Debug($"[requisition] deck order from {from} applied ({ordered.Count} cards)");
        }

        [HarmonyPatch(typeof(DraggableItemDeckArea), nameof(DraggableItemDeckArea.AddBack))]
        static class PlayerDrop
        {
            static void Postfix(DraggableItemDeckArea __instance)
            {
                try
                {
                    if (Coop.IsGuest && !Coop.Applying && __instance != null) _playerDropped.Add(Util.ScenePath.Of(__instance.transform));
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Requisition.Postfix line 279", e); }
            }
        }

        /// <summary>
        /// A guest finishing a mission saves "uses left" from the current deck, which is the host's: that would give
        /// the guest the host's counts. Keep the guest's own count instead, minus what the shared mission spent.
        /// </summary>
        [HarmonyPatch(typeof(ProgressionManager), nameof(ProgressionManager.SaveUnlockedCardStates))]
        static class GuestCardUses
        {
            static void Prefix(ProgressionManager __instance, out Dictionary<string, int> __state)
            {
                __state = null;
                try
                {
                    if (!Coop.IsGuest || __instance.UserProgression?.CardStates == null) return;
                    var uses = new Dictionary<string, int>();
                    foreach (var kv in __instance.UserProgression.CardStates) if (kv.Value != null) uses[kv.Key] = kv.Value.RemainingUses;
                    __state = uses;
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Requisition.GuestCardUses.Prefix", e); __state = null; }
            }

            static void Postfix(ProgressionManager __instance, Dictionary<string, int> __state)
            {
                try
                {
                    if (__state == null) return;
                    var states = __instance.UserProgression.CardStates;
                    var defs = Definitions();
                    int changed = 0;
                    foreach (var kv in states)
                    {
                        if (kv.Value == null) continue;
                        int before = __state.TryGetValue(kv.Key, out var b) ? b : defs.TryGetValue(kv.Key, out var d) ? d.MaxUses : kv.Value.RemainingUses;
                        int spent = Spent.TryGetValue(kv.Key, out var sp) ? sp : 0;
                        int mine = before < 0 ? before : System.Math.Max(0, before - spent);
                        if (kv.Value.RemainingUses != mine) { kv.Value.RemainingUses = mine; changed++; }
                    }
                    CoopLog.Info($"[requisition] kept this player's own card uses ({changed} adjusted from the host's deck; spent this mission: {Spent.Count})");
                }
                catch (System.Exception e) { CoopLog.PatchFailed("Requisition.Postfix line 300", e); }
            }
        }

        /// <summary>Guests never redeem a card themselves: the lever press runs on the host.</summary>
        [HarmonyPatch(typeof(RequisitionSlot), nameof(RequisitionSlot.AttemptRequisition))]
        static class Attempt { static bool Prefix() => !GraphGate.Suppress; }
    }
}
