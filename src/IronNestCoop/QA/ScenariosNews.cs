using System.Collections.Generic;
using System.Linq;
using ArticleSystem;
using IronNestCoop.Core;
using IronNestCoop.Sync;
using UnityEngine;

namespace IronNestCoop.QA
{
    public static partial class Scenarios
    {
        static string QueueKey()
        {
            var m = ArticlePoolQueueManager.s_instance;
            if (m == null || m.queue == null) return "(no queue)";
            return string.Join(",", m.queue.ToArray().Select(e => (e.isPool ? "P:" + e.pool?.name : "A:" + e.prefab?.name) + "x" + e.remainingUses));
        }

        /// <summary>
        /// The newspaper's article queue. The host queues a specific article and a pool, the way a mission does when it
        /// ends; the guest's queue must become the same, so its next newspaper has the mission's front page (twin:
        /// --host-extra=-coop-mutant=nonewsqueue).
        /// </summary>
        static IEnumerable<object> NewsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            var m = ArticlePoolQueueManager.s_instance;
            var pool = Resources.FindObjectsOfTypeAll<ArticlePoolDefinition>().FirstOrDefault(p => p != null && p.articlePrefabs != null && p.articlePrefabs.Count > 0);
            bot.Check(m != null && pool != null, $"the article queue and an article pool exist ({pool?.name})");
            if (m != null && pool != null)
            {
                m.EnqueueSpecificArticle(pool.articlePrefabs[0], 1, "qa front page");
                m.EnqueuePool(pool, 2, "qa pool");
                foreach (var x in bot.Wait(1)) yield return x;
                bot.Note("host queue: " + QueueKey());
                bot.Signal("queue", QueueKey());
            }
            foreach (var x in bot.Until(() => bot.HasSignal("guestdone"), 60, "guest checked")) yield return x;
            bot.Signal("done");
        }

        static IEnumerable<object> NewsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "in mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("queue"), 60, "host queued articles")) yield return x;
            string want = bot.SignalValue("queue");
            foreach (var x in bot.Until(() => QueueKey() == want, 5, null, soft: true)) yield return x;
            bot.Check(QueueKey() == want, $"the guest's newspaper queue is the host's (guest {QueueKey()}, host {want})");
            bot.Signal("guestdone");
            foreach (var x in bot.Wait(2)) yield return x;
        }
    }
}
