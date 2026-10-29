using System;
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
    /// Paint on shells. A shell in the magazine can be painted before it is loaded (PaintableMesh: every brush dab
    /// is a PaintAtUV call on the painter's machine only). Dabs are collected per shell and sent 20 times a
    /// second with the brush that made them; the others make the same dabs with the same brush. Paint made before
    /// someone joined is not sent to them.
    /// </summary>
    public sealed class Paint : SyncModule
    {
        public override string Name => "paint";
        public static Paint Instance { get; private set; }

        sealed class Batch { public PaintableMesh Mesh; public Color Color; public float Size, Sx, Sy, Soft; public readonly List<Vector2> Uvs = new(); }

        readonly Dictionary<IntPtr, Batch> _pending = new();
        float _nextSend;
        public int DabsSent, DabsApplied;

        public Paint() { Instance = this; }

        public override void Register(Session s) => s.On((ushort)Msg.Paint, OnPaint);
        public override void OnSessionStart() { _pending.Clear(); DabsSent = DabsApplied = 0; }

        internal void LocalDab(PaintableMesh m, Vector2 uv)
        {
            if (!Coop.InSession || Coop.Applying) return;
            if (!_pending.TryGetValue(m.Pointer, out var b))
                _pending[m.Pointer] = b = new Batch { Mesh = m, Color = m.BrushColor, Size = m.BrushSize, Sx = m.BrushScaleX, Sy = m.BrushScaleY, Soft = m.BrushSoftness };
            if (b.Uvs.Count < 4000) b.Uvs.Add(uv);
        }

        public override void Tick(float dt)
        {
            if (_pending.Count == 0 || Time.unscaledTime < _nextSend) return;
            _nextSend = Time.unscaledTime + 0.05f;
            foreach (var b in _pending.Values)
            {
                if (b.Mesh == null || b.Uvs.Count == 0) continue;
                var w = Write(ScenePath.Of(b.Mesh.transform), b, Coop.Session.LocalSlot);
                if (Coop.IsHost) WorldReady.SendToReady(Msg.Paint, w); else Coop.SendToHost(Msg.Paint, w);
                DabsSent += b.Uvs.Count;
            }
            _pending.Clear();
        }

        // A dab's UV is not always inside 0..1 (a brush at the edge of a shell's texture), so the wire format keeps
        // -2..3. Clamping to 0..1 put every such dab on the texture's edge (a straight line down the right side of
        // the painter's own shell, when the host passed their paint back to them).
        const float UvMin = -2f, UvSpan = 5f;
        static ushort PackUv(float v) => (ushort)Mathf.RoundToInt(Mathf.Clamp01((v - UvMin) / UvSpan) * 65535f);
        static float UnpackUv(ushort v) => v / 65535f * UvSpan + UvMin;

        static NetWriter Write(string path, Batch b, byte painter)
        {
            var w = Coop.Writer();
            w.VarU((ulong)MissionFlow.Instance.Epoch);
            w.U8(painter);   // who painted it: the host passes a guest's paint on to everyone but them
            w.Str(path);
            w.F32(b.Color.r); w.F32(b.Color.g); w.F32(b.Color.b); w.F32(b.Color.a);
            w.F32(b.Size); w.F32(b.Sx); w.F32(b.Sy); w.F32(b.Soft);
            w.VarU((ulong)b.Uvs.Count);
            foreach (var uv in b.Uvs) { w.U16(PackUv(uv.x)); w.U16(PackUv(uv.y)); }
            return w;
        }

        readonly System.Collections.Generic.Dictionary<byte, (float At, int Used)> _budget = new();

        void OnPaint(in MsgContext c, NetReader r)
        {
            if (c.FromSlot == Coop.Session.LocalSlot || (int)r.VarU() != MissionFlow.Instance.Epoch) return;
            if (!(WorldReady.LocalReady || Coop.IsHost)) return;
            byte painter = r.U8();
            if (!c.FromHost) painter = c.FromSlot;          // a guest speaks only for itself
            if (painter == Coop.Session.LocalSlot) return;   // my own paint, passed back: already on my shell
            string path = r.Str(2048);
            var b = new Batch { Color = new Color(r.F32(), r.F32(), r.F32(), r.F32()), Size = r.F32(), Sx = r.F32(), Sy = r.F32(), Soft = r.F32() };
            int n = r.Count(4000, "dabs");
            for (int i = 0; i < n; i++) b.Uvs.Add(new Vector2(UnpackUv(r.U16()), UnpackUv(r.U16())));
            // a brush from the wire is kept to what a real brush can be: a huge or broken one would stall painting everywhere
            static float Keep(float v, float lo, float hi, float dflt) => float.IsFinite(v) ? Mathf.Clamp(v, lo, hi) : dflt;
            b.Color = new Color(Keep(b.Color.r, 0, 1, 1), Keep(b.Color.g, 0, 1, 1), Keep(b.Color.b, 0, 1, 1), Keep(b.Color.a, 0, 1, 1));
            b.Size = Keep(b.Size, 0.001f, 0.5f, 0.05f); b.Sx = Keep(b.Sx, 0.05f, 20f, 1f); b.Sy = Keep(b.Sy, 0.05f, 20f, 1f); b.Soft = Keep(b.Soft, 0f, 1f, 0.5f);
            // at most a few thousand dabs a second from one player (a hand on a brush makes far fewer)
            float now = Time.unscaledTime;
            // per painter (the host relays everyone's paint from its own slot), and each message costs as much as
            // 20 dabs: many tiny messages are as much work as a few big ones
            if (!_budget.TryGetValue(painter, out var bud) || now - bud.At >= 1f) bud = (now, 0);
            if (bud.Used + n + 20 > 6000) { CoopLog.WarnOnce("paint-rate" + painter, $"[paint] player {painter + 1} sends more paint than a brush can make: dropped"); return; }
            _budget[painter] = (bud.At, bud.Used + n + 20);
            if (Coop.IsHost)   // pass a guest's paint on, to everyone but the painter
            {
                var fwd = Write(path, b, painter);
                foreach (var slot in WorldReady.Slots) if (slot != painter) Coop.Session.Send((ushort)Msg.Paint, fwd, Delivery.Reliable, slot);
            }
            var m = ScenePath.Find(path)?.GetComponent<PaintableMesh>();
            if (m == null) { CoopLog.Debug($"[paint] no paintable shell at {path}"); return; }
            // their brush for their dabs, then ours back
            var keep = (m.BrushColor, m.BrushSize, m.BrushScaleX, m.BrushScaleY, m.BrushSoftness);
            m.BrushColor = b.Color; m.BrushSize = b.Size; m.BrushScaleX = b.Sx; m.BrushScaleY = b.Sy; m.BrushSoftness = b.Soft;
            try { Coop.Apply("paint " + path, () => { foreach (var uv in b.Uvs) m.PaintAtUV(uv); }); }
            finally { (m.BrushColor, m.BrushSize, m.BrushScaleX, m.BrushScaleY, m.BrushSoftness) = keep; }
            DabsApplied += b.Uvs.Count;
        }

        [HarmonyPatch(typeof(PaintableMesh), nameof(PaintableMesh.PaintAtUV))]
        static class Dab
        {
            static void Postfix(PaintableMesh __instance, Vector2 uv) { try { Instance?.LocalDab(__instance, uv); } catch (Exception e) { CoopLog.ErrorOnce("paint-dab", "[paint] " + e.Message); } }
        }

        public override string Status => $"sent {DabsSent} dabs, applied {DabsApplied}";
    }
}
