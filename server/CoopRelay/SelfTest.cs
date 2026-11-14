using System.Diagnostics;
using OniCoop.Net;

namespace CoopRelay;

/// <summary>End-to-end protocol test: a relay on a random local port and scripted CoopClients (the same transport
/// class the mod uses). No game needed.</summary>
static class SelfTest
{
    sealed class P
    {
        public string Name;
        public CoopClient C;
        public readonly List<ClientEvent> Seen = [];
        public readonly List<ClientEvent> Unread = [];
        public bool Frozen; // owner not polling (game main thread busy loading)
    }

    static readonly List<P> players = [];
    static int port;
    static int failures;
    static readonly System.Collections.Concurrent.ConcurrentQueue<string> relayLog = new();

    static Manifest BaseManifest() => new()
    {
        build = 744825, coopVersion = "0.1.0", dlcs = [],
        mods = [new ModEntry { staticId = "some.mod", title = "Some Mod", version = 3 }],
    };

    static P Join(string name, Role role, Manifest m = null, bool wantWorld = true, string token = "secret", LinkSim sim = null,
                  ushort protocol = Proto.Version)
    {
        var hello = new HelloMsg { protocol = protocol, token = token, room = "test", name = name, role = role, wantWorld = wantWorld, manifest = m ?? BaseManifest() };
        var p = new P { Name = name, C = new CoopClient("127.0.0.1", port, hello, sim, s => relayLog.Enqueue($"[{name}] {s}")) };
        p.C.Start();
        players.Add(p);
        return p;
    }

    static void Pump()
    {
        foreach (P p in players)
        {
            if (p.Frozen) continue;
            while (p.C.TryPoll(out ClientEvent ev)) { p.Seen.Add(ev); p.Unread.Add(ev); }
        }
    }

