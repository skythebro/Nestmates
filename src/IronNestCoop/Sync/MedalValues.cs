using System.Collections.Generic;
using HarmonyLib;
using IronNestCoop.Core;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;
using IronNestCoop.Util;
using UnityEngine;

namespace IronNestCoop.Sync
{
    /// <summary>
    /// What the end-of-mission screen's medals and kill numbers are worked out from (MedalTrackedValues: every kill,
    /// every shell fired and what it hit, every punchcard used, and named custom values). Mission logic that records
    /// them runs only on the host, so a guest's medals and kill streaks came out empty. The host forwards each record
    /// as it's made; guests record the same and nothing of their own. Times are moved onto the guest's own clock, so
    /// "kills within N seconds" and mission-length medals come out the same.
    /// </summary>
    public sealed class MedalValues : SyncModule
    {
        public override string Name => "medals";
        public static MedalValues Instance { get; private set; }
        static bool Off => LaunchArgs.Mutant("nomedalsync");

        enum Ev : byte { Kill = 1, Shell = 2, Hits = 3, Punchcard = 4, Value = 5, Custom = 6 }

        public int Applied, Sent;

        public MedalValues() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.MedalValues, OnMsg);

        static MedalTrackedValues Values => MissionManager.Instance?.CurrentMissionState?.TrackingValues;

