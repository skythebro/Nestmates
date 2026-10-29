using System;
using System.Collections.Generic;
using IronNestCoop.Net.Transport;

namespace IronNestCoop.Steam
{
    /// <summary>
    /// ISteamNetworkingMessages transport (relayed by Steam, no IPs, no port forwarding).
    /// Only lobby members are accepted; session requests are accepted by polling, because the
    /// game's Steamworks.NET dispatcher owns the callback queue.
    /// </summary>
    public sealed unsafe class SteamTransport : ITransport
    {
        const int Channel = 77;
        const int Batch = 64;

        readonly Func<IEnumerable<ulong>> _members;
        readonly HashSet<ulong> _allowed = new();
        float _nextAccept;
        long _dropped;

        public SteamTransport(Func<IEnumerable<ulong>> lobbyMembers)
        {
            _members = lobbyMembers;
            LocalId = SteamApi.LocalId;
        }

        public ulong LocalId { get; }

        public int MaxPayload(Delivery d) => d == Delivery.Reliable ? 512 * 1024 : 1200;

        public bool Send(ulong peer, byte[] data, int offset, int count, Delivery d)
        {
            if (count > MaxPayload(d)) return false;
            var id = SteamApi.IdentityOf(peer);
            int flags = SteamApi.SendNoNagle | SteamApi.SendAutoRestartBrokenSession | (d == Delivery.Reliable ? SteamApi.SendReliable : SteamApi.SendUnreliable);
            fixed (byte* p = data)
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    int r = SteamApi.SteamAPI_ISteamNetworkingMessages_SendMessageToUser(SteamApi.NetMessages, ref id, p + offset, (uint)count, flags, Channel);
                    if (r == 1) return true;              // k_EResultOK (success)
                    if (r != 25 || d != Delivery.Reliable) // 25 = LimitExceeded (buffer full)
                    {
                        CoopLog.Debug($"steam send to {peer} failed: EResult {r}");
                        return false;
                    }
                    System.Threading.Thread.Sleep(5);
                }
            }
            return false;
        }

        public void Poll(List<Packet> received)
        {
            if (UnityEngine.Time.unscaledTime >= _nextAccept)
            {
                _nextAccept = UnityEngine.Time.unscaledTime + 0.25f;
                _allowed.Clear();
                foreach (var m in _members())
                {
                    if (m == LocalId) continue;
                    _allowed.Add(m);
                    var id = SteamApi.IdentityOf(m);
                    SteamApi.SteamAPI_ISteamNetworkingMessages_AcceptSessionWithUser(SteamApi.NetMessages, ref id);
                }
            }
            var msgs = stackalloc IntPtr[Batch];
            while (true)
            {
                int n = SteamApi.SteamAPI_ISteamNetworkingMessages_ReceiveMessagesOnChannel(SteamApi.NetMessages, Channel, msgs, Batch);
                for (int i = 0; i < n; i++)
                {
                    byte* m = (byte*)msgs[i];
                    IntPtr data = *(IntPtr*)m;
                    int size = *(int*)(m + 8);
                    ulong from = SteamApi.SteamAPI_SteamNetworkingIdentity_GetSteamID64((IntPtr)(m + 16));
                    int flags = *(int*)(m + 196);
                    if (size > MaxPayload(Delivery.Reliable)) { if (++_dropped % 100 == 1) CoopLog.Warn($"dropped oversized Steam message ({size} bytes) from {from}"); }
                    else if (from != 0 && _allowed.Contains(from) && size >= 0)
                    {
                        var buf = new byte[size];
                        System.Runtime.InteropServices.Marshal.Copy(data, buf, 0, size);
                        received.Add(new Packet { From = from, Data = buf, Delivery = (flags & SteamApi.SendReliable) != 0 ? Delivery.Reliable : Delivery.Unreliable });
                    }
                    else if (++_dropped % 100 == 1) CoopLog.Warn($"dropped Steam message from non-member {from} ({_dropped} so far)");
                    SteamApi.SteamAPI_SteamNetworkingMessage_t_Release(msgs[i]);
                }
                if (n < Batch) break;
            }
        }

        public void Close(ulong peer)
        {
            var id = SteamApi.IdentityOf(peer);
            SteamApi.SteamAPI_ISteamNetworkingMessages_CloseSessionWithUser(SteamApi.NetMessages, ref id);
        }

        public string Describe() => $"Steam relay (you are {LocalId})";

        public void Dispose()
        {
            foreach (var m in _allowed) Close(m);
        }
    }
}
