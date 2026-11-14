using System.Globalization;
using System.Text;
using DivergenceAnalyzer;
using SyncBench;

// Usage: dotnet run --project tools/SyncBench -c Release -- <run dir with per-tick snapshots> [--seeds N] [--trace netprobe.csv ...]
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
if (args.Length < 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: SyncBench <run dir> [--seeds N] [--trace file.csv ...]");
    return 1;
}
string dir = args[0];
int seeds = 20;
var traces = new List<string>();
for (int a = 1; a < args.Length; a++)
{
    if (args[a] == "--seeds") seeds = int.Parse(args[++a]);
    else if (args[a] == "--trace") traces.Add(args[++a]);
}

const double TickMs = 200;
var run = Run.Load(dir);
var tickList = run.SnapFiles.Keys.Where(t => t >= 0).OrderBy(t => t).ToList();
bool consecutive = tickList.Zip(tickList.Skip(1), (x, y) => y - x).All(d => d == 1);
var md = new StringBuilder();
md.AppendLine($"# Sync bandwidth & loss — {Path.GetFileName(Path.GetDirectoryName(dir.TrimEnd('/')))}/{Path.GetFileName(dir.TrimEnd('/'))}\n");
if (!consecutive)
    md.AppendLine($"> WARNING: snapshots are not consecutive ticks ({tickList.Count} snapshots); numbers below are only a smoke test.\n");

// Viewports centred on the duplicants at the first tick (where players look most).
var dupes = run.Entities[tickList[0]].Values.Where(e => e.Kind == 'D').ToList();
int cx = (int)dupes.Average(e => e.Cell % run.W), cy = (int)dupes.Average(e => e.Cell / run.W);
View Centered(string name, int w, int h) =>
    new(name, Math.Clamp(cx - w / 2, 0, run.W - w), Math.Clamp(cy - h / 2, 0, run.H - h), w, h);
var views = new List<View>
{
    Centered("normal zoom 64x40", 64, 40),
    Centered("zoomed out 128x80", 128, 80),
    new("whole map", 0, 0, run.W, run.H),
};
md.AppendLine($"{tickList.Count} ticks, grid {run.W}x{run.H}, duplicants centred at ({cx},{cy}). Views: " +
              string.Join(", ", views.Select(v => $"{v.Name} at ({v.X0},{v.Y0})")) + "\n");

// ---- load: keep every tick for the small views, stream the whole map
var states = views.Take(2).Select(_ => new List<QState>()).ToList();
var mapFull = new List<int>(); var mapDelta = new List<int>(); var mapChanged = new List<double>();
QState? mapPrev = null;
foreach (int t in tickList)
{
    var snap = Snapshot.Read(run.SnapFiles[t]);
    for (int v = 0; v < 2; v++) states[v].Add(Quant.From(snap, views[v]));
    var m = Quant.From(snap, views[2]);
    mapFull.Add(Codec.Size(Codec.Full(m), Comp.Brotli));
    if (mapPrev != null)
    {
        mapDelta.Add(Codec.Size(Codec.Delta(mapPrev, m), Comp.Brotli));
        mapChanged.Add(ChangedFraction(mapPrev, m));
    }
    mapPrev = m;
    Console.Error.Write($"\rloaded tick {t}   ");
}
Console.Error.WriteLine();

// ---- bandwidth
md.AppendLine("## Bandwidth (grid only, per player)\n");
md.AppendLine("Bytes per packet are averages over the run (including a 12-byte header). kbps = payload rate, " +
              "before IP/UDP overhead. Quantization: mass as half-float (~0.05 %), temperature 0.05 K, germ count log2/8.\n");
md.AppendLine("| view | cells changed per tick | raw full | quantized full | delta, no compression | delta + deflate | delta + brotli | brotli 1 tick (speed 1, 5/s) | brotli 3 ticks (speed 3, 5/s) | brotli 5 ticks (speed 1, 1/s) |");
md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
for (int v = 0; v < 2; v++)
{
    var s = states[v];
    double changed = Enumerable.Range(1, s.Count - 1).Average(i => ChangedFraction(s[i - 1], s[i]));
    double raw = views[v].N * 15 + Codec.Header;
    double full = s.Average(x => Codec.Size(Codec.Full(x), Comp.Brotli));
    double dNone = AvgDelta(s, 1, Comp.None), dDef = AvgDelta(s, 1, Comp.Deflate), d1 = AvgDelta(s, 1, Comp.Brotli);
    double d3 = AvgDelta(s, 3, Comp.Brotli), d5 = AvgDelta(s, 5, Comp.Brotli);
    md.AppendLine($"| {views[v].Name} | {changed:P1} | {raw:F0} B | {full:F0} B | {dNone:F0} B | {dDef:F0} B | {d1:F0} B | " +
                  $"**{Kbps(d1, 5):F0} kbps** | {Kbps(d3, 5):F0} kbps | {Kbps(d5, 1):F0} kbps |");
}
md.AppendLine($"| whole map | {mapChanged.Average():P1} | {run.W * run.H * 15 + 12} B | {mapFull.Average():F0} B | | | {mapDelta.Average():F0} B | {Kbps(mapDelta.Average(), 5):F0} kbps | | |\n");

