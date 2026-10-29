namespace IronNestCoop.Net.Session
{
    /// <summary>Wire constants shared by every build. Bump <see cref="Version"/> on any incompatible change.</summary>
    public static class Protocol
    {
        public const ushort Version = 1;
        public const int MaxPlayers = 5;

        /// <summary>First byte of every transport packet.</summary>
        public enum Kind : byte
        {
            Hello = 1,     // guest -> host: proto, modVersion, name, playerInfo
            Welcome = 2,   // host -> guest: slot, session nonce
            Reject = 3,    // host -> guest: reason
            Bye = 4,       // either: reason
            Ping = 5,      // either: sender time
            Pong = 6,      // either: echoed time, responder time
            Roster = 7,    // host -> all: player list
            Data = 8,      // game message: u16 type, u8 origin slot, u8 target, body
            Fragment = 9,  // piece of a large Data packet
            PlayerInfo = 10, // guest -> host: updated player info blob (name, cat, ...)
        }

        public const byte TargetAll = 0xFF;
        public const byte HostSlot = 0;
        /// <summary>Largest per-player info blob (cat look, colours, names).</summary>
        public const int MaxInfoBytes = 4096;

        public const double HelloIntervalSec = 1.0;
        public const double PingIntervalSec = 1.0;
        public const double TimeoutSec = 15.0;
        public const double JoinTimeoutSec = 20.0;
    }
}
