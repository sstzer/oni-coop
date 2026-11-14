using System.Security.Cryptography;
using OniCoop.Net;

namespace CoopRelay;

// coop-relay serve    [--bind 0.0.0.0] [--port 47810] [--token T] [--data DIR] [--log FILE]
// coop-relay selftest                          relay + fake players in one process, no game needed
// coop-relay bot      --server HOST[:PORT] [--token T] [--room R] [--name N] [--role guest|host]
//                     [--manifest FILE.b64] [--save FILE] [--out FILE] [--center X,Y] [--radius R]
//                     [--sim delay=150,jitter=20,loss=0.05] [--duration SEC]
static class Program
{
    static StreamWriter logFile;
    static readonly object logLock = new();

    public static void Log(string msg)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
        lock (logLock)
        {
            Console.WriteLine(line);
            logFile?.WriteLine(line);
        }
    }

    static int Main(string[] args)
    {
        string cmd = args.Length > 0 && !args[0].StartsWith("--") ? args[0] : "serve";
        var o = ParseOptions(args.Length > 0 && !args[0].StartsWith("--") ? args[1..] : args);
        try
        {
            switch (cmd)
            {
                case "serve": return Serve(o);
                case "selftest": return SelfTest.Run();
                case "bot": return Bot.Run(o);
                default:
                    Console.Error.WriteLine("usage: coop-relay [serve|selftest|bot] [--options]  (see Program.cs header)");
                    return 1;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    static Dictionary<string, string> ParseOptions(string[] a)
    {
        var d = new Dictionary<string, string>();
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].StartsWith("--")) throw new ArgumentException($"unexpected argument '{a[i]}'");
            string k = a[i][2..];
            d[k] = i + 1 < a.Length && !a[i + 1].StartsWith("--") ? a[++i] : "true";
        }
        return d;
    }

    static int Serve(Dictionary<string, string> o)
    {
        var opt = new RelayOptions
        {
            Bind = o.GetValueOrDefault("bind", "0.0.0.0"),
            Port = int.Parse(o.GetValueOrDefault("port", Proto.DefaultPort.ToString())),
            Token = o.GetValueOrDefault("token", Environment.GetEnvironmentVariable("COOP_TOKEN") ?? ""),
            DataDir = o.GetValueOrDefault("data", "relay-data"),
        };
        if (o.TryGetValue("log", out string lf))
            logFile = new StreamWriter(File.Open(lf, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        if (opt.Token.Length == 0 && opt.Bind != "127.0.0.1")
            Log("WARNING: no --token: anyone who can reach this port can join. Fine for LAN tests, not for the internet.");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        new RelayServer(opt, Log).Start(cts.Token);
        try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); } catch (AggregateException) { }
        Log("relay stopped");
        return 0;
    }

    public static (string host, int port) ParseServer(string s)
    {
        int c = s.LastIndexOf(':');
        if (c > 0 && s.IndexOf(':') == c) return (s[..c], int.Parse(s[(c + 1)..]));
        return (s, Proto.DefaultPort);
    }
}

/// <summary>A scripted player for manual tests against real game instances.</summary>
static class Bot
{
    public static int Run(Dictionary<string, string> o)
    {
        var (host, port) = Program.ParseServer(o.GetValueOrDefault("server", "127.0.0.1"));
        bool isHost = o.GetValueOrDefault("role", "guest") == "host";
        var manifest = o.TryGetValue("manifest", out string mf)
            ? Manifest.Read(Wire.Reader(Convert.FromBase64String(File.ReadAllText(mf).Trim())))
            : new Manifest { build = 744825, coopVersion = "bot" };
        var hello = new HelloMsg
        {
            token = o.GetValueOrDefault("token", ""), room = o.GetValueOrDefault("room", "default"),
            name = o.GetValueOrDefault("name", isHost ? "BotHost" : "Bot"), role = isHost ? Role.Host : Role.Guest,
            wantWorld = !isHost && o.ContainsKey("out"), manifest = manifest,
        };
        byte[] save = isHost && o.TryGetValue("save", out string sf) ? File.ReadAllBytes(sf) : null;
        string[] c = o.GetValueOrDefault("center", "100,100").Split(',');
        float cx = float.Parse(c[0]), cy = float.Parse(c[1]), radius = float.Parse(o.GetValueOrDefault("radius", "15"));
        double duration = double.Parse(o.GetValueOrDefault("duration", "600"));

        var client = new CoopClient(host, port, hello, LinkSim.Parse(o.GetValueOrDefault("sim", "")), Program.Log);
        client.Start();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool inGame = !hello.wantWorld;
        uint seq = 0;
        double nextPresence = 0;
        while (sw.Elapsed.TotalSeconds < duration)
        {
            client.Tick();
            while (client.TryPoll(out ClientEvent ev))
            {
                if (ev.type != EvType.Presence) Program.Log("bot: " + ev);
                switch (ev.type)
                {
                    case EvType.Connected:
                        foreach (string w in ev.welcome.warnings) Program.Log("bot: warning: " + w);
                        client.SendState(new StateMsg { state = inGame ? MemberState.InGame : MemberState.WaitingForWorld });
                        break;
                    case EvType.Rejected:
                        foreach (string d in ev.reject.details) Program.Log("bot:   " + d);
                        break;
                    case EvType.Closed:
                        return 3;
                    case EvType.Members:
                        Program.Log("bot: members " + string.Join(", ", ev.members.Select(m => $"#{m.id} {m.name} {m.role} {m.state}{(m.udp ? " udp" : "")}")));
                        break;
                    case EvType.SaveRequest:
                        if (save == null) client.SendSaveFailed(ev.requestId, "bot has no --save");
                        else client.SendSave(ev.requestId, "bot.sav", save);
                        break;
                    case EvType.SaveReceived:
                        File.WriteAllBytes(o["out"], ev.data);
                        Program.Log($"bot: wrote {ev.data.Length} B to {o["out"]} (sha256 {Convert.ToHexStringLower(SHA256.HashData(ev.data))[..16]}…)");
                        inGame = true;
                        client.SendState(new StateMsg { state = MemberState.InGame });
                        break;
                }
            }
            double t = sw.Elapsed.TotalSeconds;
            if (inGame && client.Welcomed && t >= nextPresence)
            {
                nextPresence = t + 0.1;
                float vx = cx + radius * (float)Math.Cos(t * 0.4), vy = cy + radius * 0.5f * (float)Math.Sin(t * 0.4);
                client.SendPresence(new PresenceData
                {
                    seq = ++seq, world = 0,
                    viewMinX = vx - 24, viewMaxX = vx + 24, viewMinY = vy - 14, viewMaxY = vy + 14,
                    cursorX = vx + 8 * (float)Math.Cos(t * 2.1), cursorY = vy + 5 * (float)Math.Sin(t * 1.7),
                    flags = PresenceData.FlagCursor | PresenceData.FlagInGame, rttMs = (ushort)Math.Max(0, client.RttMs),
                });
            }
            Thread.Sleep(10);
        }
        client.Close("bot finished");
        Thread.Sleep(300);
        return 0;
    }
}
