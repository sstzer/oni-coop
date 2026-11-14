using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

namespace DivergenceProbe
{
    /// <summary>
    /// Writes, for every real 200 ms sim tick of one run:
    ///   ticks.bin     fixed-size record: counters, RNG state hash, per-field grid hashes, entity hashes,
    ///                 one hash per 32x32 chunk (format: see tools/DivergenceAnalyzer)
    ///   entities.tsv  every duplicant and critter: cell, position, current chore, current anim
    ///   snap_*.bin.gz full grid (element, mass, temperature, disease) at tick -1, every N ticks, last tick
    ///   elements.tsv  element index -> id, meta.json at the end
    /// </summary>
    internal sealed unsafe class Recorder
    {
        public static Recorder Active;

        public const int ChunkSize = 32;
        private const ulong P = 1099511628211UL;

        public readonly string Dir;
        public int Ticks;        // real ticks recorded so far (record index = Ticks, pre-state = -1)
        public int ZeroDtCalls;  // UnsafeSim200ms(0) calls since the previous record
        public string Error;

        private readonly int run;
        private readonly string variant;
        private readonly ProbeConfig cfg;
        private readonly int w, h, cw, ch, nChunks;
        private readonly ulong[] chunk;
        private BinaryWriter ticksOut;
        private StreamWriter entOut;
        private readonly List<Task> pending = new List<Task>();
        private int startFrame;
        private float startTime;
        private int snapshots;

        public Recorder(string dir, int run, string variant, ProbeConfig cfg)
        {
            Dir = dir;
            this.run = run;
            this.variant = variant;
            this.cfg = cfg;
            w = Grid.WidthInCells;
            h = Grid.HeightInCells;
            cw = (w + ChunkSize - 1) / ChunkSize;
            ch = (h + ChunkSize - 1) / ChunkSize;
            nChunks = cw * ch;
            chunk = new ulong[nChunks];
        }

        public void Begin()
        {
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true); // a crashed earlier attempt of this run
            Directory.CreateDirectory(Dir);

            var sb = new StringBuilder();
            for (int i = 0; i < ElementLoader.elements.Count; i++)
            {
                Element e = ElementLoader.elements[i];
                sb.Append(i).Append('\t').Append(e.id).Append('\t').Append(e.state).Append('\n');
            }
            File.WriteAllText(Path.Combine(Dir, "elements.tsv"), sb.ToString());

            ticksOut = new BinaryWriter(new BufferedStream(File.Create(Path.Combine(Dir, "ticks.bin")), 1 << 16));
            ticksOut.Write(0x31545044); // "DPT1"
            ticksOut.Write(w);
            ticksOut.Write(h);
            ticksOut.Write(ChunkSize);
            ticksOut.Write(nChunks);

            entOut = new StreamWriter(Path.Combine(Dir, "entities.tsv"), false, new UTF8Encoding(false), 1 << 16);
            entOut.Write("tick\tkind\tid\tname\tcell\tx\ty\tchore\tanim\n");

            startFrame = Time.frameCount;
            startTime = Time.realtimeSinceStartup;
            Record(-1);
        }

        public void OnTick()
        {
            try
            {
                Record(Ticks);
                Ticks++;
            }
            catch (Exception e)
            {
                Error = "recording failed: " + e;
                Active = null;
            }
        }

