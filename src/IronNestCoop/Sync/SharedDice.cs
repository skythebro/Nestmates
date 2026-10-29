using System;
using System.Collections.Generic;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Util;
using SleepyNodes;
using UnityEngine;
using Random = UnityEngine.Random;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// Dice that every player must roll the same way. Some things the game shows are placed by a random roll on
    /// each machine (the crater photo of an impact is turned at random, a delivery lands a little off-centre).
    /// Everything else about them already comes from the host, so for the length of the call that rolls, the dice
    /// are seeded from facts every machine shares (where the shell lands, which spawner, which card), then the
    /// game's own random state is put back so nothing else changes. Only in a co-op session.
    ///
    /// Rolls that decide the game (targets, dispersion, malfunctions, valve damage, espresso, teleprinter names)
    /// are not here: those run once on the host and their results are sent. Purely local flavour (lamp flicker,
    /// sway, sounds, menu gauges, each player's own cat) is left to each machine.
    /// </summary>
    public static class SharedDice
    {
        static readonly bool Off = LaunchArgs.Mutant("nodice");   // used by the tests to prove the dice tests depend on seeding

        public static int Seeded;   // rolls made with shared dice (tests, logs)

        static Random.State? Roll(int seed)
        {
            if (Off || !Coop.InSession) return null;
            var was = Random.state;
            Random.InitState(seed);
            Seeded++;
            return was;
        }

        static void Done(Random.State? was) { if (was is Random.State s) Random.state = s; }

        static int Mix(int h, int v) { unchecked { return (h ^ v) * 16777619; } }
        static int Mix(int h, float f) => Mix(h, BitConverter.SingleToInt32Bits(f));
        static int Mix(int h, double d) => Mix(h, BitConverter.DoubleToInt64Bits(d).GetHashCode());
        static int Mix(int h, string s)
        {
            if (s == null) return Mix(h, 0);
            foreach (char c in s) h = Mix(h, (int)c);
            return h;
        }
        static int Start(string what) => Mix(unchecked((int)2166136261), what);
        static int Epoch => MissionFlow.Instance?.Epoch ?? 0;

        /// <summary>How often something has rolled this mission (a spawner's second delivery lands elsewhere).</summary>
        static readonly Dictionary<string, int> _counts = new();
        static int _countsEpoch = -1;
        static int Next(string key)
        {
            if (_countsEpoch != Epoch) { _counts.Clear(); _countsEpoch = Epoch; }
            _counts.TryGetValue(key, out int n);
            _counts[key] = n + 1;
            return n;
        }

        // ------------------------------------------------------------------ impacts

        /// <summary>A shell lands: its crater photo is turned at random when it appears (RandomUIRotation).
        /// Target and start time come from the host's shot.</summary>
        [HarmonyPatch(typeof(ShellVisual), "Update")]
        static class ShellImpact
        {
            static readonly bool OffHere = LaunchArgs.Mutant("impactdice");
            static void Prefix(ShellVisual __instance, out Random.State? __state)
            {
                __state = null;
                if (OffHere || __instance == null) return;
                var t = __instance.targetLocalPos;
                __state = Roll(Mix(Mix(Mix(Start("shell"), t.x), t.y), __instance.startedAt));
            }
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        /// <summary>A scripted (enemy or mission) impact: the same photo; place and shell id come from the host.</summary>
        [HarmonyPatch(typeof(State_TriggerImpact), "SpawnImpactPrefab")]
        static class ScriptedImpact
        {
            static void Prefix(Vector2 impactLocation, string shellInstanceId, out Random.State? __state)
                => __state = Roll(Mix(Mix(Mix(Start("scripted"), impactLocation.x), impactLocation.y), shellInstanceId));
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        /// <summary>The correction arrow of a miss is turned off true by a random error (smaller with better
        /// correction tiers): the same error for everyone, per impact.</summary>
        [HarmonyPatch(typeof(ImpactVisualCorrections), "ApplyArrowRotationWithTiers")]
        static class CorrectionArrow
        {
            static void Prefix(ImpactVisualCorrections __instance, out Random.State? __state)
            {
                __state = null;
                if (__instance == null) return;
                var p = __instance._impactLocalPos;
                __state = Roll(Mix(Mix(Start("arrow"), p.x), p.y));
            }
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        /// <summary>The counter-battery barrage around the turret when the timer runs out: when and where each
        /// enemy shell falls.</summary>
        [HarmonyPatch(typeof(CounterBatteryCinematicImpactSpawner), "ScheduleNext")]
        static class BarrageSchedule
        {
            static void Prefix(out Random.State? __state) => __state = Roll(Mix(Start("barrage-when"), Next("barrage-when")));
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        [HarmonyPatch(typeof(CounterBatteryCinematicImpactSpawner), "SpawnOne")]
        static class BarrageSpawn
        {
            static void Prefix(out Random.State? __state) => __state = Roll(Mix(Start("barrage-where"), Next("barrage-where")));
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        // ------------------------------------------------------------------ things that arrive

        /// <summary>Deliveries through the ceiling hatch (record disks, crates) land a little off-centre. The host's
        /// spawner fires, guests replay it (WorldEvents): same spawner, same count, same spot.</summary>
        [HarmonyPatch(typeof(PrefabSpawner), nameof(PrefabSpawner.Spawn))]
        static class Delivery
        {
            static void Prefix(PrefabSpawner __instance, out Random.State? __state)
            {
                __state = null;
                if (__instance == null || Off || !Coop.InSession) return;
                string key = "spawn " + ScenePath.Of(__instance.transform);
                __state = Roll(Mix(Mix(Start(key), Epoch), Next(key)));
            }
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        /// <summary>A card or item slid onto a surface (dealt punchcards, a reset deck) gets a random spread: per
        /// item and surface, the same spread on every machine.</summary>
        [HarmonyPatch(typeof(DraggableItem), nameof(DraggableItem.MoveToSurface),
            new[] { typeof(DragSurface), typeof(bool), typeof(bool), typeof(DraggableItem.EjectAxis), typeof(float), typeof(float), typeof(float), typeof(float) })]
        static class SlideOntoSurface
        {
            static void Prefix(DraggableItem __instance, DragSurface overrideSurface, out Random.State? __state)
            {
                __state = null;
                if (__instance == null || Off || !Coop.InSession) return;
                string id = ItemId.Of(__instance);
                string surface = overrideSurface != null ? ScenePath.Of(overrideSurface.transform) : "-";
                __state = Roll(Mix(Mix(Mix(Start("slide"), id), surface), Epoch));
            }
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        /// <summary>The scout plane's flyover (and the ambient one) is placed and turned at random when enabled; the
        /// plane itself is placed from the host's message first.</summary>
        [HarmonyPatch(typeof(MinimalTransformRandomizer), "OnEnable")]
        static class Flyover
        {
            static void Prefix(MinimalTransformRandomizer __instance, out Random.State? __state)
            {
                __state = null;
                if (__instance == null || Off || !Coop.InSession) return;
                var t = __instance.transform;
                var at = t.parent != null ? t.parent.position : t.position;
                __state = Roll(Mix(Mix(Mix(Mix(Start("flyover"), ScenePath.Of(t)), Mathf.Round(at.x * 100)), Mathf.Round(at.y * 100)), Mathf.Round(at.z * 100)));
            }
            static Exception Finalizer(Exception __exception, Random.State? __state) { Done(__state); return __exception; }   // even if the game throws
        }

        // ------------------------------------------------------------------ the newspaper

        /// <summary>Which articles fill the newspaper (and their order) comes from a seed picked when it opens:
        /// everyone reads the same paper for the same mission.</summary>
        [HarmonyPatch(typeof(ArticleSystem.ArticleController), "ConfigureRngOnEnable")]
        static class Newspaper
        {
            static void Postfix(ArticleSystem.ArticleController __instance)
            {
                if (__instance == null || Off || !Coop.InSession) return;
                __instance.SetSeedInternal(Mix(Start("news"), Epoch));
                Seeded++;
            }
        }
    }
}