        static NetWriter Begin(Ev ev)
        {
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)ev); w.F32(Time.time);   // the host's clock now
            return w;
        }

        void Send(NetWriter w) { WorldReady.SendToReady(Msg.MedalValues, w); Sent++; }

        // ------------------------------------------------------------------ host: forward each record

        internal void HostKill(MedalTrackedValues.Data_KilledEntity k)
        {
            var w = Begin(Ev.Kill); w.Str(k.Entity?.ID ?? ""); w.F32(k.KilledAtTime); w.Str(k.ShellInstanceId ?? ""); Send(w);
        }

        internal void HostShell(MedalTrackedValues.Data_ShellFired s)
        {
            var w = Begin(Ev.Shell);
            w.Str(s.ShellInstanceId ?? ""); w.Str(s.Shell != null ? s.Shell.ShellId : ""); w.F32(s.ShotAtTime);
            bool has = false; float dist = 0f;
            try { var d = s.DistanceFromNearestTarget; if (d != null && d.HasValue) { has = true; dist = d.Value; } }
            catch (System.Exception) { }   // an IL2CPP nullable field with nothing set can't always be read: treat it as unset
            w.Bool(has); w.F32(dist);
            int n = s.Hits?.Count ?? 0;
            w.VarU((ulong)n);
            for (int i = 0; i < n; i++) w.Str(s.Hits[i]?.ID ?? "");
            Send(w);
        }

        internal void HostHits(string shell, List<string> ids)
        {
            var w = Begin(Ev.Hits); w.Str(shell ?? ""); w.VarU((ulong)ids.Count); foreach (var id in ids) w.Str(id); Send(w);
        }

        internal void HostPunchcard(MedalTrackedValues.Data_PunchcardUsed p)
        {
            var w = Begin(Ev.Punchcard); w.Str(p.Punchcard != null ? p.Punchcard.ID : ""); w.F32(p.UsedAtTime); Send(w);
        }

        internal void HostValue(int id, float v) { var w = Begin(Ev.Value); w.VarI(id); w.F32(v); Send(w); }

        internal void HostCustom(string key, float v) { var w = Begin(Ev.Custom); w.Str(key ?? ""); w.F32(v); Send(w); }

        // ------------------------------------------------------------------ guest: record the same

        static bool _applying;
        static readonly Dictionary<string, PunchcardDefinitionV2> _cards = new();

        static PunchcardDefinitionV2 Card(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            if (_cards.TryGetValue(id, out var c) && c != null) return c;
            foreach (var o in Resources.FindObjectsOfTypeAll<PunchcardDefinitionV2>()) if (o != null && o.ID == id) { _cards[id] = o; return o; }
            return null;
        }

        static MapEntity Unit(string id)
        {
            var fm = FireMission.Instance;
            return fm?.Entities != null && id != null && fm.Entities.ContainsKey(id) ? fm.Entities[id] : null;
        }

        void OnMsg(in MsgContext c, NetReader r)
        {
            if (Off || !c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            var ev = (Ev)r.U8();
            float shift = Time.time - r.F32();   // host time + shift = the same moment on this machine's clock
            var tv = Values;
            if (tv == null) return;
            switch (ev)
            {
                case Ev.Kill:
                {
                    string id = r.Str(256); float at = r.F32(); string shell = r.Str(256);
                    var e = Unit(id);
                    if (e == null) { CoopLog.Debug($"[medals] kill of unknown unit {id}"); return; }
                    Do(() => tv.TrackKill(new MedalTrackedValues.Data_KilledEntity { Entity = e, KilledAtTime = at + shift, ShellInstanceId = shell }));
                    break;
                }
                case Ev.Shell:
                {
                    string inst = r.Str(256), shellId = r.Str(256); float at = r.F32();
                    bool hasD = r.Bool(); float dist = r.F32();
                    int n = r.Count(512, "shell hits");
                    var hits = new Il2CppSystem.Collections.Generic.List<MapEntity>();
                    for (int i = 0; i < n; i++) { var e = Unit(r.Str(256)); if (e != null) hits.Add(e); }
                    var data = new MedalTrackedValues.Data_ShellFired { ShellInstanceId = inst, Shell = MissionSaveManager.FindShell(shellId), Hits = hits, ShotAtTime = at + shift };
                    if (hasD) data.DistanceFromNearestTarget = new Il2CppSystem.Nullable<float>(dist);
                    Do(() => tv.TrackShell(data));
                    break;
                }
                case Ev.Hits:
                {
                    string inst = r.Str(256);
                    int n = r.Count(512, "shell hits");
                    var hits = new Il2CppSystem.Collections.Generic.List<MapEntity>();
                    for (int i = 0; i < n; i++) { var e = Unit(r.Str(256)); if (e != null) hits.Add(e); }
                    Do(() => tv.AddHitsToShell(inst, hits.Cast<Il2CppSystem.Collections.Generic.IEnumerable<MapEntity>>()));
                    break;
                }
                case Ev.Punchcard:
                {
                    string id = r.Str(256); float at = r.F32();
                    var card = Card(id);
                    if (card == null) { CoopLog.Debug($"[medals] punchcard {id} not found here"); return; }
                    Do(() => tv.TrackPunchcard(new MedalTrackedValues.Data_PunchcardUsed { Punchcard = card, UsedAtTime = at + shift }));
                    break;
                }
                case Ev.Value:
                {
                    int id = (int)r.VarI(); float v = r.F32();
                    if (float.IsFinite(v)) Do(() => tv.SetValue((MedalTrackedValue)id, v));
                    break;
                }
                case Ev.Custom:
                {
                    string key = r.Str(256); float v = r.F32();
                    if (float.IsFinite(v)) Do(() => tv.SetCustomValue(key, v));
                    break;
                }
            }
        }

        void Do(System.Action a)
        {
            _applying = true;
            try { Coop.Apply("medal values", a); Applied++; }
            finally { _applying = false; }
        }

        /// <summary>On a guest in a co-op game the records are the host's; its own (from replayed shots) would count twice.</summary>
        static bool GuestOwn => Coop.InSession && Coop.IsGuest && !_applying && !Off;
        static bool HostSends => Coop.InSession && Coop.IsHost && !Off;

        // ------------------------------------------------------------------ patches

        [HarmonyPatch(typeof(MedalTrackedValues), nameof(MedalTrackedValues.TrackKill))]
        static class KillPatch
        {
            static bool Prefix() => !GuestOwn;
            static void Postfix(MedalTrackedValues.Data_KilledEntity value)
            { try { if (HostSends && value != null) Instance?.HostKill(value); } catch (System.Exception e) { CoopLog.PatchFailed("MedalValues.Kill", e); } }
        }

        [HarmonyPatch(typeof(MedalTrackedValues), nameof(MedalTrackedValues.TrackShell))]
        static class ShellPatch
        {
            static bool Prefix() => !GuestOwn;
            static void Postfix(MedalTrackedValues.Data_ShellFired value)
            { try { if (HostSends && value != null) Instance?.HostShell(value); } catch (System.Exception e) { CoopLog.PatchFailed("MedalValues.Shell", e); } }
        }

        [HarmonyPatch(typeof(MedalTrackedValues), nameof(MedalTrackedValues.AddHitsToShell))]
        static class HitsPatch
        {
            static bool Prefix() => !GuestOwn;
            static void Postfix(string shellInstanceId, Il2CppSystem.Collections.Generic.IEnumerable<MapEntity> hits)
            {
                try
                {
                    if (!HostSends || hits == null) return;
                    var ids = new List<string>();
                    var list = new Il2CppSystem.Collections.Generic.List<MapEntity>(hits);
                    for (int i = 0; i < list.Count; i++) if (list[i] != null) ids.Add(list[i].ID);
                    Instance?.HostHits(shellInstanceId, ids);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("MedalValues.Hits", e); }
            }
        }

        [HarmonyPatch(typeof(MedalTrackedValues), nameof(MedalTrackedValues.TrackPunchcard))]
        static class PunchcardPatch
        {
            static bool Prefix() => !GuestOwn;
            static void Postfix(MedalTrackedValues.Data_PunchcardUsed value)
            { try { if (HostSends && value != null) Instance?.HostPunchcard(value); } catch (System.Exception e) { CoopLog.PatchFailed("MedalValues.Punchcard", e); } }
        }

        [HarmonyPatch(typeof(MedalTrackedValues), nameof(MedalTrackedValues.SetValue))]
        static class ValuePatch
        {
            static bool Prefix() => !GuestOwn;
            static void Postfix(MedalTrackedValue valueId, float value)
            { try { if (HostSends) Instance?.HostValue((int)valueId, value); } catch (System.Exception e) { CoopLog.PatchFailed("MedalValues.Value", e); } }
        }

        [HarmonyPatch(typeof(MedalTrackedValues), nameof(MedalTrackedValues.SetCustomValue))]
        static class CustomPatch
        {
            static bool Prefix() => !GuestOwn;
            static void Postfix(string key, float value)
            { try { if (HostSends) Instance?.HostCustom(key, value); } catch (System.Exception e) { CoopLog.PatchFailed("MedalValues.Custom", e); } }
        }
    }
}
