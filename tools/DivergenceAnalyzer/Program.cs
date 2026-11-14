using System.Globalization;
using System.Text;
using DivergenceAnalyzer;

// Usage: dotnet run --project tools/DivergenceAnalyzer -- <session dir>
// Compares consecutive runs of the same variant (run0 vs run1, run2 vs run3, ...) and writes
// <session>/analysis/report.md plus diff heatmaps.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

if (args.Length < 1 || !Directory.Exists(args[0]))
{
    Console.Error.WriteLine("usage: DivergenceAnalyzer <session dir>");
    return 1;
}
string session = args[0];
var runs = Directory.GetDirectories(session, "run*_*").Select(Run.Load).OrderBy(r => r.Index).ToList();
string outDir = Path.Combine(session, "analysis");
Directory.CreateDirectory(outDir);

var md = new StringBuilder();
md.AppendLine($"# Divergence report — {Path.GetFileName(session)}\n");
md.AppendLine("| run | variant | ticks | frames | seconds | frames/tick | zero-dt calls | unexpected pauses | error |");
md.AppendLine("|---|---|---|---|---|---|---|---|---|");
foreach (var r in runs)
{
    var real = r.Ticks.Values.Where(t => t.Tick >= 0).ToList();
    string M(string k) => r.Meta.ValueKind == System.Text.Json.JsonValueKind.Object && r.Meta.TryGetProperty(k, out var v) ? v.ToString() : "?";
    double fpt = real.Count > 1 ? (real[^1].Frames - real[0].Frames) / (double)(real.Count - 1) : 0;
    md.AppendLine($"| {r.Index} | {r.Variant} | {real.Count} | {M("frames")} | {M("seconds")} | {fpt:F2} | {real.Sum(t => t.ZeroDt)} | {M("unexpectedPauses")} | {M("error")} |");
}
md.AppendLine();

var summary = new List<string>();
for (int i = 0; i + 1 < runs.Count; i++)
{
    if (runs[i].Variant != runs[i + 1].Variant) continue;
    summary.Add(Compare(runs[i], runs[i + 1], md, outDir));
    i++;
}

md.Insert(0, "## Summary\n\n" + string.Join("\n", summary.Select(s => "- " + s)) + "\n\n");
File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString());
Console.WriteLine(string.Join("\n", summary));
Console.WriteLine($"\nreport: {Path.Combine(outDir, "report.md")}");
return 0;