    static ClientEvent WaitFor(P p, Func<ClientEvent, bool> pred, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Pump();
            int i = p.Unread.FindIndex(e => pred(e));
            if (i >= 0)
            {
                ClientEvent e = p.Unread[i];
                p.Unread.RemoveRange(0, i + 1);
                return e;
            }
            Thread.Sleep(5);
        }
        return null;
    }

    static void PumpFor(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { Pump(); Thread.Sleep(5); }
    }

    static void Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {what}");
        if (!ok) failures++;
    }

    static PresenceData Pres(uint seq, float x) => new()
    {
        seq = seq, world = 0, viewMinX = x - 20, viewMaxX = x + 20, viewMinY = 50, viewMaxY = 80, cursorX = x, cursorY = 60,
        flags = PresenceData.FlagCursor | PresenceData.FlagInGame,
    };

    public static int Run()
    {
        using var cts = new CancellationTokenSource();
        string data = Path.Combine(Path.GetTempPath(), "coop-selftest-" + Environment.ProcessId);
        var relay = new RelayServer(new RelayOptions { Bind = "127.0.0.1", Port = 0, Token = "secret", DataDir = data, IdleTimeoutSec = 3 }, s => relayLog.Enqueue("[relay] " + s));
        relay.Start(cts.Token);
        port = relay.Port;
        Console.WriteLine($"relay on 127.0.0.1:{port}");

        Console.WriteLine("admission");
        var bad = Join("Mallory", Role.Guest, token: "wrong");
        Check(WaitFor(bad, e => e.type == EvType.Rejected)?.reject.code == RejectCode.BadToken, "wrong token is rejected");
        Check(WaitFor(bad, e => e.type == EvType.Closed) != null, "...and the connection is closed");
        var old = Join("Old", Role.Guest, protocol: 999);
        Check(WaitFor(old, e => e.type == EvType.Rejected)?.reject.code == RejectCode.Protocol, "other protocol version is rejected");

        var early = Join("Early", Role.Guest);
        Check(WaitFor(early, e => e.type == EvType.Connected) != null, "guest can join before the host");
        Check(WaitFor(early, e => e.type == EvType.Notice && e.text.Contains("服务器还没有连上")) != null, "...and is told it waits for the server");

        var host = Join("Host", Role.Host, wantWorld: false);
        Check(WaitFor(host, e => e.type == EvType.Connected) != null, "host joins");
        host.C.SendState(new StateMsg { state = MemberState.Lobby });
        Check(WaitFor(early, e => e.type == EvType.Notice && e.text.Contains("读档")) != null, "waiting guest is told the server is not in a game yet");
        var host2 = Join("Host2", Role.Host, wantWorld: false);
        Check(WaitFor(host2, e => e.type == EvType.Rejected)?.reject.code == RejectCode.RoomHasHost, "second host is rejected");

        var m = BaseManifest(); m.build = 700000;
        var oldBuild = Join("OldBuild", Role.Guest, m);
        var rej = WaitFor(oldBuild, e => e.type == EvType.Rejected);
        Check(rej?.reject.code == RejectCode.Incompatible && rej.reject.details.Any(d => d.Contains("游戏版本")), "different game build is rejected with a reason");
        m = BaseManifest(); m.mods.Clear();
        var noMod = Join("NoMod", Role.Guest, m);
        rej = WaitFor(noMod, e => e.type == EvType.Rejected);
        Check(rej?.reject.details.Any(d => d.Contains("Some Mod")) == true, "missing mod is rejected and named");
        m = BaseManifest(); m.mods.Add(new ModEntry { staticId = "extra.ui", title = "Extra UI", version = 1 }); m.mods[0].version = 4;
        var extra = Join("Extra", Role.Guest, m, wantWorld: false);
        var welcome = WaitFor(extra, e => e.type == EvType.Connected);
        Check(welcome != null && welcome.welcome.warnings.Count == 2, "extra mod / other mod version only warn");
        m = BaseManifest(); m.dlcs.Add(Compat.Expansion1);
        var so = Join("SpacedOut", Role.Guest, m);
        Check(WaitFor(so, e => e.type == EvType.Connected) != null, "Spaced-Out guest waits while the host's save is unknown");

        Console.WriteLine("world transfer");
        var save = new byte[3_500_000];
        new Random(1).NextBytes(save);
        host.C.SendState(new StateMsg { state = MemberState.InGame, gameDlcs = [""], colony = "Test Colony" }); // "" = base game, as real saves report it
        rej = WaitFor(so, e => e.type == EvType.Rejected, 3000);
        Check(rej?.reject.details.Any(d => d.Contains("原版")) == true, "...and is rejected once the host loads a base-game save");
        var req = WaitFor(host, e => e.type == EvType.SaveRequest);
        Check(req != null, "host is asked for the world when it enters a game with a guest waiting");
        var late = Join("Late", Role.Guest);
        Check(WaitFor(late, e => e.type == EvType.Connected) != null, "a second guest joins while the save is being made");
        PumpFor(200);
        Check(WaitFor(host, e => e.type == EvType.SaveRequest, 300) == null, "...without a second save request");
        host.C.SendSave(req.requestId, "c.sav", save);
        var got1 = WaitFor(early, e => e.type == EvType.SaveReceived, 10000);
        var got2 = WaitFor(late, e => e.type == EvType.SaveReceived, 10000);
        Check(got1 != null && got1.data.AsSpan().SequenceEqual(save), $"first guest gets the exact save ({save.Length} B)");
        Check(got2 != null && got2.data.AsSpan().SequenceEqual(save), "second guest gets it too");
        Check(File.Exists(Path.Combine(data, "rooms", "test", "latest.sav")), "relay keeps a copy of the latest save");
        early.C.SendState(new StateMsg { state = MemberState.InGame });
        late.C.SendState(new StateMsg { state = MemberState.InGame });
        var members = WaitFor(host, e => e.type == EvType.Members && e.members.Count(x => x.state == MemberState.InGame) == 3);
        Check(members != null, "member list shows everyone in game");

        Console.WriteLine("frozen game");
        late.Frozen = true; // e.g. loading the world: nobody polls the client for a while
        PumpFor(5000);
        late.Frozen = false;
        PumpFor(200);
        Check(!late.Seen.Any(e => e.type == EvType.Closed), "a client whose owner stops polling for 5 s (idle limit 3 s) stays connected");

        Console.WriteLine("presence");
        PumpFor(1500); // UDP registration + pings
        Check(host.C.UdpReady && early.C.UdpReady, "UDP path confirmed on localhost");
        for (uint i = 1; i <= 5; i++) { host.C.SendPresence(Pres(i, 100 + i)); PumpFor(20); }
        var pres = WaitFor(early, e => e.type == EvType.Presence && e.presence.seq == 5);
        Check(pres != null && pres.from == host.C.Id && Math.Abs(pres.presence.cursorX - 105) < 1e-3, "presence goes host → guest with the sender id");
        Check(WaitFor(host, e => e.type == EvType.Presence, 300) == null, "...and not back to the sender");

        var noUdp = Join("NoUdp", Role.Guest, wantWorld: false, sim: new LinkSim { loss = 1.0 });
        Check(WaitFor(noUdp, e => e.type == EvType.Connected) != null, "guest whose UDP is blocked joins");
        noUdp.C.SendState(new StateMsg { state = MemberState.InGame });
        PumpFor(1200);
        Check(!noUdp.C.UdpReady, "...UDP is not confirmed for it");
        host.C.SendPresence(Pres(6, 50));
        Check(WaitFor(noUdp, e => e.type == EvType.Presence && e.presence.seq == 6) != null, "...it still gets presence over TCP");
        noUdp.C.SendPresence(Pres(1, 77));
        Check(WaitFor(host, e => e.type == EvType.Presence && e.from == noUdp.C.Id) != null, "...and its presence reaches the others");
        Check(noUdp.C.RttMs >= 0, "...and it has an RTT (pings over TCP)");

        Console.WriteLine("relay channel");
        early.C.SendRelay(Proto.TargetHost, 3, [1, 2, 3]);
        var rel = WaitFor(host, e => e.type == EvType.RelayFrom);
        Check(rel != null && rel.from == early.C.Id && rel.channel == 3 && rel.data.SequenceEqual(new byte[] { 1, 2, 3 }), "guest → host relay message");
        host.C.SendRelay(Proto.TargetAll, 1, [9]);
        Check(WaitFor(early, e => e.type == EvType.RelayFrom && e.from == host.C.Id) != null && WaitFor(late, e => e.type == EvType.RelayFrom) != null, "host → all relay message");

        Console.WriteLine("simulated 300 ms line");
        var far = Join("Far", Role.Guest, wantWorld: false, sim: new LinkSim { delayMs = 150, jitterMs = 10 });
        var sw = Stopwatch.StartNew();
        Check(WaitFor(far, e => e.type == EvType.Connected) != null, "guest behind a simulated 150 ms (one-way) link joins");
        Console.WriteLine($"        (welcome after {sw.ElapsedMilliseconds} ms)");
        far.C.SendState(new StateMsg { state = MemberState.InGame });
        PumpFor(3500);
        Check(far.C.RttMs is >= 280 and <= 400, $"...measured RTT {far.C.RttMs} ms");

        Console.WriteLine("leaving");
        host.C.Close("test over");
        var after = WaitFor(early, e => e.type == EvType.Members && e.members.All(x => x.role != Role.Host));
        Check(after != null, "guests see the host leave");
        Check(WaitFor(host, e => e.type == EvType.Closed) != null, "host connection closes cleanly");

        foreach (P p in players) p.C.Dispose();
        cts.Cancel();
        try { Directory.Delete(data, true); } catch { }
        if (failures > 0)
        {
            Console.WriteLine("\nrelay / client log:");
            foreach (string l in relayLog) Console.WriteLine("  " + l);
        }
        Console.WriteLine(failures == 0 ? "\nALL PASSED" : $"\n{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }
}