// dead-band: only send a cell once the client's copy is off by more than a threshold
md.AppendLine("Dead-band (send a cell only when the client's copy is off by more than the threshold; the client's error " +
              "is then bounded by the threshold). Delta + brotli, speed 1, 5 updates/s:\n");
md.AppendLine("| view | threshold (mass / temperature) | cells sent per tick | bytes per packet | kbps | max error on screen |");
md.AppendLine("|---|---|---|---|---|---|");
foreach (var (mRel, tK) in new[] { (0.01f, 0.1f), (0.02f, 0.5f), (0.05f, 1.0f), (0.10f, 2.0f) })
    for (int v = 0; v < 2; v++)
    {
        var s = states[v];
        var client = Clone(s[0]);
        double cells = 0, bytes = 0;
        for (int i = 1; i < s.Count; i++)
        {
            var next = Clone(client);
            int sent = 0;
            for (int c = 0; c < next.N; c++)
            {
                float mc = Quant.MassOf(client.Mass[c]), ms = Quant.MassOf(s[i].Mass[c]);
                bool off = client.Elem[c] != s[i].Elem[c] || client.DisIdx[c] != s[i].DisIdx[c]
                        || Math.Abs(mc - ms) > mRel * Math.Max(Math.Max(Math.Abs(mc), Math.Abs(ms)), 1e-6f)
                        || Math.Abs((long)client.Temp[c] - s[i].Temp[c]) * Quant.TempStep > tK
                        || Math.Abs(client.DisCnt[c] - s[i].DisCnt[c]) > 8;
                if (!off) continue;
                next.Elem[c] = s[i].Elem[c]; next.Mass[c] = s[i].Mass[c]; next.Temp[c] = s[i].Temp[c];
                next.DisIdx[c] = s[i].DisIdx[c]; next.DisCnt[c] = s[i].DisCnt[c];
                sent++;
            }
            cells += sent / (double)next.N;
            bytes += Codec.Size(Codec.Delta(client, next), Comp.Brotli);
            client = next;
        }
        int n = s.Count - 1;
        md.AppendLine($"| {views[v].Name} | {mRel:P0} / {tK} K | {cells / n:P1} | {bytes / n:F0} B | **{Kbps(bytes / n, 5):F0} kbps** | mass {mRel:P0}, {tK} K |");
    }
md.AppendLine();

// entities
md.AppendLine("Duplicants and critters inside the view (position 1/32 cell, anim and chore only when changed, brotli):\n");
md.AppendLine("| view | entities in view (avg) | bytes per tick | kbps at 5/s |");
md.AppendLine("|---|---|---|---|");
for (int v = 0; v < 2; v++)
{
    var sizes = new List<int>(); var counts = new List<int>();
    for (int i = 1; i < tickList.Count; i++)
    {
        run.Entities.TryGetValue(tickList[i - 1], out var prev);
        run.Entities.TryGetValue(tickList[i], out var cur);
        var raw = EntityCodec.Delta(prev, cur, views[v], run.W);
        sizes.Add(raw.Length == 0 ? 0 : Codec.Size(raw, Comp.Brotli) - Codec.Header + 2);
        counts.Add(cur?.Values.Count(e => views[v].Contains(e.Cell, run.W, 4)) ?? 0);
    }
    md.AppendLine($"| {views[v].Name} | {counts.Average():F1} | {sizes.Average():F0} B | {Kbps(sizes.Average(), 5):F1} kbps |");
}
md.AppendLine();

// ---- loss & latency
var profiles = new[]
{
    new NetProfile("you -> home server (one-way ~160 ms)", 160, 25),
    new NetProfile("friend -> home server (one-way ~40 ms)", 40, 5),
};
var models = new List<Func<LossModel>>
{
    () => new NoLoss(), () => new Bernoulli(0.01), () => new Bernoulli(0.03), () => new Bernoulli(0.05),
    () => new Bernoulli(0.10), () => new Bernoulli(0.20), () => new GilbertElliott(0.05, 4),
};
foreach (string tf in traces)
{
    var lost = File.ReadLines(tf).Where(l => l.StartsWith("D,")).Select(l => l.Split(',')[2] == "1").ToArray();
    string name = $"trace {Path.GetFileName(tf)} ({lost.Count(x => x) / (double)lost.Length:P1})";
    models.Add(() => new TraceLoss(lost, name));
}

