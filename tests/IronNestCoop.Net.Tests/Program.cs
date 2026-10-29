using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using IronNestCoop.Net.Session;
using IronNestCoop.Net.Transport;
using IronNestCoop.Net.Wire;

namespace IronNestCoop.Net.Tests
{
    [AttributeUsage(AttributeTargets.Method)] sealed class TestAttribute : Attribute { }

    sealed class AssertFailed : Exception { public AssertFailed(string m) : base(m) { } }

    static class A
    {
        public static void True(bool c, string what) { if (!c) throw new AssertFailed(what); }
        public static void Eq<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new AssertFailed($"{what}: expected {expected}, got {actual}");
        }
        public static void Throws<TE>(Action a, string what) where TE : Exception
        {
            try { a(); } catch (TE) { return; }
            throw new AssertFailed($"{what}: expected {typeof(TE).Name}");
        }
    }

    /// <summary>Host + guests on one in-memory network with a fake clock.</summary>
    sealed class Rig
    {
        public readonly MemoryHub Hub;
        public readonly List<Session.Session> Sessions = new();
        public readonly List<string> Log = new();
        public Rig(int seed = 1) { Hub = new MemoryHub(seed); }

        public Session.Session Make(ulong id, string ver = "test")
        {
            var s = new Session.Session(Hub.Create(id), ver, () => Hub.Now);
            s.Log += m => Log.Add($"[{id}] {m}");
            Sessions.Add(s);
            return s;
        }

        public void Run(double seconds, double step = 0.016)
        {
            for (double t = 0; t < seconds; t += step)
            {
                Hub.Now += step;
                foreach (var s in Sessions) s.Tick();
            }
        }

        public Session.Session Host(int guests, out Session.Session[] g)
        {
            var h = Make(1); h.Host("Host", new byte[] { 1 });
            g = new Session.Session[guests];
            for (int i = 0; i < guests; i++) { g[i] = Make((ulong)(100 + i)); g[i].Join(1, "G" + i, new byte[] { (byte)(10 + i) }); }
            Run(1.5);
            return h;
        }
    }

    static class Program
    {
        static int Main(string[] args)
        {
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                .Where(m => args.Length == 0 || args.Any(a => m.Name.Contains(a, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name).ToList();
            int pass = 0, fail = 0;
            foreach (var t in tests)
            {
                try { t.Invoke(null, null); pass++; Console.WriteLine($"PASS {t.DeclaringType.Name}.{t.Name}"); }
                catch (TargetInvocationException e) { fail++; Console.WriteLine($"FAIL {t.DeclaringType.Name}.{t.Name}: {e.InnerException?.Message}"); }
            }
            if (tests.Count == 0) { Console.WriteLine("RESULT: FAIL (no tests ran)"); return 2; }
            Console.WriteLine($"RESULT: {(fail == 0 ? "PASS" : "FAIL")} {pass}/{tests.Count}");
            return fail == 0 ? 0 : 1;
        }
    }

    static class WireTests
    {
        [Test]
        static void RoundTripsEveryType()
        {
            var w = new NetWriter(4);
            w.U8(7); w.Bool(true); w.U16(65000); w.I32(-5); w.U32(4000000000); w.U64(ulong.MaxValue); w.F32(1.5f); w.F64(-2.25);
            w.VarU(0); w.VarU(127); w.VarU(128); w.VarU(ulong.MaxValue); w.VarI(-1); w.VarI(long.MinValue);
            w.Str(null); w.Str(""); w.Str("Grüße 砲塔"); w.Bytes(null); w.Bytes(new byte[] { 1, 2, 3 });
            var r = new NetReader(w.ToArray());
            A.Eq((byte)7, r.U8(), "u8"); A.True(r.Bool(), "bool"); A.Eq((ushort)65000, r.U16(), "u16"); A.Eq(-5, r.I32(), "i32");
            A.Eq(4000000000u, r.U32(), "u32"); A.Eq(ulong.MaxValue, r.U64(), "u64"); A.Eq(1.5f, r.F32(), "f32"); A.Eq(-2.25, r.F64(), "f64");
            A.Eq(0ul, r.VarU(), "v0"); A.Eq(127ul, r.VarU(), "v127"); A.Eq(128ul, r.VarU(), "v128"); A.Eq(ulong.MaxValue, r.VarU(), "vmax");
            A.Eq(-1L, r.VarI(), "vi-1"); A.Eq(long.MinValue, r.VarI(), "vimin");
            A.Eq(null, r.Str(), "null str"); A.Eq("", r.Str(), "empty str"); A.Eq("Grüße 砲塔", r.Str(), "utf8");
            A.Eq(null, r.Bytes(), "null bytes"); A.Eq("1,2,3", string.Join(",", r.Bytes()), "bytes");
            A.Eq(0, r.Remaining, "consumed all");
        }

        [Test]
        static void TruncatedInputThrowsMalformed()
        {
            var w = new NetWriter(); w.Str("hello world"); w.U64(5);
            var full = w.ToArray();
            for (int cut = 0; cut < full.Length; cut++)
            {
                var r = new NetReader(full, 0, cut);
                A.Throws<MalformedMessageException>(() => { r.Str(); r.U64(); }, $"cut at {cut}");
            }
        }

        [Test]
        static void CountsAreCappedAndNeverNegative()
        {
            var w = new NetWriter(); w.VarU(ulong.MaxValue); w.VarU(4001); w.VarU(3);
            var r = new NetReader(w.ToArray());
            A.Throws<MalformedMessageException>(() => r.Count(4000, "dabs"), "a count that a cast to int would make negative");
            A.Throws<MalformedMessageException>(() => r.Count(4000, "dabs"), "a count over the cap");
            A.Eq(3, r.Count(4000, "dabs"), "a normal count");
        }

        [Test]
        static void HugeLengthPrefixIsRejectedWithoutAllocating()
        {
            var w = new NetWriter(); w.VarU(ulong.MaxValue);
            A.Throws<MalformedMessageException>(() => new NetReader(w.ToArray()).Bytes(), "huge blob");
            A.Throws<MalformedMessageException>(() => new NetReader(w.ToArray()).Str(), "huge string");
        }
    }

    static class FragmentTests
    {
        [Test]
        static void SplitAndReassembleInterleaved()
        {
            var rng = new Random(3);
            var f = new Fragmenter();
            var a = new byte[100_000]; rng.NextBytes(a);
            var b = new byte[7_001]; rng.NextBytes(b);
            var pa = f.Split(a, 1200).ToList(); var pb = f.Split(b, 1200).ToList();
            var rx = new Fragmenter();
            byte[] gotA = null, gotB = null;
            int i = 0, j = 0;
            while (i < pa.Count || j < pb.Count)
            {
                if (i < pa.Count) { var x = rx.Accept(9, new NetReader(pa[i], 1, pa[i].Length - 1)); i++; if (x != null) gotA = x; }
                if (j < pb.Count) { var x = rx.Accept(9, new NetReader(pb[j], 1, pb[j].Length - 1)); j++; if (x != null) gotB = x; }
            }
            A.True(gotA != null && gotA.SequenceEqual(a), "a intact");
            A.True(gotB != null && gotB.SequenceEqual(b), "b intact");
        }

        [Test]
        static void BadFragmentHeaderIsMalformed()
        {
            var w = new NetWriter(); w.U32(1); w.U16(5); w.U16(3); // index >= count
            A.Throws<MalformedMessageException>(() => new Fragmenter().Accept(1, new NetReader(w.ToArray())), "index >= count");
        }
    }

    static class SessionTests
    {
        const ushort Chat = 100;

        static NetWriter Msg(string s) { var w = new NetWriter(); w.Str(s); return w; }

        [Test]
        static void HostAndTwoGuestsSeeSameRoster()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            A.Eq(3, h.PlayerCount, "host count");
            foreach (var s in g)
            {
                A.True(s.IsConnected, "guest connected");
                A.Eq(3, s.PlayerCount, "guest count");
                A.Eq("Host", s.GetPlayer(0).Name, "host name on guest");
            }
            A.Eq((byte)1, g[0].LocalSlot, "slot g0");
            A.Eq((byte)2, g[1].LocalSlot, "slot g1");
            A.Eq("G1", g[0].GetPlayer(2).Name, "g0 sees g1");
            A.Eq((byte)11, g[0].GetPlayer(2).Info[0], "player info relayed");
        }

        [Test]
        static void GuestBroadcastIsRelayedWithOrigin()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            var got = new List<string>();
            void Hook(Session.Session s, string who) => s.On(Chat, (in MsgContext c, NetReader r) => got.Add($"{who}<-{c.FromSlot}:{r.Str()}"));
            Hook(h, "h"); Hook(g[0], "g0"); Hook(g[1], "g1");
            g[0].Send(Chat, Msg("hi"), Delivery.Reliable);
            rig.Run(0.2);
            got.Sort();
            A.Eq("g1<-1:hi|h<-1:hi", string.Join("|", got), "delivered to host and other guest only");
        }

        [Test]
        static void TargetedSendReachesOnlyTarget()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            var got = new List<string>();
            h.On(Chat, (in MsgContext c, NetReader r) => got.Add("h"));
            g[0].On(Chat, (in MsgContext c, NetReader r) => got.Add("g0"));
            g[1].On(Chat, (in MsgContext c, NetReader r) => got.Add("g1"));
            g[0].Send(Chat, Msg("x"), Delivery.Reliable, target: 2);
            h.Send(Chat, Msg("y"), Delivery.Reliable, target: 1);
            g[1].SendToHost(Chat, Msg("z"), Delivery.Reliable);
            rig.Run(0.2);
            got.Sort();
            A.Eq("g0|g1|h", string.Join("|", got), "each target once");
        }

        [Test]
        static void FloodFromOneGuestIsNotRelayed()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            int atHost = 0, atOther = 0;
            h.On(Chat, (in MsgContext c, NetReader r) => atHost++);
            g[1].On(Chat, (in MsgContext c, NetReader r) => atOther++);
            for (int i = 0; i < 5000; i++) g[0].Send(Chat, Msg("x"), Delivery.Unreliable);
            rig.Run(0.05, 0.05);
            A.True(atOther > 0 && atOther <= 2000, $"at most 2000 a second are passed on ({atOther})");
            A.True(atHost <= 2000, $"and handled by the host ({atHost})");
            A.True(rig.Log.Exists(l => l.Contains("more than play can make")), "the host says so in its log");
            rig.Run(1.1, 0.05);
            int before = atOther;
            g[0].Send(Chat, Msg("later"), Delivery.Reliable);
            rig.Run(0.2);
            A.True(atOther == before + 1, "a second later the guest is heard again");
        }

        [Test]
        static void VersionMismatchIsRefusedWithReason()
        {
            var rig = new Rig();
            var h = rig.Make(1, "1.0.0"); h.Host("Host", null);
            var g = rig.Make(2, "0.9.0"); g.Join(1, "G", null);
            rig.Run(1.0);
            A.Eq(SessionState.Closed, g.State, "guest closed");
            A.True(g.CloseReason.Contains("version mismatch") && g.CloseReason.Contains("1.0.0"), "reason names both versions: " + g.CloseReason);
            A.Eq(1, h.PlayerCount, "host alone");
        }

        [Test]
        static void SixthPlayerIsRefused()
        {
            var rig = new Rig();
            var h = rig.Host(5, out var g);
            A.Eq(5, h.PlayerCount, "full lobby");
            A.Eq(SessionState.Closed, g[4].State, "fifth guest refused");
            A.True(g[4].CloseReason.Contains("full"), "full reason: " + g[4].CloseReason);
        }

        [Test]
        static void SilentGuestTimesOutAndOthersAreTold()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            string left = null;
            g[1].PlayerLeft += (p, why) => left = p.Name;
            rig.Hub.Partition(100, true);
            rig.Run(Protocol.TimeoutSec + 2);
            A.Eq(2, h.PlayerCount, "host dropped silent guest");
            A.Eq(SessionState.Closed, g[0].State, "cut-off guest noticed");
            A.Eq("G0", left, "other guest told");
            A.Eq(2, g[1].PlayerCount, "other guest roster");
        }

        [Test]
        static void ExplicitLeaveIsImmediate()
        {
            var rig = new Rig();
            var h = rig.Host(1, out var g);
            g[0].Leave("bye");
            rig.Run(0.1);
            A.Eq(1, h.PlayerCount, "host sees leave quickly");
            h.Leave("host quit");
            var g2 = rig.Make(200); // new guest after host left -> must time out, not hang
            g2.Join(1, "Late", null);
            rig.Run(Protocol.JoinTimeoutSec + 1);
            A.Eq(SessionState.Closed, g2.State, "join to dead host times out");
        }

        [Test]
        static void KickWithBlockPreventsRejoin()
        {
            var rig = new Rig();
            var h = rig.Host(1, out var g);
            h.Kick(1, "kicked by host", blockRejoin: true);
            rig.Run(0.2);
            A.Eq(SessionState.Closed, g[0].State, "kicked guest closed");
            var again = rig.Make(100); again.Join(1, "G0", null);
            rig.Run(1.0);
            A.Eq(SessionState.Closed, again.State, "rejoin refused");
            A.True(again.CloseReason.Contains("removed"), again.CloseReason);
        }

        [Test]
        static void LargeSnapshotArrivesIntact()
        {
            var rig = new Rig();
            var h = rig.Host(1, out var g);
            var blob = new byte[3 * 1024 * 1024]; new Random(5).NextBytes(blob);
            byte[] got = null;
            g[0].On(Chat, (in MsgContext c, NetReader r) => got = r.Bytes());
            var w = new NetWriter(); w.Bytes(blob);
            h.Send(Chat, w, Delivery.Reliable);
            rig.Run(0.2);
            A.True(got != null && got.SequenceEqual(blob), "3 MB blob intact");
        }

        [Test]
        static void OversizedUnreliableIsPromotedNotDropped()
        {
            var rig = new Rig();
            var h = rig.Host(1, out var g);
            int got = 0;
            g[0].On(Chat, (in MsgContext c, NetReader r) => got = r.Bytes().Length);
            var w = new NetWriter(); w.Bytes(new byte[5000]);
            h.Send(Chat, w, Delivery.Unreliable);
            rig.Run(0.2);
            A.Eq(5000, got, "promoted to reliable fragments");
        }

        [Test]
        static void HostClockEstimateIsAccurateUnderLatency()
        {
            var rig = new Rig();
            rig.Hub.LatencyMs = 80; rig.Hub.JitterMs = 10;
            var h = rig.Make(1); h.Host("Host", null);
            rig.Run(3.0); // host clock runs ahead before the guest exists
            var g = rig.Make(2); g.Join(1, "G", null);
            rig.Run(5.0);
            double err = Math.Abs(g.HostTime - h.LocalTime);
            A.True(err < 0.02, $"host time error {err * 1000:F1} ms");
            A.True(g.GetPlayer(0).RttMs > 150 && g.GetPlayer(0).RttMs < 215, $"rtt {g.GetPlayer(0).RttMs}");
        }

        [Test]
        static void ReliableOrderHoldsUnderLossAndJitter()
        {
            var rig = new Rig(9);
            var h = rig.Host(1, out var g);
            rig.Hub.LatencyMs = 60; rig.Hub.JitterMs = 50; rig.Hub.LossRate = 0.3; rig.Hub.DuplicateRate = 0.1;
            var seen = new List<int>();
            g[0].On(Chat, (in MsgContext c, NetReader r) => seen.Add(r.I32()));
            for (int i = 0; i < 200; i++) { var w = new NetWriter(); w.I32(i); h.Send(Chat, w, Delivery.Reliable); rig.Run(0.016); }
            rig.Run(1.0);
            A.Eq(200, seen.Count, "all reliable delivered");
            for (int i = 0; i < 200; i++) A.Eq(i, seen[i], "in order");
            A.True(g[0].IsConnected, "still connected despite 30% loss");
        }

        [Test]
        static void GarbageNeverCrashesAndIsCounted()
        {
            var rig = new Rig();
            var h = rig.Host(1, out var g);
            var rng = new Random(11);
            var evil = rig.Hub.Create(666);
            var member = (MemoryTransport)g[0].Transport;
            for (int i = 0; i < 2000; i++)
            {
                var b = new byte[rng.Next(0, 64)]; rng.NextBytes(b);
                bool fromMember = rng.Next(2) == 0;
                if (b.Length > 0 && rng.Next(3) == 0) b[0] = (byte)rng.Next(1, 11); // valid kinds, bad bodies
                // A Bye from the real guest is a legitimate leave, not garbage.
                if (fromMember && b.Length > 0 && b[0] == (byte)Protocol.Kind.Bye) b[0] = 0;
                (fromMember ? member : evil).Send(1, b, 0, b.Length, Delivery.Unreliable);
                if (i % 50 == 0) rig.Run(0.02);
            }
            rig.Run(0.5);
            A.True(h.IsConnected, "host survives");
            A.True(h.MalformedDropped > 0, "malformed counted");
            A.Eq(2, h.PlayerCount, "stranger did not join by accident");
        }

        [Test]
        static void GuestCannotSpoofOrigin()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            int got = 0;
            g[1].On(Chat, (in MsgContext c, NetReader r) => got++);
            // Data packet from guest 1 claiming to come from the host (origin 0).
            var pkt = new byte[] { (byte)Protocol.Kind.Data, Chat & 0xFF, Chat >> 8, 0, Protocol.TargetAll, 0 };
            ((MemoryTransport)g[0].Transport).Send(1, pkt, 0, pkt.Length, Delivery.Reliable);
            rig.Run(0.2);
            A.Eq(0, got, "spoofed packet not relayed");
            A.True(h.MalformedDropped >= 1, "spoof counted");
        }

        [Test]
        static void PlayerInfoUpdateReachesEveryone()
        {
            var rig = new Rig();
            var h = rig.Host(2, out var g);
            g[0].SetLocalInfo("Renamed", new byte[] { 42 });
            rig.Run(0.2);
            A.Eq("Renamed", h.GetPlayer(1).Name, "host sees rename");
            A.Eq((byte)42, g[1].GetPlayer(1).Info[0], "other guest sees info");
        }
    }
    /// <summary>The direct connection over real loopback sockets: join key, caps, refused ids.</summary>
    static class TcpTests
    {
        static List<Packet> Wait(TcpTransport host, double seconds)
        {
            var got = new List<Packet>();
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until && got.Count == 0) { host.Poll(got); System.Threading.Thread.Sleep(20); }
            return got;
        }

        static bool Delivers(TcpTransport host, ulong id, string key)
        {
            TcpTransport g;
            try { g = TcpTransport.Connect(id, 1, "127.0.0.1", host.Port, 3000, key); }
            catch { return false; }
            using (g)
            {
                System.Threading.Thread.Sleep(100);
                g.Send(1, new byte[] { 7 }, 0, 1, Delivery.Reliable);
                return Wait(host, 2.5).Any(p => p.From == id && p.Data.Length == 1 && p.Data[0] == 7);
            }
        }

        [Test]
        static void RightKeyIsAdmitted()
        {
            using var host = TcpTransport.Listen(1, 0, System.Net.IPAddress.Loopback, "K7Q2PX");
            A.True(Delivers(host, 50, "K7Q2PX"), "a guest with the key reaches the host");
        }

        [Test]
        static void WrongOrMissingKeyIsRefused()
        {
            using var host = TcpTransport.Listen(1, 0, System.Net.IPAddress.Loopback, "K7Q2PX");
            A.True(!Delivers(host, 51, "WRONG1"), "a wrong key is refused");
            A.True(!Delivers(host, 52, ""), "no key is refused");
            A.True(Delivers(host, 53, "K7Q2PX"), "the right key still works after wrong ones");
        }

        [Test]
        static void HostIdAndZeroAreRefused()
        {
            using var host = TcpTransport.Listen(1, 0);
            A.True(!Delivers(host, 1, ""), "a guest claiming the host's id is refused");
            A.True(!Delivers(host, 0, ""), "id 0 is refused");
            A.True(Delivers(host, 54, ""), "a normal id is admitted");
        }

        [Test]
        static void ConnectionFloodIsCappedAndClosed()
        {
            using var host = TcpTransport.Listen(1, 0);
            var socks = new List<System.Net.Sockets.TcpClient>();
            for (int i = 0; i < 24; i++) { var c = new System.Net.Sockets.TcpClient(); c.Connect("127.0.0.1", host.Port); socks.Add(c); }
            System.Threading.Thread.Sleep(500);
            int closed = 0;
            foreach (var c in socks)
            {
                try
                {
                    c.Client.ReceiveTimeout = 200;
                    var buf = new byte[1];
                    if (c.Client.Poll(200_000, System.Net.Sockets.SelectMode.SelectRead) && c.Client.Receive(buf) == 0) closed++;
                }
                catch { closed++; }
            }
            A.True(closed >= 24 - TcpTransport.MaxGreeting, $"connections over the greeting cap are closed at once ({closed} of 24 closed)");
            foreach (var c in socks) c.Dispose();
            System.Threading.Thread.Sleep(5500);   // the silent ones time out of their greeting
            A.True(Delivers(host, 55, ""), "after the flood a guest can join again");
        }

        static bool ClosedWithin(System.Net.Sockets.TcpClient c, int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try
                {
                    if (c.Client.Poll(100_000, System.Net.Sockets.SelectMode.SelectRead) && c.Client.Receive(new byte[1]) == 0) return true;
                }
                catch { return true; }
            }
            return false;
        }

        [Test]
        static void TrickledGreetingIsCutAtOneDeadline()
        {
            using var host = TcpTransport.Listen(1, 0);
            var c = new System.Net.Sockets.TcpClient(); c.Connect("127.0.0.1", host.Port);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool closed = false;
            // one byte a second: the old per-read timeout (5 s) never fired
            for (int i = 0; i < 8 && !closed; i++)
            {
                try { c.GetStream().Write(new byte[] { 7 }, 0, 1); } catch { closed = true; break; }
                closed = ClosedWithin(c, 1000);
            }
            A.True(closed && sw.ElapsedMilliseconds < 7000, $"a greeting trickled in byte by byte is cut after 5 s ({sw.ElapsedMilliseconds} ms, closed {closed})");
            c.Dispose();
        }

        [Test]
        static void OneAddressHoldsAtMostTwoGreetings()
        {
            using var host = TcpTransport.Listen(1, 0);
            var silent = new List<System.Net.Sockets.TcpClient>();
            for (int i = 0; i < TcpTransport.MaxGreetingPerAddress; i++) { var s = new System.Net.Sockets.TcpClient(); s.Connect("127.0.0.1", host.Port); silent.Add(s); }
            System.Threading.Thread.Sleep(200);
            var third = new System.Net.Sockets.TcpClient(); third.Connect("127.0.0.1", host.Port);
            A.True(ClosedWithin(third, 1000), "a third greeting from the same address is closed at once");
            third.Dispose();
            foreach (var s in silent) A.True(ClosedWithin(s, 6500), "the silent ones time out of their greeting");
            foreach (var s in silent) s.Dispose();
            System.Threading.Thread.Sleep(200);
            A.True(Delivers(host, 58, ""), "then a guest from that address joins");
        }

        [Test]
        static void TooLongKeyIsRejectedByTheGuest()
        {
            using var host = TcpTransport.Listen(1, 0);
            A.Throws<ArgumentException>(() => TcpTransport.Connect(56, 1, "127.0.0.1", host.Port, 2000, new string('x', 100)), "a key over 64 bytes");
        }
    }
}
