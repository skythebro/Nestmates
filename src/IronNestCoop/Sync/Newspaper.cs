using System.Collections.Generic;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;
using ArticleSystem;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// The newspaper between missions. Its front page is an article the mission queues when it ends (the game's
    /// ArticlePoolQueueManager); mission logic runs only on the host, so a guest's queue never got it and the guest
    /// read a generic issue with another date. The host sends every article it queues (and a clear of the queue);
    /// guests queue the same and don't queue anything of their own while in a co-op game. A joining guest gets the
    /// host's whole queue. Which articles fill the rest of the page already comes from a shared seed (SharedDice).
    /// </summary>
    public sealed class Newspaper : SyncModule
    {
        public override string Name => "newspaper";
        public static Newspaper Instance { get; private set; }
        static bool Off => LaunchArgs.Mutant("nonewsqueue");

        enum Ev : byte { Pool = 1, Article = 2, Clear = 3, Snapshot = 4 }

        public int Applied, Sent;

        public Newspaper() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Newspaper, OnMsg);

        static ArticlePoolQueueManager Manager => ArticlePoolQueueManager.s_instance;

        // ------------------------------------------------------------------ finding the same asset on every machine

        static readonly Dictionary<string, ArticlePoolDefinition> _pools = new();
        static readonly Dictionary<string, GameObject> _articles = new();

        static ArticlePoolDefinition FindPool(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_pools.TryGetValue(name, out var p) && p != null) return p;
            foreach (var o in Resources.FindObjectsOfTypeAll<ArticlePoolDefinition>())
                if (o != null && o.name == name) { _pools[name] = o; return o; }
            return null;
        }

        static GameObject FindArticle(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_articles.TryGetValue(name, out var g) && g != null) return g;
            foreach (var o in Resources.FindObjectsOfTypeAll<GameObject>())
                if (o != null && o.name == name && string.IsNullOrEmpty(o.scene.name)) { _articles[name] = o; return o; }   // the prefab, not a copy on a page
            return null;
        }

        // ------------------------------------------------------------------ host: what it queues

        internal void HostQueued(bool isPool, string name, int count, string note)
        {
            var w = Coop.Writer(); w.U8((byte)(isPool ? Ev.Pool : Ev.Article)); w.Str(name); w.VarI(count); w.Str(note ?? "");
            Coop.Send(Msg.Newspaper, w);
            Sent++;
            CoopLog.Debug($"[newspaper] host queued {(isPool ? "pool" : "article")} {name} x{count}");
        }

        internal void HostCleared()
        {
            var w = Coop.Writer(); w.U8((byte)Ev.Clear); Coop.Send(Msg.Newspaper, w);
        }

        public override void SendSnapshot(byte slot)
        {
            var m = Manager;
            if (Off || m == null || m.queue == null) return;
            var w = Coop.Writer(); w.U8((byte)Ev.Snapshot);
            w.VarU((ulong)m.queue.Count);
            foreach (var e in m.queue)
            {
                w.Bool(e.isPool);
                w.Str(e.isPool ? (e.pool != null ? e.pool.name : "") : (e.prefab != null ? e.prefab.name : ""));
                w.VarI(e.remainingUses); w.Str(e.note ?? "");
            }
            Coop.Session.Send((ushort)Msg.Newspaper, w, Delivery.Reliable, slot);
        }

        // ------------------------------------------------------------------ guest: queue the same

        static bool _applying;

        void OnMsg(in MsgContext c, NetReader r)
        {
            if (Off || !c.FromHost || Coop.IsHost) return;
            var m = Manager;
            var ev = (Ev)r.U8();
            if (m == null) { CoopLog.Warn("[newspaper] no article queue here yet"); return; }
            if (ev == Ev.Clear) { _waiting.Clear(); Apply(() => m.ClearQueue()); return; }
            if (ev == Ev.Snapshot)
            {
                int n = r.Count(256, "queued articles");
                var list = new List<(bool, string, int, string)>();
                for (int i = 0; i < n; i++) list.Add((r.Bool(), r.Str(256), (int)r.VarI(), r.Str(512)));
                _waiting.Clear();
                Apply(() => { m.ClearQueue(); foreach (var e in list) Queue(m, e.Item1, e.Item2, e.Item3, e.Item4); });
                CoopLog.Info($"[newspaper] took the host's article queue ({n} entries)");
                return;
            }
            string name = r.Str(256); int count = (int)r.VarI(); string note = r.Str(512);
            if (count < 1 || count > 100) return;
            Apply(() => Queue(m, ev == Ev.Pool, name, count, note));
        }

        // An article the host queued at mission start can arrive before this guest has loaded it. It waits here and
        // is queued as soon as it can be found (for up to a minute), in the host's order.
        readonly List<(bool isPool, string name, int count, string note, float until)> _waiting = new();
        float _nextRetry;

        public override void OnSessionStart() => _waiting.Clear();

        public override void Tick(float dt)
        {
            if (_waiting.Count == 0 || Time.unscaledTime < _nextRetry || Coop.IsHost) return;
            _nextRetry = Time.unscaledTime + 2f;
            var m = Manager;
            if (m == null) return;
            var todo = new List<(bool, string, int, string, float)>(_waiting);
            _waiting.Clear();
            foreach (var e in todo)
            {
                if (Time.unscaledTime > e.Item5) { CoopLog.Warn($"[newspaper] gave up on {e.Item2}: not found here"); continue; }
                Apply(() => Queue(m, e.Item1, e.Item2, e.Item3, e.Item4, e.Item5));
            }
        }

        void Queue(ArticlePoolQueueManager m, bool isPool, string name, int count, string note, float until = -1f)
        {
            if (_waiting.Count > 0 && until < 0) { _waiting.Add((isPool, name, count, note, Time.unscaledTime + 60f)); return; }   // keep the host's order
            if (isPool)
            {
                var p = FindPool(name);
                if (p == null) { _waiting.Add((isPool, name, count, note, until < 0 ? Time.unscaledTime + 60f : until)); CoopLog.Debug($"[newspaper] pool {name} not loaded yet; waiting"); return; }
                m.EnqueuePool(p, count, note);
            }
            else
            {
                var g = FindArticle(name);
                if (g == null) { _waiting.Add((isPool, name, count, note, until < 0 ? Time.unscaledTime + 60f : until)); CoopLog.Debug($"[newspaper] article {name} not loaded yet; waiting"); return; }
                m.EnqueueSpecificArticle(g, count, note);
            }
            Applied++;
            CoopLog.Debug($"[newspaper] queued the host's {(isPool ? "pool" : "article")} {name} x{count}");
        }

        static void Apply(System.Action a)
        {
            _applying = true;
            try { Coop.Apply("newspaper queue", a); }
            finally { _applying = false; }
        }

        /// <summary>Guests don't queue articles of their own in a co-op game: their queue is the host's.</summary>
        static bool GuestOwnQueue => Coop.InSession && Coop.IsGuest && !_applying && !Off;

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(ArticlePoolQueueManager), nameof(ArticlePoolQueueManager.EnqueuePool))]
        static class PoolPatch
        {
            static bool Prefix() => !GuestOwnQueue;
            static void Postfix(ArticlePoolDefinition pool, int count, string note)
            {
                try { if (Coop.InSession && Coop.IsHost && !Off && pool != null) Instance?.HostQueued(true, pool.name, count, note); }
                catch (System.Exception e) { CoopLog.PatchFailed("Newspaper.PoolPatch", e); }
            }
        }

        [HarmonyPatch(typeof(ArticlePoolQueueManager), nameof(ArticlePoolQueueManager.EnqueueSpecificArticle))]
        static class ArticlePatch
        {
            static bool Prefix() => !GuestOwnQueue;
            static void Postfix(GameObject articlePrefab, int count, string note)
            {
                try { if (Coop.InSession && Coop.IsHost && !Off && articlePrefab != null) Instance?.HostQueued(false, articlePrefab.name, count, note); }
                catch (System.Exception e) { CoopLog.PatchFailed("Newspaper.ArticlePatch", e); }
            }
        }

        [HarmonyPatch(typeof(ArticlePoolQueueManager), nameof(ArticlePoolQueueManager.ClearQueue))]
        static class ClearPatch
        {
            static bool Prefix() => !GuestOwnQueue;
            static void Postfix()
            {
                try { if (Coop.InSession && Coop.IsHost && !Off) Instance?.HostCleared(); }
                catch (System.Exception e) { CoopLog.PatchFailed("Newspaper.ClearPatch", e); }
            }
        }
    }
}
