using System.IO.Compression;
using DivergenceAnalyzer;

namespace SyncBench;

/// <summary>A viewport's grid state after quantization, structure-of-arrays (compresses better).</summary>
sealed class QState
{
    public int Tick, N;
    public ushort[] Elem = [];
    public ushort[] Mass = [];   // IEEE half bits: ~0.05 % relative precision, more than hover text shows
    public uint[] Temp = [];     // units of Quant.TempStep kelvin
    public byte[] DisIdx = [];
    public byte[] DisCnt = [];   // log2-quantized germ count
}

readonly record struct View(string Name, int X0, int Y0, int W, int H)
{
    public int N => W * H;
    public bool Contains(int cell, int gridW, int margin = 0)
    {
        int x = cell % gridW, y = cell / gridW;
        return x >= X0 - margin && x < X0 + W + margin && y >= Y0 - margin && y < Y0 + H + margin;
    }
}

static class Quant
{
    public const float TempStep = 0.05f;

    public static QState From(Snapshot s, View v)
    {
        var q = new QState
        {
            Tick = s.Tick, N = v.N,
            Elem = new ushort[v.N], Mass = new ushort[v.N], Temp = new uint[v.N],
            DisIdx = new byte[v.N], DisCnt = new byte[v.N],
        };
        int k = 0;
        for (int y = v.Y0; y < v.Y0 + v.H; y++)
            for (int x = v.X0; x < v.X0 + v.W; x++, k++)
            {
                int i = y * s.W + x;
                q.Elem[k] = s.Elem[i];
                q.Mass[k] = BitConverter.HalfToUInt16Bits((Half)s.Mass[i]);
                q.Temp[k] = (uint)Math.Round(Math.Max(0f, s.Temp[i]) / TempStep);
                q.DisIdx[k] = s.DisIdx[i];
                q.DisCnt[k] = s.DisCount[i] <= 0 ? (byte)0 : (byte)Math.Min(255, 1 + (int)Math.Round(Math.Log2(s.DisCount[i]) * 8));
            }
        return q;
    }

    public static float MassOf(ushort h) => (float)BitConverter.UInt16BitsToHalf(h);
}

static class Metrics
{
    /// <summary>visual: element differs or mass off by > 5 % (what you would see: liquid level, gas
    /// density, wrong gas). strict: element, mass > 0.5 %, temperature > 0.1 K, or germ type differs.</summary>
    public static (double visual, double strict) Diff(QState a, QState b)
    {
        int vis = 0, str = 0;
        for (int i = 0; i < a.N; i++)
        {
            if (a.Elem[i] != b.Elem[i]) { vis++; str++; continue; }
            float ma = Quant.MassOf(a.Mass[i]), mb = Quant.MassOf(b.Mass[i]);
            float rel = Math.Abs(ma - mb) / Math.Max(Math.Max(Math.Abs(ma), Math.Abs(mb)), 1e-6f);
            if (rel > 0.05f) vis++;
            if (rel > 0.005f || Math.Abs((long)a.Temp[i] - b.Temp[i]) * Quant.TempStep > 0.1f || a.DisIdx[i] != b.DisIdx[i]) str++;
        }
        return (vis / (double)a.N, str / (double)a.N);
    }
}

enum Comp { None, Deflate, Brotli }

static class Codec
{
    public const int Header = 12; // seq, tick, base tick

