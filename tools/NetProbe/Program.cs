using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

// UDP link probe for the ONI multiplayer design: measures RTT, jitter, and per-packet loss in both
// directions at a game-like packet rate/size, and writes a per-packet trace SyncBench can replay.
//
//   netprobe server --port 47800 --token SECRET
//   netprobe client --host 1.2.3.4 --port 47800 --token SECRET [--rate 10] [--size 1100] [--duration 600] [--out trace.csv]
//
// The server only streams after the client proves it owns its source address (HELLO -> CHALLENGE cookie
// -> START), knows the shared token, and keeps sending; rate, size, duration and sessions are capped.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
var opts = Opts.Parse(args);
if (opts.Mode == "server") return await Server.Run(opts);
if (opts.Mode == "client") return await Client.Run(opts);
Console.Error.WriteLine("usage:\n  netprobe server --port 47800 --token SECRET\n" +
                        "  netprobe client --host HOST --port 47800 --token SECRET [--rate 10] [--size 1100] [--duration 600] [--out trace.csv]");
return 1;

sealed class Opts
{
    public string Mode = "", Host = "", Token = "", Out = "";
    public int Port = 47800, Rate = 10, Size = 1100, Duration = 600;

    public static Opts Parse(string[] a)
    {
        var o = new Opts { Mode = a.Length > 0 ? a[0] : "" };
        for (int i = 1; i + 1 < a.Length; i += 2)
        {
            switch (a[i])
            {
                case "--host": o.Host = a[i + 1]; break;
                case "--port": o.Port = int.Parse(a[i + 1]); break;
                case "--token": o.Token = a[i + 1]; break;
                case "--rate": o.Rate = int.Parse(a[i + 1]); break;
                case "--size": o.Size = int.Parse(a[i + 1]); break;
                case "--duration": o.Duration = int.Parse(a[i + 1]); break;
                case "--out": o.Out = a[i + 1]; break;
                default: throw new ArgumentException("unknown option " + a[i]);
            }
        }
        return o;
    }
}

static class Wire
{
    public const byte Hello = 1, Challenge = 2, Start = 3, Data = 4, Up = 5, Echo = 6, End = 7, Summary = 8;
    public const int MaxRate = 60, MaxSize = 1400, MinSize = 32, MaxDuration = 3600, MaxSessions = 4;

    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static long NowUs => Clock.ElapsedTicks * 1_000_000 / Stopwatch.Frequency;

    public static byte[] TokenHash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes("netprobe:" + token))[..16];
}

static class Server
{
    sealed class Session
    {
        public IPEndPoint Ep = null!;
        public int Rate, Size, Duration;
        public long LastHeardUs;
        public uint UpCount, UpMaxSeq, DownSent;
        public CancellationTokenSource Cts = new();
    }