static string Compare(Run a, Run b, StringBuilder md, string outDir)
{
    string label = $"run{a.Index} vs run{b.Index} ({a.Variant})";
    md.AppendLine($"## {label}\n");
    var ticks = a.Ticks.Keys.Intersect(b.Ticks.Keys).OrderBy(t => t).ToList();
    if (ticks.Count == 0) { md.AppendLine("no common ticks\n"); return label + ": no data"; }

    // ---- per-field first divergence
    var fields = new (string name, Func<TickRec, ulong> get)[]
    {
        ("RNG state", t => t.Rng), ("element", t => t.HElem), ("mass", t => t.HMass),
        ("temperature", t => t.HTemp), ("disease", t => t.HDisease), ("duplicants", t => t.HDupes),
        ("critters", t => t.HCritters), ("items", t => t.HItems),
    };
    md.AppendLine("| field | first differing tick | differs at pre-state (-1) |");
    md.AppendLine("|---|---|---|");
    var firsts = new Dictionary<string, int?>();
    foreach (var (name, get) in fields)
    {
        int? first = ticks.Cast<int?>().FirstOrDefault(t => get(a.Ticks[t!.Value]) != get(b.Ticks[t!.Value]));
        firsts[name] = first;
        md.AppendLine($"| {name} | {Fmt(first)} | {(get(a.Ticks[-1]) != get(b.Ticks[-1]) ? "YES" : "no")} |");
    }
    md.AppendLine();

    // ---- chunks
    int Diff(int t) => Enumerable.Range(0, a.NChunks).Count(c => a.Ticks[t].Chunks[c] != b.Ticks[t].Chunks[c]);
    int? firstChunkTick = ticks.Cast<int?>().FirstOrDefault(t => Diff(t!.Value) > 0);
    md.AppendLine($"Differing 32x32 chunks over time (of {a.NChunks}):\n");
    md.AppendLine("| tick | " + string.Join(" | ", Milestones(ticks)) + " |");
    md.AppendLine("|---|" + string.Concat(Milestones(ticks).Select(_ => "---|")));
    md.AppendLine("| chunks | " + string.Join(" | ", Milestones(ticks).Select(t => Diff(t).ToString())) + " |\n");
    if (firstChunkTick is int fct)
    {
        var cs = Enumerable.Range(0, a.NChunks).Where(c => a.Ticks[fct].Chunks[c] != b.Ticks[fct].Chunks[c]).ToList();
        md.AppendLine($"First differing chunks at tick {fct}: " + string.Join("; ", cs.Take(12).Select(a.ChunkRange)) + (cs.Count > 12 ? " …" : "") + "\n");
    }

    // ---- timing
    var fa = ticks.Where(t => t >= 0).Select(t => a.Ticks[t].Frames).ToList();
    var fb = ticks.Where(t => t >= 0).Select(t => b.Ticks[t].Frames).ToList();
    int? firstFrameDiff = ticks.Where(t => t >= 0).Cast<int?>().FirstOrDefault(t => a.Ticks[t!.Value].Frames != b.Ticks[t!.Value].Frames);
    md.AppendLine($"Rendered frames at the same tick first differ at tick {Fmt(firstFrameDiff)}; zero-dt sim calls: {a.Ticks.Values.Sum(t => t.ZeroDt)} vs {b.Ticks.Values.Sum(t => t.ZeroDt)}.\n");

    // ---- entities
    string dupeLine = EntityDivergence(a, b, ticks, 'D', md);
    EntityDivergence(a, b, ticks, 'C', md);

    // ---- snapshots
    md.AppendLine("Full-grid comparison at snapshot ticks (meaningful = element differs, |Δmass| > 0.1 %, |ΔT| > 0.01 K, or disease differs):\n");
    md.AppendLine("| tick | bit-exact differing cells | meaningful | element | mass | temperature | disease | max ΔT (K) | Σ|Δmass| (kg) | heatmap |");
    md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
    foreach (int t in a.SnapFiles.Keys.Intersect(b.SnapFiles.Keys).OrderBy(t => t))
    {
        var sa = Snapshot.Read(a.SnapFiles[t]);
        var sb = Snapshot.Read(b.SnapFiles[t]);
        int n = sa.W * sa.H, exact = 0, mean = 0, ne = 0, nm = 0, nt = 0, nd = 0;
        double maxDT = 0, sumDM = 0;
        var flag = new byte[n]; // 0 none, 1 bit-exact only, 2 meaningful
        for (int i = 0; i < n; i++)
        {
            bool e = sa.Elem[i] != sb.Elem[i];
            float ma = sa.Mass[i], mb = sb.Mass[i];
            bool m = Math.Abs(ma - mb) > 1e-3 * Math.Max(Math.Max(Math.Abs(ma), Math.Abs(mb)), 1e-6);
            bool tt = Math.Abs(sa.Temp[i] - sb.Temp[i]) > 0.01f;
            bool d = sa.DisIdx[i] != sb.DisIdx[i] || sa.DisCount[i] != sb.DisCount[i];
            bool bits = e || d || BitConverter.SingleToInt32Bits(ma) != BitConverter.SingleToInt32Bits(mb)
                        || BitConverter.SingleToInt32Bits(sa.Temp[i]) != BitConverter.SingleToInt32Bits(sb.Temp[i]);
            if (bits) { exact++; flag[i] = 1; }
            if (e || m || tt || d) { mean++; flag[i] = 2; }
            if (e) ne++; if (m) nm++; if (tt) nt++; if (d) nd++;
            if (!e) { maxDT = Math.Max(maxDT, Math.Abs(sa.Temp[i] - sb.Temp[i])); sumDM += Math.Abs(ma - mb); }
        }
        string png = $"pair{a.Index}-{b.Index}_{a.Variant}_t{(t < 0 ? "pre" : t.ToString("D4"))}.png";
        Heatmap(Path.Combine(outDir, png), sa, a, flag, t);
        md.AppendLine($"| {(t < 0 ? "pre" : t)} | {exact} | {mean} | {ne} | {nm} | {nt} | {nd} | {maxDT:G4} | {sumDM:G4} | {png} |");
    }
    md.AppendLine();

    string verdict = $"{label}: RNG first differs at {Fmt(firsts["RNG state"])}, grid (any chunk) at {Fmt(firstChunkTick)}, " +
                     $"element at {Fmt(firsts["element"])}, duplicants at {Fmt(firsts["duplicants"])}; {dupeLine}";
    return verdict;
}

