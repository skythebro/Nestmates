using System;
using System.Collections.Generic;

namespace IronNestCoop.Net.Transport
{
    public enum Delivery : byte
    {
        /// <summary>May be lost, duplicated or reordered (handlers must be idempotent). Small: see MaxPayload.</summary>
        Unreliable = 0,
        /// <summary>Never lost, delivered in send order per peer.</summary>
        Reliable = 1,
    }

    public struct Packet
    {
        public ulong From;
        public byte[] Data;
        public Delivery Delivery;
        /// <summary>Transport bookkeeping (TCP: the connection's queued-byte count). Not for callers.</summary>
        internal object Conn;
    }

    /// <summary>
    /// Peer-addressed message transport. Implementations: in-memory (unit tests), TCP loopback
    /// (two game copies on one PC), Steam networking messages (real play). The session layer above
    /// does handshake, keep-alive, timeouts and fragmentation, so transports stay dumb.
    /// All calls are made from one thread (the game's main thread).
    /// </summary>
    public interface ITransport : IDisposable
    {
        /// <summary>Stable id of this machine on this transport (SteamID64 on Steam).</summary>
        ulong LocalId { get; }

        /// <summary>Largest payload Send accepts for each delivery mode.</summary>
        int MaxPayload(Delivery d);

        /// <summary>Queue a message. Returns false if the transport refused it (peer gone, buffer full).</summary>
        bool Send(ulong peer, byte[] data, int offset, int count, Delivery d);

        /// <summary>Pump I/O and append everything received since the last call.</summary>
        void Poll(List<Packet> received);

        /// <summary>Forget a peer and release its resources. Safe to call for unknown peers.</summary>
        void Close(ulong peer);

        /// <summary>Short human-readable state for the UI ("listening on 127.0.0.1:27500").</summary>
        string Describe();
    }
}