        private void Record(int tick)
        {
            int n = Grid.CellCount;
            ushort* el = Grid.elementIdx;
            uint* ms = (uint*)Grid.mass;
            uint* tp = (uint*)Grid.temperature;
            byte* di = Grid.diseaseIdx;
            int* dc = Grid.diseaseCount;

            ulong hE = 14695981039346656037UL, hM = hE, hT = hE, hD = hE;
            Array.Clear(chunk, 0, nChunks);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int cRow = (y / ChunkSize) * cw;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x;
                    ulong e = el[i], m = ms[i], t = tp[i], d = di[i] | ((ulong)(uint)dc[i] << 8);
                    hE = (hE ^ e) * P;
                    hM = (hM ^ m) * P;
                    hT = (hT ^ t) * P;
                    hD = (hD ^ d) * P;
                    int c = cRow + x / ChunkSize;
                    chunk[c] = (chunk[c] ^ Mix(e | (m << 16) ^ (t << 40) ^ (d << 7))) * P;
                }
            }

            UnityEngine.Random.State rs = UnityEngine.Random.state;
            ulong* rp = (ulong*)&rs;
            ulong rng = Mix(rp[0]) ^ Mix(rp[1] + 0x9E3779B97F4A7C15UL);

            ulong hDupes = HashEntities(tick, out int nDupes, out ulong hCritters, out int nCritters);
            ulong hItems = HashItems(out int nItems);

            ticksOut.Write(tick);
            ticksOut.Write(Time.frameCount - startFrame);
            ticksOut.Write(ZeroDtCalls);
            ticksOut.Write(Time.realtimeSinceStartup - startTime);
            ticksOut.Write(rng);
            ticksOut.Write(hE);
            ticksOut.Write(hM);
            ticksOut.Write(hT);
            ticksOut.Write(hD);
            ticksOut.Write(hDupes);
            ticksOut.Write(nDupes);
            ticksOut.Write(hCritters);
            ticksOut.Write(nCritters);
            ticksOut.Write(hItems);
            ticksOut.Write(nItems);
            for (int c = 0; c < nChunks; c++)
                ticksOut.Write(chunk[c]);
            ZeroDtCalls = 0;

            if (tick < 0 || tick % Math.Max(1, cfg.snapshotEvery) == 0 || tick == cfg.ticksPerRun - 1)
                Snapshot(tick, n);
        }

        private ulong HashEntities(int tick, out int nDupes, out ulong hCritters, out int nCritters)
        {
            ulong hd = 14695981039346656037UL, hc = hd;
            nDupes = 0;
            nCritters = 0;
            foreach (MinionIdentity mi in Components.LiveMinionIdentities.Items)
            {
                if (mi == null) continue;
                Vector3 p = mi.transform.GetPosition();
                int cell = Grid.PosToCell(p);
                int id = mi.GetComponent<KPrefabID>().InstanceID;
                Chore chore = mi.GetComponent<ChoreDriver>()?.GetCurrentChore();
                string choreId = chore?.choreType?.Id ?? "-";
                string anim = mi.GetComponent<KBatchedAnimController>()?.GetCurrentAnim()?.name ?? "-";
                hd = (hd ^ Mix((ulong)(uint)id ^ ((ulong)(uint)cell << 32))) * P;
                hd = (hd ^ (ulong)choreId.GetHashCode()) * P;
                Line(tick, 'D', id, mi.GetProperName(), cell, p, choreId, anim);
                nDupes++;
            }
            foreach (Brain b in Components.Brains.Items)
            {
                if (b == null || b.GetComponent<MinionIdentity>() != null) continue;
                Vector3 p = b.transform.GetPosition();
                int cell = Grid.PosToCell(p);
                KPrefabID kp = b.GetComponent<KPrefabID>();
                int id = kp.InstanceID;
                string anim = b.GetComponent<KBatchedAnimController>()?.GetCurrentAnim()?.name ?? "-";
                hc = (hc ^ Mix((ulong)(uint)id ^ ((ulong)(uint)cell << 32))) * P;
                Line(tick, 'C', id, kp.PrefabTag.Name, cell, p, "-", anim);
                nCritters++;
            }
            hCritters = hc;
            return hd;
        }

        private static ulong HashItems(out int count)
        {
            ulong h = 14695981039346656037UL;
            count = 0;
            foreach (Pickupable pk in Components.Pickupables.Items)
            {
                if (pk == null) continue;
                KPrefabID kp = pk.KPrefabID;
                int id = kp != null ? kp.InstanceID : 0;
                float mass = pk.PrimaryElement != null ? pk.PrimaryElement.Mass : 0f;
                h = (h ^ Mix((ulong)(uint)id ^ ((ulong)(uint)pk.cachedCell << 32))) * P;
                h = (h ^ *(uint*)&mass) * P;
                count++;
            }
            return h;
        }

        private void Line(int tick, char kind, int id, string name, int cell, Vector3 p, string chore, string anim)
        {
            entOut.Write(tick.ToString(CultureInfo.InvariantCulture));
            entOut.Write('\t'); entOut.Write(kind);
            entOut.Write('\t'); entOut.Write(id.ToString(CultureInfo.InvariantCulture));
            entOut.Write('\t'); entOut.Write(name);
            entOut.Write('\t'); entOut.Write(cell.ToString(CultureInfo.InvariantCulture));
            entOut.Write('\t'); entOut.Write(p.x.ToString("R", CultureInfo.InvariantCulture));
            entOut.Write('\t'); entOut.Write(p.y.ToString("R", CultureInfo.InvariantCulture));
            entOut.Write('\t'); entOut.Write(chore);
            entOut.Write('\t'); entOut.Write(anim);
            entOut.Write('\n');
        }

        private void Snapshot(int tick, int n)
        {
            // Copy on the main thread (the sim owns these buffers), compress on a worker.
            var bytes = new byte[n * 15];
            fixed (byte* dst = bytes)
            {
                Buffer.MemoryCopy(Grid.elementIdx, dst, n * 2, n * 2);
                Buffer.MemoryCopy(Grid.mass, dst + n * 2, n * 4, n * 4);
                Buffer.MemoryCopy(Grid.temperature, dst + n * 6, n * 4, n * 4);
                Buffer.MemoryCopy(Grid.diseaseIdx, dst + n * 10, n, n);
                Buffer.MemoryCopy(Grid.diseaseCount, dst + n * 11, n * 4, n * 4);
            }
            string file = Path.Combine(Dir, tick < 0 ? "snap_pre.bin.gz" : $"snap_{tick:D4}.bin.gz");
            int ww = w, hh = h;
            snapshots++;
            pending.Add(Task.Run(() =>
            {
                using (var fs = File.Create(file))
                using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest))
                using (var bw = new BinaryWriter(gz))
                {
                    bw.Write(0x31535044); // "DPS1"
                    bw.Write(ww);
                    bw.Write(hh);
                    bw.Write(tick);
                    bw.Write(bytes);
                }
            }));
        }

        public Task Finish(int unexpectedPauses)
        {
            int frames = Time.frameCount - startFrame;
            float secs = Time.realtimeSinceStartup - startTime;
            try
            {
                ticksOut?.Flush();
                ticksOut?.Close();
                entOut?.Flush();
                entOut?.Close();
            }
            catch (Exception e)
            {
                Error = Error ?? ("closing files failed: " + e.Message);
            }
            string meta =
                "{\n" +
                $"  \"run\": {run},\n" +
                $"  \"variant\": \"{variant}\",\n" +
                $"  \"seed\": {cfg.seed},\n" +
                $"  \"ticks\": {Ticks},\n" +
                $"  \"frames\": {frames},\n" +
                $"  \"seconds\": {secs.ToString("F2", CultureInfo.InvariantCulture)},\n" +
                $"  \"unexpectedPauses\": {unexpectedPauses},\n" +
                $"  \"snapshots\": {snapshots},\n" +
                $"  \"grid\": [{w}, {h}],\n" +
                $"  \"unity\": \"{Application.unityVersion}\",\n" +
                $"  \"savePath\": \"{cfg.savePath.Replace("\\", "/")}\",\n" +
                $"  \"error\": {(Error == null ? "null" : "\"" + Error.Replace("\\", "/").Replace("\"", "'").Replace("\n", " | ") + "\"")}\n" +
                "}\n";
            File.WriteAllText(Path.Combine(Dir, "meta.json"), meta);
            return Task.WhenAll(pending);
        }

        private static ulong Mix(ulong x)
        {
            x ^= x >> 33;
            x *= 0xff51afd7ed558ccdUL;
            x ^= x >> 33;
            x *= 0xc4ceb9fe1a85ec53UL;
            x ^= x >> 33;
            return x;
        }
    }
}
