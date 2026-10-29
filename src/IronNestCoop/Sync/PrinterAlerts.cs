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
    /// The alert lights of the teleprinters (Field Reports, High Command). They light when a message arrives
    /// (the alarm itself is sent by WorldEvents) and go out when a player walks up to that printer
    /// (PrinterAlertDismisser's trigger): that walk happened on one machine only, so each player's lights went out
    /// at different times. The host's lights are the source of truth: their state is sent whenever it changes (and to
    /// late joiners), and a guest who walks up to a printer asks the host to dismiss it for everyone.
    /// </summary>
    public sealed class PrinterAlerts : SyncModule
    {
        public override string Name => "printeralerts";
        public static PrinterAlerts Instance { get; private set; }
        static readonly bool Off = LaunchArgs.Mutant("noprinteralerts");   // used by the tests to prove the events test depends on this feature

        enum Ev : byte { State = 1, Dismiss = 2 }

        readonly Dictionary<string, string> _sent = new();   // host: the state last sent for each printer path
        readonly Dictionary<byte, float> _lastDismiss = new();
        float _next;
        public int Applied, Dismissals;

        public PrinterAlerts() { Instance = this; }

        public override void Register(Session s)
        {
            s.On((ushort)Msg.PrinterAlerts, OnMsg);
            s.On((ushort)Msg.TeleprinterScroll, OnScroll);
        }
        public override void OnSessionStart() { _sent.Clear(); _lastDismiss.Clear(); _scrollOff.Clear(); _scrollers.Clear(); }

        // ------------------------------------------------------------------ teleprinter paper scroll

        // The paper is a slider, and other players see a scroll as a replayed drag. A guest that was paused while
        // the host scrolled missed that replay and kept its paper where it was, with the newest lines hidden under
        // the typewriter head. Twice a second the host sends how far each paper is scrolled; a guest whose paper
        // is still off a second later (so not a replay in flight) puts it there with the game's own restore call.

        static readonly List<TeleprinterScroller> _scrollers = new();
        static float _nextScrollerScan, _nextScrollSend;
        readonly Dictionary<string, int> _scrollOff = new();   // guest: how many host updates in a row a paper was off
        public int ScrollsFixed;

        internal static List<TeleprinterScroller> Scrollers()
        {
            if (Time.unscaledTime >= _nextScrollerScan || _scrollers.Exists(x => x == null))
            {
                _nextScrollerScan = Time.unscaledTime + 5f;
                _scrollers.Clear();
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<TeleprinterScroller>()))
                {
                    var t = o.TryCast<TeleprinterScroller>();
                    if (t != null && !string.IsNullOrEmpty(t.gameObject.scene.name)) _scrollers.Add(t);
                }
            }
            return _scrollers;
        }

        void SendScrolls()
        {
            if (Time.unscaledTime < _nextScrollSend || LaunchArgs.Mutant("noscrollsync")) return;
            _nextScrollSend = Time.unscaledTime + 0.5f;
            var list = Scrollers();
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.VarU((ulong)list.Count);
            foreach (var t in list) { w.Str(ScenePath.Of(t.transform)); w.F32(t.scrollT); }
            WorldReady.SendToReady(Msg.TeleprinterScroll, w, Delivery.Unreliable);
        }

        void OnScroll(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost || (int)r.VarU() != MissionFlow.Instance.Epoch || !WorldReady.LocalReady) return;
            int n = r.Count(16, "teleprinters");
            for (int i = 0; i < n; i++)
            {
                string path = r.Str(2048); float t = r.F32();
                if (!float.IsFinite(t)) continue;
                TeleprinterScroller s = null;
                foreach (var x in Scrollers()) if (x != null && ScenePath.Of(x.transform) == path) { s = x; break; }
                if (s == null) continue;
                var slider = s.sliderInteractable;
                bool busy = slider != null && (slider.IsDragging || Controls.Replayed.Contains(slider.Pointer));
                if (busy || Mathf.Abs(s.scrollT - t) < 0.02f) { _scrollOff[path] = 0; continue; }
                int off = _scrollOff.TryGetValue(path, out var k) ? k + 1 : 1;
                _scrollOff[path] = off;
                if (off < 2) continue;   // off for a whole second: not a scroll still arriving
                _scrollOff[path] = 0;
                ScrollsFixed++;
                CoopLog.Info($"[printeralerts] {s.name}: paper at {s.scrollT:F2}, host at {t:F2} -> put where the host has it");
                Coop.Apply("teleprinter scroll", () => s.RestoreScroll(t));
            }
        }

        static readonly List<PrinterAlertSystem> _all = new();
        static float _nextScan;

        /// <summary>The alert systems in the turret (a scene search at most every 5 s, not every check).</summary>
        static IEnumerable<PrinterAlertSystem> All()
        {
            if (Time.unscaledTime >= _nextScan || _all.Exists(a => a == null))
            {
                _nextScan = Time.unscaledTime + 5f;
                _all.Clear();
                foreach (var o in Object.FindObjectsOfType(Il2CppType.Of<PrinterAlertSystem>()))
                {
                    var a = o.TryCast<PrinterAlertSystem>();
                    if (a != null && !string.IsNullOrEmpty(a.gameObject.scene.name)) _all.Add(a);
                }
            }
            return _all;
        }

        static string StateOf(PrinterAlertSystem a)
        {
            var d = a.CaptureMissionState();
            return d == null ? "" : GameJson.Write(d);
        }

        /// <summary>Which lights are on, without how far their glow curve has run (that changes every frame).</summary>
        internal static string KeyOf(PrinterAlertSystem a)
        {
            var d = a.CaptureMissionState();
            if (d == null) return "";
            static int M(PrinterAlertLightSaveData l) => l == null ? -1 : l.Mode;
            return $"{d.ResidentTier}/{d.OverrideTier}/{M(d.SuccessLight)}/{M(d.LowPriorityLight)}/{M(d.HighPriorityLight)}";
        }

        public override void Tick(float dt)
        {
            if (Coop.IsHost && Coop.InSession) SendScrolls();
            if (Off || !Coop.IsHost || Time.unscaledTime < _next || MissionFlow.Phase != MissionManager.GamePhase.MissionActive) return;
            _next = Time.unscaledTime + 0.25f;
            foreach (var a in All())
            {
                try
                {
                    if (a == null) continue;
                    string path = ScenePath.Of(a.transform), key = KeyOf(a);
                    if (_sent.TryGetValue(path, out var was) && was == key) continue;
                    _sent[path] = key;
                    Send(path, StateOf(a), null);
                }
                catch (System.Exception e) { CoopLog.ErrorOnce("printeralerts-tick", "[printeralerts] " + e.Message); }   // one broken printer must not stop the rest
            }
        }

        public override void SendSnapshot(byte slot)
        {
            if (Off) return;
            foreach (var a in All()) Send(ScenePath.Of(a.transform), StateOf(a), slot);
        }

        static void Send(string path, string state, byte? slot)
        {
            var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)Ev.State); w.Str(path); w.Str(state);
            if (slot is byte one) Coop.Session.Send((ushort)Msg.PrinterAlerts, w, Delivery.Reliable, one);
            else WorldReady.SendToReady(Msg.PrinterAlerts, w);
        }

        void OnMsg(in MsgContext c, NetReader r)
        {
            if (Off || (int)r.VarU() != MissionFlow.Instance.Epoch || !(WorldReady.LocalReady || Coop.IsHost)) return;
            var ev = (Ev)r.U8();
            string path = r.Str(2048);
            var a = ScenePath.Find(path)?.GetComponent<PrinterAlertSystem>();
            if (a == null) { CoopLog.Warn($"[printeralerts] {path} not found here"); return; }
            if (ev == Ev.Dismiss)
            {
                if (!Coop.IsHost || !WorldReady.IsReady(c.FromSlot)) return;
                // a player walks up to a printer a few times a minute at most
                if (!_lastDismiss.TryGetValue(c.FromSlot, out var at) || Time.unscaledTime - at >= 0.5f)
                {
                    _lastDismiss[c.FromSlot] = Time.unscaledTime;
                    Dismissals++;
                    CoopLog.Debug($"[printeralerts] slot {c.FromSlot} walked up to {a.name}: dismissed for everyone");
                    Coop.Apply("printer alerts dismissed", () => a.DismissAllAlerts());   // the next tick sends the new state to all
                }
                Send(path, StateOf(a), c.FromSlot);   // and that player gets the host's lights back at once, whatever happened
                return;
            }
            if (!c.FromHost || Coop.IsHost) return;
            string state = r.Str(65536);
            var data = GameJson.Read<PrinterAlertSystemSaveData>(state);
            if (data == null) return;
            static int M(PrinterAlertLightSaveData l) => l == null ? -1 : l.Mode;
            if (KeyOf(a) == $"{data.ResidentTier}/{data.OverrideTier}/{M(data.SuccessLight)}/{M(data.LowPriorityLight)}/{M(data.HighPriorityLight)}") return;
            Applied++;
            Coop.Apply("printer alerts", () => a.RestoreMissionState(data));
            CoopLog.Debug($"[printeralerts] {a.name} now as on the host");
        }

        /// <summary>A guest walked up to a printer: its own lights go out at once; ask the host to do it for all.</summary>
        [HarmonyPatch(typeof(PrinterAlertSystem), nameof(PrinterAlertSystem.DismissAllAlerts))]
        static class Dismiss
        {
            static void Postfix(PrinterAlertSystem __instance)
            {
                try
                {
                    if (Off || !Coop.IsGuest || Coop.Applying || __instance == null) return;
                    var w = Coop.Writer(); w.VarU((ulong)MissionFlow.Instance.Epoch); w.U8((byte)Ev.Dismiss); w.Str(ScenePath.Of(__instance.transform));
                    Coop.SendToHost(Msg.PrinterAlerts, w);
                }
                catch (System.Exception e) { CoopLog.PatchFailed("PrinterAlerts.Postfix line 139", e); }
            }
        }
    }
}
