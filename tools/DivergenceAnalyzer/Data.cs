using System.IO.Compression;
using System.Text.Json;

namespace DivergenceAnalyzer;

/// <summary>One record of ticks.bin (see src/DivergenceProbe/Recorder.cs).</summary>
sealed record TickRec(
    int Tick, int Frames, int ZeroDt, float Secs, ulong Rng,
    ulong HElem, ulong HMass, ulong HTemp, ulong HDisease,
    ulong HDupes, int NDupes, ulong HCritters, int NCritters, ulong HItems, int NItems,
    ulong[] Chunks);

sealed record Entity(char Kind, int Id, string Name, int Cell, float X, float Y, string Chore, string Anim);

sealed class Snapshot
{
    public int W, H, Tick;
    public ushort[] Elem = [];
    public float[] Mass = [], Temp = [];
    public byte[] DisIdx = [];
    public int[] DisCount = [];

    public static Snapshot Read(string file)
    {
        using var fs = File.OpenRead(file);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var br = new BinaryReader(gz);
        if (br.ReadInt32() != 0x31535044) throw new InvalidDataException("bad snapshot magic: " + file);
        var s = new Snapshot { W = br.ReadInt32(), H = br.ReadInt32(), Tick = br.ReadInt32() };
        int n = s.W * s.H;
        byte[] b = br.ReadBytes(n * 15);
        if (b.Length != n * 15) throw new InvalidDataException("truncated snapshot: " + file);
        s.Elem = new ushort[n]; Buffer.BlockCopy(b, 0, s.Elem, 0, n * 2);
        s.Mass = new float[n]; Buffer.BlockCopy(b, n * 2, s.Mass, 0, n * 4);
        s.Temp = new float[n]; Buffer.BlockCopy(b, n * 6, s.Temp, 0, n * 4);
        s.DisIdx = new byte[n]; Buffer.BlockCopy(b, n * 10, s.DisIdx, 0, n);
        s.DisCount = new int[n]; Buffer.BlockCopy(b, n * 11, s.DisCount, 0, n * 4);
        return s;
    }
}

sealed class Run
{
    public string Dir = "";
    public int Index;
    public string Variant = "";
    public int W, H, ChunkSize, NChunks;
    public Dictionary<int, TickRec> Ticks = new();
    public Dictionary<int, Dictionary<(char, int), Entity>> Entities = new();
    public Dictionary<int, string> SnapFiles = new(); // tick (-1 = pre) -> file
    public string[] ElementNames = [];
    public string[] ElementStates = [];
    public JsonElement Meta;

    public static Run Load(string dir)
    {
        var r = new Run { Dir = dir };
        string name = Path.GetFileName(dir.TrimEnd('/', '\\')); // run{N}_{variant}
        int us = name.IndexOf('_');
        if (name.StartsWith("run") && us > 3 && int.TryParse(name[3..us], out int idx))
        {
            r.Index = idx;
            r.Variant = name[(us + 1)..];
        }
        else
        {
            r.Variant = name;
        }

        string metaFile = Path.Combine(dir, "meta.json");
        if (File.Exists(metaFile))
            r.Meta = JsonDocument.Parse(File.ReadAllText(metaFile)).RootElement.Clone();

        using (var br = new BinaryReader(File.OpenRead(Path.Combine(dir, "ticks.bin"))))
        {
            if (br.ReadInt32() != 0x31545044) throw new InvalidDataException("bad ticks.bin magic in " + dir);
            r.W = br.ReadInt32(); r.H = br.ReadInt32(); r.ChunkSize = br.ReadInt32(); r.NChunks = br.ReadInt32();
            int recSize = 4 + 4 + 4 + 4 + 8 + 32 + 12 + 12 + 12 + 8 * r.NChunks;
            while (br.BaseStream.Length - br.BaseStream.Position >= recSize)
            {
                int tick = br.ReadInt32();
                var rec = new TickRec(tick, br.ReadInt32(), br.ReadInt32(), br.ReadSingle(), br.ReadUInt64(),
                    br.ReadUInt64(), br.ReadUInt64(), br.ReadUInt64(), br.ReadUInt64(),
                    br.ReadUInt64(), br.ReadInt32(), br.ReadUInt64(), br.ReadInt32(), br.ReadUInt64(), br.ReadInt32(),
                    Enumerable.Range(0, r.NChunks).Select(_ => br.ReadUInt64()).ToArray());
                r.Ticks[tick] = rec;
            }
        }

        foreach (string line in File.ReadLines(Path.Combine(dir, "entities.tsv")).Skip(1))
        {
            var f = line.Split('\t');
            if (f.Length < 9) continue;
            int tick = int.Parse(f[0]);
            var e = new Entity(f[1][0], int.Parse(f[2]), f[3], int.Parse(f[4]), float.Parse(f[5]), float.Parse(f[6]), f[7], f[8]);
            if (!r.Entities.TryGetValue(tick, out var d)) r.Entities[tick] = d = new();
            d[(e.Kind, e.Id)] = e;
        }

        foreach (string f in Directory.GetFiles(dir, "snap_*.bin.gz"))
        {
            string t = Path.GetFileName(f)["snap_".Length..^".bin.gz".Length];
            r.SnapFiles[t == "pre" ? -1 : int.Parse(t)] = f;
        }

        var els = File.ReadAllLines(Path.Combine(dir, "elements.tsv")).Select(l => l.Split('\t')).ToList();
        r.ElementNames = els.Select(x => x[1]).ToArray();
        r.ElementStates = els.Select(x => StateName(x.Length > 2 ? x[2] : "")).ToArray();
        return r;
    }

    /// <summary>Element.State is a flags enum: the low two bits are Vacuum/Gas/Liquid/Solid, higher bits
    /// are extra flags, so ToString() gives "Solid" for plain solids but e.g. "5" for oxygen.</summary>
    private static string StateName(string s)
    {
        int v = int.TryParse(s, out int n) ? n & 3
              : s.Contains("Solid") ? 3 : s.Contains("Liquid") ? 2 : s.Contains("Gas") ? 1 : 0;
        return v switch { 1 => "Gas", 2 => "Liquid", 3 => "Solid", _ => "Vacuum" };
    }

    public string ChunkRange(int c)
    {
        int cw = (W + ChunkSize - 1) / ChunkSize;
        int cx = c % cw, cy = c / cw;
        return $"x[{cx * ChunkSize}..{Math.Min(W, (cx + 1) * ChunkSize)}) y[{cy * ChunkSize}..{Math.Min(H, (cy + 1) * ChunkSize)})";
    }
}
