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

        static ArticlePoolDefinition SeqPool() => Resources.FindObjectsOfTypeAll<ArticlePoolDefinition>()
            .Where(p => p != null && p.selection == ArticlePoolDefinition.SelectionMode.Sequential && p.articlePrefabs != null && p.articlePrefabs.Count > 2)
            .OrderBy(p => p.name).FirstOrDefault();

        static int SeqIndex(ArticlePoolDefinition p)
        {
            var d = ArticlePoolQueueManager.s_instance?._sequentialNextIndex;
            return d != null && p != null && d.ContainsKey(p) ? d[p] : 0;
        }

        static void SetSeqIndex(ArticlePoolDefinition p, int i)
        {
            var m = ArticlePoolQueueManager.s_instance;
            if (m == null || p == null) return;
            if (m._sequentialNextIndex == null) m._sequentialNextIndex = new Il2CppSystem.Collections.Generic.Dictionary<ArticlePoolDefinition, int>();
            m._sequentialNextIndex[p] = i;
        }

        /// <summary>The articles actually printed on the newspaper's columns, in order.</summary>
        static string Printed(ArticleController c)
        {
            var names = new List<string>();
            foreach (var col in c.GetComponentsInChildren<ArticleColumn>(true))
                for (int i = 0; i < col.transform.childCount; i++)
                {
                    var ch = col.transform.GetChild(i);
                    if (ch.gameObject.activeSelf) names.Add(ch.name.Replace("(Clone)", "").Trim());
                }
            return string.Join("|", names);
        }

        static ArticleController Paper() => Resources.FindObjectsOfTypeAll<ArticleController>()
            .FirstOrDefault(c => c != null && !string.IsNullOrEmpty(c.gameObject.scene.name));

        /// <summary>
        /// The newspaper's article queue. The host queues a specific article and a pool, the way a mission does when it
        /// ends; the guest's queue must become the same, so its next newspaper has the mission's front page (twin:
        /// --host-extra=-coop-mutant=nonewsqueue).
        /// </summary>
        static IEnumerable<object> NewsHost(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsHost && Coop.Session.PlayerCount == 2, 90, "guest joined")) yield return x;
            foreach (var x in StartMission(bot, "Artillery Introduction")) yield return x;
            // the host's own place in a sequential pool (its play history), before the guest has loaded the mission
            var seq = SeqPool();
            if (seq != null) SetSeqIndex(seq, 2);
            bot.Note($"sequential pool {seq?.name ?? "none"}: host at {SeqIndex(seq)}");
            foreach (var x in bot.Until(() => WorldReady.Slots.Contains((byte)1), 120, "guest mission loaded")) yield return x;
            foreach (var x in bot.Wait(3)) yield return x;
            if (seq != null) bot.Signal("seq", seq.name + "^" + SeqIndex(seq));
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
            // print the paper on both machines from the same queue and seed, and compare what's on it
            var paper = Paper();
            if (paper == null) bot.Note("no newspaper in this scene: printed paper not compared");
            else
            {
                bot.Signal("print");
                paper.SetSeedInternal(424242); paper.PopulateNow();
                foreach (var x in bot.Wait(4)) yield return x;
                bot.Note("host paper: " + Printed(paper));
                bot.Signal("printed", Printed(paper));
                foreach (var x in bot.Until(() => bot.HasSignal("guestprinted"), 30, "guest printed")) yield return x;
            }
            bot.Signal("done");
        }

        static IEnumerable<object> NewsGuest(Bot bot)
        {
            foreach (var x in bot.Until(() => Coop.IsGuest, 90, "connected")) yield return x;
            var seq = SeqPool();
            if (seq != null) SetSeqIndex(seq, 1);   // this guest's own play history put it elsewhere in the pool
            foreach (var x in bot.Until(() => WorldReady.LocalReady, 180, "in mission")) yield return x;
            foreach (var x in bot.Until(() => bot.HasSignal("queue"), 60, "host queued articles")) yield return x;
            if (bot.HasSignal("seq"))
            {
                var sv = bot.SignalValue("seq").Split('^');
                bot.Check(seq != null && seq.name == sv[0] && SeqIndex(seq) == int.Parse(sv[1]),
                    $"the guest is at the host's place in sequential pool {sv[0]} (guest {SeqIndex(seq)}, host {sv[1]})");
            }
            string want = bot.SignalValue("queue");
            foreach (var x in bot.Until(() => QueueKey() == want, 5, null, soft: true)) yield return x;
            bot.Check(QueueKey() == want, $"the guest's newspaper queue is the host's (guest {QueueKey()}, host {want})");
            bot.Signal("guestdone");
            foreach (var x in bot.Until(() => bot.HasSignal("print") || bot.HasSignal("done"), 30, "host printing")) yield return x;
            var paper = Paper();
            if (bot.HasSignal("print") && paper != null)
            {
                paper.SetSeedInternal(424242); paper.PopulateNow();
                foreach (var x in bot.Wait(4)) yield return x;
                foreach (var x in bot.Until(() => bot.HasSignal("printed"), 30, "host printed")) yield return x;
                string want2 = bot.SignalValue("printed"), mine = Printed(paper);
                if (want2.Length == 0 && mine.Length == 0) bot.Note("the newspaper can't print in this scene (inactive during a mission): printed paper not compared");
                else bot.Check(mine == want2, $"the guest's printed newspaper is the host's (guest {mine}, host {want2})");
                bot.Signal("guestprinted");
            }
            foreach (var x in bot.Until(() => bot.HasSignal("done"), 30, "host done")) yield return x;
        }
    }
}