static string EntityDivergence(Run a, Run b, List<int> ticks, char kind, StringBuilder md)
{
    string what = kind == 'D' ? "Duplicants" : "Critters";
    var first = new Dictionary<int, (int tick, string detail)>();
    var firstPos = new Dictionary<int, int>();
    foreach (int t in ticks)
    {
        a.Entities.TryGetValue(t, out var ea); b.Entities.TryGetValue(t, out var eb);
        ea ??= new(); eb ??= new();
        foreach (var key in ea.Keys.Union(eb.Keys).Where(k => k.Item1 == kind))
        {
            if (first.ContainsKey(key.Item2)) continue;
            bool ha = ea.TryGetValue(key, out var x), hb = eb.TryGetValue(key, out var y);
            if (!ha || !hb) { first[key.Item2] = (t, $"{(x ?? y)!.Name}: exists only in run{(ha ? a.Index : b.Index)}"); continue; }
            if (!firstPos.ContainsKey(key.Item2) && (x!.X != y!.X || x.Y != y.Y)) firstPos[key.Item2] = t;
            if (x!.Cell != y!.Cell || x.Chore != y.Chore)
                first[key.Item2] = (t, $"{x.Name}: cell {x.Cell} vs {y.Cell}, chore {x.Chore} vs {y.Chore}, anim {x.Anim} vs {y.Anim}");
        }
    }
    int total = a.Entities.TryGetValue(ticks[^1], out var last) ? last.Keys.Count(k => k.Item1 == kind) : 0;
    md.AppendLine($"{what}: {first.Count}/{total} diverged in cell or chore; first at tick {(first.Count > 0 ? first.Values.Min(v => v.tick).ToString() : "never")}; " +
                  $"first exact-position difference at tick {(firstPos.Count > 0 ? firstPos.Values.Min().ToString() : "never")}.\n");
    if (first.Count > 0)
    {
        md.AppendLine("| first tick | detail |");
        md.AppendLine("|---|---|");
        foreach (var v in first.Values.OrderBy(v => v.tick).Take(kind == 'D' ? 40 : 15))
            md.AppendLine($"| {v.tick} | {v.detail} |");
        md.AppendLine();
    }
    return first.Count == 0
        ? $"{what.ToLower()} never diverged"
        : $"{first.Count}/{total} {what.ToLower()} diverged, first at tick {first.Values.Min(v => v.tick)}";
}

static void Heatmap(string file, Snapshot s, Run run, byte[] flag, int tick)
{
    const int scale = 2;
    int W = s.W * scale, H = s.H * scale;
    var rgb = new byte[W * H * 3];
    var dupes = new HashSet<int>();
    if (run.Entities.TryGetValue(tick, out var ents))
        foreach (var e in ents.Values.Where(e => e.Kind == 'D')) dupes.Add(e.Cell);
    for (int y = 0; y < s.H; y++)
        for (int x = 0; x < s.W; x++)
        {
            int i = y * s.W + x;
            string st = s.Elem[i] < run.ElementStates.Length ? run.ElementStates[s.Elem[i]] : "";
            (byte r, byte g, byte b) c = st.Contains("Solid") ? ((byte)70, (byte)62, (byte)55)
                : st.Contains("Liquid") ? ((byte)40, (byte)80, (byte)150)
                : st.Contains("Gas") ? ((byte)150, (byte)155, (byte)160)
                : ((byte)0, (byte)0, (byte)0);
            if (flag[i] == 1) c = (255, 170, 0);
            if (flag[i] == 2) c = (255, 0, 0);
            if (dupes.Contains(i)) c = (0, 255, 0);
            for (int dy = 0; dy < scale; dy++)
                for (int dx = 0; dx < scale; dx++)
                {
                    int py = (s.H - 1 - y) * scale + dy, px = x * scale + dx; // grid y=0 is the bottom
                    int o = (py * W + px) * 3;
                    rgb[o] = c.r; rgb[o + 1] = c.g; rgb[o + 2] = c.b;
                }
        }
    Png.Write(file, W, H, rgb);
}

static List<int> Milestones(List<int> ticks)
{
    int last = ticks[^1];
    return new[] { -1, 0, 1, 2, 3, 5, 10, 20, 50, 100, 150, 200, 250, last }
        .Where(t => ticks.Contains(t)).Distinct().ToList();
}

static string Fmt(int? t) => t is int v ? (v < 0 ? "pre-state" : v.ToString()) : "never";