    /// <summary>Every cell of the viewport.</summary>
    public static byte[] Full(QState s)
    {
        var w = new BinaryWriter(new MemoryStream());
        foreach (var e in s.Elem) w.Write(e);
        foreach (var m in s.Mass) w.Write(m);
        foreach (var t in s.Temp) WriteVar(w, t);
        w.Write(s.DisIdx);
        w.Write(s.DisCnt);
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    /// <summary>Only cells whose quantized value changed between base and target:
    /// changed-cell bitmap, per-cell field mask, then the changed fields (temperature as a delta).</summary>
    public static byte[] Delta(QState b, QState t)
    {
        int n = t.N;
        var bitmap = new byte[(n + 7) / 8];
        var masks = new List<byte>();
        var elems = new BinaryWriter(new MemoryStream());
        var masses = new BinaryWriter(new MemoryStream());
        var temps = new BinaryWriter(new MemoryStream());
        var dis = new BinaryWriter(new MemoryStream());
        for (int i = 0; i < n; i++)
        {
            int m = (b.Elem[i] != t.Elem[i] ? 1 : 0) | (b.Mass[i] != t.Mass[i] ? 2 : 0)
                  | (b.Temp[i] != t.Temp[i] ? 4 : 0) | (b.DisIdx[i] != t.DisIdx[i] || b.DisCnt[i] != t.DisCnt[i] ? 8 : 0);
            if (m == 0) continue;
            bitmap[i >> 3] |= (byte)(1 << (i & 7));
            masks.Add((byte)m);
            if ((m & 1) != 0) elems.Write(t.Elem[i]);
            if ((m & 2) != 0) masses.Write(t.Mass[i]);
            if ((m & 4) != 0) WriteVar(temps, ZigZag((long)t.Temp[i] - b.Temp[i]));
            if ((m & 8) != 0) { dis.Write(t.DisIdx[i]); dis.Write(t.DisCnt[i]); }
        }
        var o = new BinaryWriter(new MemoryStream());
        o.Write(bitmap);
        for (int i = 0; i < masks.Count; i += 2)
            o.Write((byte)(masks[i] | (i + 1 < masks.Count ? masks[i + 1] << 4 : 0)));
        foreach (var part in new[] { elems, masses, temps, dis })
            o.Write(((MemoryStream)part.BaseStream).ToArray());
        return ((MemoryStream)o.BaseStream).ToArray();
    }

    public static int Size(byte[] raw, Comp c)
    {
        if (c == Comp.None) return raw.Length + Header;
        using var ms = new MemoryStream();
        if (c == Comp.Deflate)
        {
            using (var z = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        }
        else
        {
            using var enc = new BrotliEncoder(quality: 5, window: 22);
            var dst = new byte[BrotliEncoder.GetMaxCompressedLength(raw.Length)];
            enc.Compress(raw, dst, out _, out int written, isFinalBlock: true);
            return written + Header;
        }
        return (int)ms.Length + Header;
    }

    public static void WriteVar(BinaryWriter w, ulong v)
    {
        while (v >= 0x80) { w.Write((byte)(v | 0x80)); v >>= 7; }
        w.Write((byte)v);
    }

    public static void WriteVar(BinaryWriter w, uint v) => WriteVar(w, (ulong)v);
    private static ulong ZigZag(long v) => (ulong)((v << 1) ^ (v >> 63));
}

/// <summary>Duplicant/critter updates for one viewport: position (1/32 cell), anim and chore on change.</summary>
static class EntityCodec
{
    public static byte[] Delta(Dictionary<(char, int), Entity>? prev, Dictionary<(char, int), Entity>? cur, View v, int gridW)
    {
        var w = new BinaryWriter(new MemoryStream());
        if (cur == null) return [];
        foreach (var (key, e) in cur)
        {
            if (!v.Contains(e.Cell, gridW, margin: 4)) continue;
            Entity? p = null;
            prev?.TryGetValue(key, out p);
            bool newOne = p == null;
            int flags = (newOne || p!.X != e.X || p.Y != e.Y ? 1 : 0)
                      | (newOne || p!.Anim != e.Anim ? 2 : 0)
                      | (newOne || p!.Chore != e.Chore ? 4 : 0);
            if (flags == 0) continue;
            Codec.WriteVar(w, (uint)e.Id);
            w.Write((byte)flags);
            if ((flags & 1) != 0) { w.Write((ushort)Math.Round(e.X * 32)); w.Write((ushort)Math.Round(e.Y * 32)); }
            if ((flags & 2) != 0) w.Write(e.Anim.GetHashCode());
            if ((flags & 4) != 0) w.Write((ushort)e.Chore.GetHashCode());
        }
        return ((MemoryStream)w.BaseStream).ToArray();
    }
}