    public static async Task<int> Run(Opts o)
    {
        if (o.Token.Length < 8) { Console.Error.WriteLine("--token must be at least 8 characters"); return 1; }
        byte[] token = Wire.TokenHash(o.Token);
        byte[] cookieKey = RandomNumberGenerator.GetBytes(32);
        UdpClient udp;
        try
        {
            var sock = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            sock.Bind(new IPEndPoint(IPAddress.IPv6Any, o.Port)); // IPv4 + IPv6
            udp = new UdpClient { Client = sock };
        }
        catch (SocketException)
        {
            udp = new UdpClient(new IPEndPoint(IPAddress.Any, o.Port)); // host without IPv6
        }
        using var udpOwner = udp;
        var sessions = new Dictionary<string, Session>();
        Console.WriteLine($"netprobe server listening on UDP {o.Port}");

        byte[] Cookie(IPEndPoint ep, long window) =>
            HMACSHA256.HashData(cookieKey, Encoding.UTF8.GetBytes($"{ep}|{window}"))[..16];

        while (true)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(); }
            catch (SocketException) { continue; } // ICMP port unreachable from a vanished client
            byte[] b = r.Buffer;
            if (b.Length < 1) continue;
            string key = r.RemoteEndPoint.ToString();
            long now = Wire.NowUs;
            switch (b[0])
            {
                case Wire.Hello when b.Length >= 25 && CryptographicOperations.FixedTimeEquals(b.AsSpan(1, 16), token):
                {
                    var reply = new byte[1 + 8 + 16];
                    reply[0] = Wire.Challenge;
                    b.AsSpan(17, 8).CopyTo(reply.AsSpan(1));
                    Cookie(r.RemoteEndPoint, now / 30_000_000).CopyTo(reply, 9);
                    await udp.SendAsync(reply, r.RemoteEndPoint); // same size as the request: no amplification
                    break;
                }
                case Wire.Start when b.Length >= 39 && CryptographicOperations.FixedTimeEquals(b.AsSpan(1, 16), token):
                {
                    long w = now / 30_000_000;
                    bool ok = CryptographicOperations.FixedTimeEquals(b.AsSpan(17, 16), Cookie(r.RemoteEndPoint, w))
                           || CryptographicOperations.FixedTimeEquals(b.AsSpan(17, 16), Cookie(r.RemoteEndPoint, w - 1));
                    if (!ok || sessions.ContainsKey(key) || sessions.Count >= Wire.MaxSessions) break;
                    var s = new Session
                    {
                        Ep = r.RemoteEndPoint, LastHeardUs = now,
                        Rate = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(33)), 1, Wire.MaxRate),
                        Size = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(35)), Wire.MinSize, Wire.MaxSize),
                        Duration = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(37)), 1, Wire.MaxDuration),
                    };
                    sessions[key] = s;
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} session {key}: {s.Rate}/s x {s.Size} B for {s.Duration} s");
                    _ = Stream(udp, s, () => sessions.Remove(key));
                    break;
                }
                case Wire.Up when b.Length >= 13 && sessions.TryGetValue(key, out var s):
                {
                    s.LastHeardUs = now;
                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1));
                    s.UpCount++;
                    s.UpMaxSeq = Math.Max(s.UpMaxSeq, seq);
                    var echo = new byte[1 + 4 + 8 + 8];
                    echo[0] = Wire.Echo;
                    b.AsSpan(1, 12).CopyTo(echo.AsSpan(1));
                    BinaryPrimitives.WriteInt64LittleEndian(echo.AsSpan(13), now);
                    await udp.SendAsync(echo, r.RemoteEndPoint);
                    break;
                }
                case Wire.End when sessions.TryGetValue(key, out var s):
                {
                    var sum = new byte[1 + 12];
                    sum[0] = Wire.Summary;
                    BinaryPrimitives.WriteUInt32LittleEndian(sum.AsSpan(1), s.UpCount);
                    BinaryPrimitives.WriteUInt32LittleEndian(sum.AsSpan(5), s.UpMaxSeq);
                    BinaryPrimitives.WriteUInt32LittleEndian(sum.AsSpan(9), s.DownSent);
                    for (int i = 0; i < 3; i++) await udp.SendAsync(sum, r.RemoteEndPoint);
                    s.Cts.Cancel();
                    break;
                }
            }
        }
    }

    private static async Task Stream(UdpClient udp, Session s, Action done)
    {
        var pkt = new byte[s.Size];
        RandomNumberGenerator.Fill(pkt); // incompressible padding
        pkt[0] = Wire.Data;
        long start = Wire.NowUs;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / s.Rate));
        try
        {
            while (await timer.WaitForNextTickAsync(s.Cts.Token))
            {
                long now = Wire.NowUs;
                if (now - start > s.Duration * 1_000_000L) break;
                if (now - s.LastHeardUs > 10_000_000) { Console.WriteLine($"session {s.Ep}: client silent, stopping"); break; }
                BinaryPrimitives.WriteUInt32LittleEndian(pkt.AsSpan(1), s.DownSent++);
                BinaryPrimitives.WriteInt64LittleEndian(pkt.AsSpan(5), now);
                await udp.SendAsync(pkt, s.Ep);
            }
        }
        catch (OperationCanceledException) { }
        // keep answering END for a few seconds so the client can fetch the summary
        await Task.Delay(TimeSpan.FromSeconds(15));
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} session {s.Ep} closed: sent {s.DownSent}, received {s.UpCount} up");
        done();
    }
}