for (int v = 0; v < 2; v++)
{
    var s = states[v];
    var fullCache = new Dictionary<int, int>();
    var deltaCache = new Dictionary<(int, int), int>();
    var errCache = new Dictionary<(int, int), (double, double)>();
    var sim = new NetSim(s.Count, TickMs,
        t => Cached(fullCache, t, () => Codec.Size(Codec.Full(s[t]), Comp.Brotli)),
        (b, t) => Cached(deltaCache, (b, t), () => Codec.Size(Codec.Delta(s[b], s[t]), Comp.Brotli)),
        (j, i) => Cached(errCache, (j, i), () => Metrics.Diff(s[j], s[i])));

    md.AppendLine($"## Loss & latency — {views[v].Name}, speed 1, 5 updates/s, {seeds} seeds each\n");
    md.AppendLine("Strategies: **keyframe** = whole view every update; **delta+NACK** = delta to the previous update, " +
                  "client asks for a keyframe when the chain breaks; **delta-to-acked** = delta to the newest state the " +
                  "client has acknowledged (Quake 3 style); **reliable** = deltas over an ordered, retransmitting stream (TCP-like).\n");
    md.AppendLine("stale = how old the shown state is vs the server's current tick (one tick = 200 ms is the floor); " +
                  "visual error = share of cells whose element differs or mass is off by > 5 % vs the server right now.\n");
    foreach (var p in profiles)
    {
        md.AppendLine($"### {p.Name}\n");
        md.AppendLine("| loss | strategy | down kbps | p95 packet | stale mean | stale p95 | stale max | frozen > 1 s | visual err mean | visual err p95 | strict err mean |");
        md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var mk in models)
            foreach (Strategy st in Enum.GetValues<Strategy>())
            {
                var rs = Enumerable.Range(0, seeds).Select(seed => sim.Run(st, p, mk(), mk(), 1, seed * 7919 + 1)).ToList();
                md.AppendLine($"| {mk().Name} | {st} | {rs.Average(r => r.DownKbps):F0} | {rs.Average(r => r.P95PacketBytes):F0} B | " +
                              $"{rs.Average(r => r.StaleMeanMs):F0} ms | {rs.Average(r => r.StaleP95Ms):F0} ms | {rs.Max(r => r.StaleMaxMs):F0} ms | " +
                              $"{rs.Average(r => r.FrozenOver1s):P1} | {rs.Average(r => r.VisualErrMean):P2} | {rs.Average(r => r.VisualErrP95):P2} | {rs.Average(r => r.StrictErrMean):P1} |");
            }
        md.AppendLine();
    }
}

string outFile = Path.Combine(dir, "syncbench.md");
File.WriteAllText(outFile, md.ToString());
Console.WriteLine(md.ToString());
Console.Error.WriteLine($"written: {outFile}");
return 0;

static double ChangedFraction(QState a, QState b)
{
    int c = 0;
    for (int i = 0; i < a.N; i++)
        if (a.Elem[i] != b.Elem[i] || a.Mass[i] != b.Mass[i] || a.Temp[i] != b.Temp[i] || a.DisIdx[i] != b.DisIdx[i] || a.DisCnt[i] != b.DisCnt[i]) c++;
    return c / (double)a.N;
}

static QState Clone(QState s) => new()
{
    Tick = s.Tick, N = s.N,
    Elem = (ushort[])s.Elem.Clone(), Mass = (ushort[])s.Mass.Clone(), Temp = (uint[])s.Temp.Clone(),
    DisIdx = (byte[])s.DisIdx.Clone(), DisCnt = (byte[])s.DisCnt.Clone(),
};

static double AvgDelta(List<QState> s, int k, Comp c)
{
    var sizes = new List<int>();
    for (int i = k; i < s.Count; i += k) sizes.Add(Codec.Size(Codec.Delta(s[i - k], s[i]), c));
    return sizes.Count > 0 ? sizes.Average() : 0;
}

static double Kbps(double bytesPerPacket, double packetsPerSecond) => bytesPerPacket * packetsPerSecond * 8 / 1000.0;

static TV Cached<TK, TV>(Dictionary<TK, TV> cache, TK key, Func<TV> make) where TK : notnull
{
    if (!cache.TryGetValue(key, out var v)) cache[key] = v = make();
    return v;
}
