using System;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;

namespace IronNestCoop.Core
{
    /// <summary>Choices where "shared" and "each player's own" are both reasonable. The host decides for everyone.</summary>
    public enum Rule : byte
    {
        /// <summary>Fire-mission cards go only to the computer operator (off: everyone gets a copy of their own).</summary>
        CardsToOperator = 0,
        /// <summary>The record player's volume dial is one dial for everyone (off: each player sets how loud they hear it).</summary>
        SharedRecordVolume = 1,
        /// <summary>Hover effects (the firing lever pulling its clamp back) show for everyone, not just the player looking.</summary>
        SharedHover = 2,
        /// <summary>Lines drawn on the tactical map are on everyone's map (off: each player draws on their own).</summary>
        SharedMapDrawings = 3,
        /// <summary>A measurement line's bearing and distance note goes onto every player's notepad, not just the
        /// drawer's (added in 1.0.2, off by default; needs shared map drawings).</summary>
        SharedMeasureNotes = 4,
    }

    /// <summary>
    /// The host's rules, on every machine. The host sends them when someone joins and whenever it changes one;
    /// guests show them read-only (a guest who wants something different asks the host). Until the host's rules
    /// arrive a guest uses the defaults, never its own config, so the two sides can't disagree for long.
    /// </summary>
    public sealed class HostRules : SyncModule
    {
        public override string Name => "rules";
        public static HostRules Instance { get; private set; }

        /// <summary>Raised when a rule changes, either from the host's own toggle or from the host's message on a guest. Passes the old and new bit masks.</summary>
        public static event Action<uint, uint> Changed;

        static uint _remote;
        static bool _haveRemote;
        uint _lastSent = uint.MaxValue, _lastSeen = uint.MaxValue;

        public HostRules() { Instance = this; }

        static uint Bit(Rule r) => 1u << (int)r;

        static uint OwnMask()
        {
            uint m = 0;
            if (Settings.CardsToComputerOnly) m |= Bit(Rule.CardsToOperator);
            if (Settings.SharedRecordVolume) m |= Bit(Rule.SharedRecordVolume);
            if (Settings.SharedHover) m |= Bit(Rule.SharedHover);
            if (Settings.SharedMapDrawings) m |= Bit(Rule.SharedMapDrawings);
            if (Settings.SharedMeasureNotes) m |= Bit(Rule.SharedMeasureNotes);
            m |= (uint)TimerStep(Settings.CounterBatteryTimerPercent) << TimerShift;
            return m;
        }

        static readonly uint Defaults = Bit(Rule.SharedRecordVolume) | Bit(Rule.SharedHover) | Bit(Rule.SharedMapDrawings) | (4u << TimerShift);

        // The counter-battery timer length. Only the host can change it; guests just display it. It uses 4 bits, in quarters of the game's own time.
        const int TimerShift = 8;
        public static readonly int[] TimerChoices = { 0, 50, 100, 200, 300 };
        static int TimerStep(int percent) => Math.Clamp((int)Math.Round(percent / 25.0), 0, 15);
        /// <summary>Snaps a percent to the closest offered choice, so a hand-edited config value of 10 means half rather than off.</summary>
        public static int NearestTimerChoice(int percent)
        {
            if (percent <= 0) return 0;   // only an explicit 0 turns it off
            int best = 100;
            foreach (var c in TimerChoices) if (c > 0 && Math.Abs(c - percent) < Math.Abs(best - percent)) best = c;
            return best;
        }

        /// <summary>Reads the timer percent stored in a rules mask.</summary>
        public static int TimerPercentOf(uint mask) => (int)((mask >> TimerShift) & 15) * 25;

        /// <summary>Counter-battery timer length as a percent of the game's own (0 means off).</summary>
        public static int TimerPercent => (int)(((IgnoreRules ? Defaults : Mask) >> TimerShift) & 15) * 25;

        public static uint Mask => Coop.IsGuest ? (_haveRemote ? _remote : Defaults) : OwnMask();

        static readonly bool IgnoreRules = LaunchArgs.Mutant("norules");   // Test switch: keeps every rule at its default

        public static bool On(Rule r) => ((IgnoreRules ? Defaults : Mask) & Bit(r)) != 0;

        /// <summary>Guests only: true once the host's rules have arrived.</summary>
        public static bool Known => !Coop.IsGuest || _haveRemote;

        public override void Register(Session s) => s.On((ushort)Msg.Settings, OnRules);

        public override void OnSessionStart() { _haveRemote = false; _remote = 0; _lastSent = uint.MaxValue; _lastSeen = Mask; }

        public override void OnSessionEnd() { _haveRemote = false; Notify(); }

        static NetWriter Write(uint mask) { var w = Coop.Writer(); w.VarU(mask); return w; }

        public override void OnPlayerJoined(Player p)
        {
            if (Coop.IsHost && p.Slot != Coop.Session.LocalSlot) Coop.Session.Send((ushort)Msg.Settings, Write(OwnMask()), Delivery.Reliable, p.Slot);
        }

        public override void SendSnapshot(byte slot) => Coop.Session.Send((ushort)Msg.Settings, Write(OwnMask()), Delivery.Reliable, slot);

        public override void Tick(float dt)
        {
            if (Coop.IsHost)
            {
                uint m = OwnMask();
                if (m != _lastSent) { _lastSent = m; Coop.Send(Msg.Settings, Write(m)); }
            }
            Notify();
        }

        void Notify()
        {
            uint now = Mask;
            if (now == _lastSeen) return;
            uint was = _lastSeen;
            _lastSeen = now;
            CoopLog.Info($"[rules] {Describe(now)}");
            try { Changed?.Invoke(was, now); }
            catch (Exception e) { CoopLog.ErrorOnce("rules-changed", "[rules] applying a rule change failed: " + e); }
        }

        void OnRules(in MsgContext c, NetReader r)
        {
            if (!c.FromHost || Coop.IsHost) return;
            _remote = (uint)r.VarU();
            _haveRemote = true;
            Notify();
        }

        public static bool Flipped(uint was, uint now, Rule r) => ((was ^ now) & Bit(r)) != 0;

        public static string Describe(uint m) =>
            $"cards {((m & Bit(Rule.CardsToOperator)) != 0 ? "to the operator" : "a copy each")}, record volume {((m & Bit(Rule.SharedRecordVolume)) != 0 ? "shared" : "own")}, "
            + $"hover {((m & Bit(Rule.SharedHover)) != 0 ? "shared" : "own")}, map drawings {((m & Bit(Rule.SharedMapDrawings)) != 0 ? "shared" : "own")}, "
            + $"measurement notes {((m & Bit(Rule.SharedMeasureNotes)) != 0 ? "to everyone" : "to the drawer")}, "
            + $"counter-battery timer {(((m >> TimerShift) & 15) == 0 ? "off" : (((m >> TimerShift) & 15) * 25) + "%")}";

        public override string Status => Describe(Mask);
    }
}