static class Client
{
    public static async Task<int> Run(Opts o)
    {
        if (o.Host == "" || o.Token == "") { Console.Error.WriteLine("--host and --token are required"); return 1; }
        var addrs = await Dns.GetHostAddressesAsync(o.Host);
        var ep = new IPEndPoint(addrs[0], o.Port);
        using var udp = new UdpClient(ep.AddressFamily);
        udp.Connect(ep);
        byte[] token = Wire.TokenHash(o.Token);

        // handshake
        byte[] cookie = [];
        var nonce = RandomNumberGenerator.GetBytes(8);
        Task<UdpReceiveResult>? recv = null; // at most one receive outstanding on the socket
        for (int attempt = 0; attempt < 5 && cookie.Length == 0; attempt++)
        {
            var hello = new byte[25];
            hello[0] = Wire.Hello; token.CopyTo(hello, 1); nonce.CopyTo(hello, 17);
            await udp.SendAsync(hello);
            var deadline = Task.Delay(1000);
            while (cookie.Length == 0)
            {
                recv ??= udp.ReceiveAsync();
                if (await Task.WhenAny(recv, deadline) != recv) break; // retry HELLO
                var done = recv; recv = null;
                if (done.IsFaulted) { await deadline; break; }    // ICMP unreachable: nobody listening yet
                var c = done.Result.Buffer;
                if (c.Length == 25 && c[0] == Wire.Challenge && c.AsSpan(1, 8).SequenceEqual(nonce))
                    cookie = c[9..25];
            }
        }
        if (recv != null) _ = recv.ContinueWith(t => t.Exception); // drain; the receive loop below takes over
        if (cookie.Length == 0) { Console.Error.WriteLine("no reply from server (port forwarded? token right?)"); return 2; }

        var start = new byte[39];
        start[0] = Wire.Start; token.CopyTo(start, 1); cookie.CopyTo(start, 17);
        BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(33), (ushort)o.Rate);
        BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(35), (ushort)o.Size);
        BinaryPrimitives.WriteUInt16LittleEndian(start.AsSpan(37), (ushort)o.Duration);
        await udp.SendAsync(start);
        Console.WriteLine($"probing {ep}: {o.Rate}/s x {o.Size} B for {o.Duration} s ...");

        var down = new Dictionary<uint, (long recvUs, long serverUs)>();
        var rtts = new List<double>();
        uint reordered = 0, maxSeen = 0; bool any = false;
        uint upSent = 0;
        (uint upCount, uint upMax, uint downSent)? summary = null;
        long t0 = Wire.NowUs;
        var cts = new CancellationTokenSource();

        var receiver = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { continue; }
                var b = r.Buffer;
                long now = Wire.NowUs;
                if (b[0] == Wire.Data && b.Length >= 13)
                {
                    uint seq = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1));
                    lock (down)
                    {
                        if (!down.ContainsKey(seq)) down[seq] = (now, BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(5)));
                        if (any && seq < maxSeen) reordered++;
                        maxSeen = any ? Math.Max(maxSeen, seq) : seq; any = true;
                    }
                }
                else if (b[0] == Wire.Echo && b.Length >= 21)
                {
                    long sent = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(5));
                    lock (rtts) rtts.Add((now - sent) / 1000.0);
                }
                else if (b[0] == Wire.Summary && b.Length >= 13)
                {
                    summary = (BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(1)),
                               BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(5)),
                               BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(9)));
                }
            }
        });

        using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / o.Rate)))
        {
            int lastReport = 0;
            while (await timer.WaitForNextTickAsync())
            {
                long now = Wire.NowUs;
                if (now - t0 > (o.Duration + 2) * 1_000_000L) break;
                var up = new byte[13];
                up[0] = Wire.Up;
                BinaryPrimitives.WriteUInt32LittleEndian(up.AsSpan(1), upSent++);
                BinaryPrimitives.WriteInt64LittleEndian(up.AsSpan(5), now);
                await udp.SendAsync(up);
                int secs = (int)((now - t0) / 1_000_000);
                if (secs >= lastReport + 30)
                {
                    lastReport = secs;
                    int got; lock (down) got = down.Count;
                    Console.WriteLine($"  {secs,4} s: {got} packets received");
                }
            }
        }
        for (int i = 0; i < 20 && summary == null; i++)
        {
            await udp.SendAsync(new[] { Wire.End });
            await Task.Delay(300);
        }
        cts.Cancel();
        try { await receiver; } catch { }

        Report(o, ep, down, rtts, reordered, upSent, summary);
        return 0;
    }

    private static void Report(Opts o, IPEndPoint ep, Dictionary<uint, (long recvUs, long serverUs)> down, List<double> rtts,
                               uint reordered, uint upSent, (uint upCount, uint upMax, uint downSent)? summary)
    {
        uint expected = summary?.downSent ?? (down.Count == 0 ? 0 : down.Keys.Max() + 1);
        var lost = new bool[expected];
        for (uint i = 0; i < expected; i++) lost[i] = !down.ContainsKey(i);
        int nLost = lost.Count(x => x);

        var bursts = new SortedDictionary<int, int>();
        for (int i = 0; i < lost.Length;)
        {
            if (!lost[i]) { i++; continue; }
            int j = i; while (j < lost.Length && lost[j]) j++;
            bursts[j - i] = bursts.GetValueOrDefault(j - i) + 1;
            i = j;
        }

        // one-way delay variation: receive - send, minus the minimum (clock offset cancels out)
        var owd = down.Values.Select(v => (v.recvUs - v.serverUs) / 1000.0).ToList();
        double minOwd = owd.Count > 0 ? owd.Min() : 0;
        var jitter = owd.Select(x => x - minOwd).OrderBy(x => x).ToList();
        rtts.Sort();

        double P(List<double> s, double p) => s.Count == 0 ? double.NaN : s[Math.Min(s.Count - 1, (int)(p * s.Count))];
        var sb = new StringBuilder();
        sb.AppendLine($"# netprobe {ep} {DateTime.Now:yyyy-MM-dd HH:mm} rate={o.Rate}/s size={o.Size}B duration={o.Duration}s");
        sb.AppendLine($"# down: {expected} sent, {nLost} lost ({(expected > 0 ? nLost / (double)expected : 0):P2}), {reordered} reordered");
        sb.AppendLine($"# down loss bursts (length:count): {string.Join(" ", bursts.Select(kv => $"{kv.Key}:{kv.Value}"))}");
        if (summary is { } s)
            sb.AppendLine($"# up: {upSent} sent, {s.upCount} received ({(upSent > 0 ? 1 - s.upCount / (double)upSent : 0):P2} lost)");
        else
            sb.AppendLine("# up: no summary from server (END lost?)");
        sb.AppendLine($"# rtt ms: p50={P(rtts, .5):F1} p90={P(rtts, .9):F1} p99={P(rtts, .99):F1} max={(rtts.Count > 0 ? rtts[^1] : double.NaN):F1} (n={rtts.Count})");
        sb.AppendLine($"# down delay variation ms: p50={P(jitter, .5):F1} p95={P(jitter, .95):F1} p99={P(jitter, .99):F1} max={(jitter.Count > 0 ? jitter[^1] : double.NaN):F1}");
        string header = sb.ToString();
        Console.Write(header);

        string file = o.Out != "" ? o.Out : $"netprobe_{o.Host}_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
        using var w = new StreamWriter(file);
        w.Write(header);
        w.WriteLine("kind,seq,lost,delay_var_ms");
        for (uint i = 0; i < expected; i++)
            w.WriteLine(down.TryGetValue(i, out var v)
                ? $"D,{i},0,{(v.recvUs - v.serverUs) / 1000.0 - minOwd:F2}"
                : $"D,{i},1,");
        for (int i = 0; i < rtts.Count; i++) w.WriteLine($"E,{i},,{rtts[i]:F2}");
        Console.WriteLine($"trace written: {Path.GetFullPath(file)}");
    }
}
