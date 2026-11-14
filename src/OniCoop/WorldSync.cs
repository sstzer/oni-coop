namespace OniCoop
{
    using System.Collections.Generic;
    using System.IO;
    using System.IO.Compression;
    using OniCoop.Net;
    using UnityEngine;

    /// <summary>Roadmap step 3b: the cells a guest is looking at follow the host's world.
    ///
    /// Host: twice a second, for each guest, the cells in (and a little around) that guest's view whose element,
    /// mass, temperature or germs changed by more than a dead band since what this guest was last sent (2 % mass,
    /// 0.5 K) go out, deflated, over the reliable relay channel (so "last sent" = what the guest has).
    /// Guest: applies them, and since its own simulation keeps moving gas around in between, it pins the cells on
    /// its screen back to the host's values twice a second. Building tiles (foundation cells) are left alone; they
    /// belong to building sync (step 3c).</summary>
    internal static class WorldSync
    {
        public const byte ChCells = 6;
        private const float SendInterval = 0.5f;
        private const int Margin = 4, MaxCellsPerMessage = 12000;
        private const float MassBand = 0.02f, TempBand = 0.5f;

        private sealed class CellArrays
        {
            public readonly ushort[] el; public readonly float[] mass, temp; public readonly byte[] dIdx; public readonly int[] dCnt; public readonly bool[] known;
            public CellArrays(int n) { el = new ushort[n]; mass = new float[n]; temp = new float[n]; dIdx = new byte[n]; dCnt = new int[n]; known = new bool[n]; }
        }

        private static bool Differs(ushort el0, float m0, float t0, byte d0, int c0, ushort el1, float m1, float t1, byte d1, int c1)
        {
            if (el0 != el1 || d0 != d1) return true;
            if (Mathf.Abs(m0 - m1) > Mathf.Max(MassBand * Mathf.Max(m0, m1), 0.0005f)) return true;
            if (Mathf.Abs(t0 - t1) > TempBand) return true;
            return Mathf.Abs(c0 - c1) > Mathf.Max(100, 0.1f * Mathf.Max(c0, c1));
        }

        private static void ViewCells(float minX, float minY, float maxX, float maxY, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = Mathf.Max(0, Mathf.FloorToInt(minX) - Margin);
            y0 = Mathf.Max(0, Mathf.FloorToInt(minY) - Margin);
            x1 = Mathf.Min(Grid.WidthInCells - 1, Mathf.CeilToInt(maxX) + Margin);
            y1 = Mathf.Min(Grid.HeightInCells - 1, Mathf.CeilToInt(maxY) + Margin);
        }

        // ------------------------------------------------------------------ host

        private sealed class GuestState { public CellArrays sent; public float nextSend; }

        private static readonly Dictionary<ushort, GuestState> guests = new Dictionary<ushort, GuestState>();
        public static long HostCellsSent, HostBytesSent;

        /// <summary>A guest (re)loaded the world: it has the host's save, not what we streamed before.</summary>
        public static void ResetGuest(ushort id) => guests.Remove(id);

        public static void HostTick(float now, ushort guestId, PresenceData view)
        {
            if (view == null || (view.flags & PresenceData.FlagInGame) == 0) return;
            if (!guests.TryGetValue(guestId, out GuestState g)) guests[guestId] = g = new GuestState { sent = new CellArrays(Grid.CellCount) };
            if (now < g.nextSend) return;
            g.nextSend = now + SendInterval;

            ViewCells(view.viewMinX, view.viewMinY, view.viewMaxX, view.viewMaxY, out int x0, out int y0, out int x1, out int y1);
            CellArrays s = g.sent;
            var ms = new MemoryStream();
            int count = 0;
            using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
            using (var w = new BinaryWriter(z))
            {
                int prev = 0;
                for (int y = y0; y <= y1 && count < MaxCellsPerMessage; y++)
                    for (int x = x0; x <= x1 && count < MaxCellsPerMessage; x++)
                    {
                        int c = Grid.XYToCell(x, y);
                        if (!Grid.IsValidCell(c) || Grid.Foundation[c]) continue;
                        ushort el = Grid.ElementIdx[c];
                        float m = Grid.Mass[c], t = Grid.Temperature[c];
                        byte d = Grid.DiseaseIdx[c];
                        int dc = Grid.DiseaseCount[c];
                        if (s.known[c] && !Differs(s.el[c], s.mass[c], s.temp[c], s.dIdx[c], s.dCnt[c], el, m, t, d, dc)) continue;
                        s.known[c] = true; s.el[c] = el; s.mass[c] = m; s.temp[c] = t; s.dIdx[c] = d; s.dCnt[c] = dc;
                        WriteVarint(w, c - prev);
                        prev = c;
                        w.Write(el); w.Write(m); w.Write(t); w.Write(d);
                        if (d != byte.MaxValue) w.Write(dc);
                        count++;
                    }
            }
            if (count == 0) return;
            byte[] payload = ms.ToArray();
            HostCellsSent += count;
            HostBytesSent += payload.Length;
            CommandBus.Client.SendRelay(guestId, ChCells, payload);
        }

        private static void WriteVarint(BinaryWriter w, int v)
        {
            uint u = (uint)v;
            while (u >= 0x80) { w.Write((byte)(u | 0x80)); u >>= 7; }
            w.Write((byte)u);
        }

        private static int ReadVarint(BinaryReader r)
        {
            uint u = 0;
            for (int shift = 0; ; shift += 7)
            {
                byte b = r.ReadByte();
                u |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return (int)u;
            }
        }

        // ------------------------------------------------------------------ guest

        private static CellArrays host;
        private static float nextPin;
        public static long GuestCellsReceived, GuestCellsApplied, GuestCellsPinned;

        public static void Reset() => host = null;

        private static void Apply(int c)
        {
            SimMessages.ModifyCell(c, host.el[c], host.temp[c], host.mass[c], host.dIdx[c], host.dCnt[c], SimMessages.ReplaceType.Replace);
        }

        private static bool LocalDiffers(int c) =>
            Differs(host.el[c], host.mass[c], host.temp[c], host.dIdx[c], host.dCnt[c],
                    Grid.ElementIdx[c], Grid.Mass[c], Grid.Temperature[c], Grid.DiseaseIdx[c], Grid.DiseaseCount[c]);

        public static void OnCells(byte[] data)
        {
            if (host == null) host = new CellArrays(Grid.CellCount);
            using (var r = Inflate.Open(data)) // (the shared reader: no garbage per message; using is harmless)
            {
                int c = 0;
                while (true)
                {
                    int delta;
                    if (!Inflate.More(r)) break;
                    delta = ReadVarint(r);
                    c += delta;
                    ushort el = r.ReadUInt16(); float m = r.ReadSingle(), t = r.ReadSingle(); byte d = r.ReadByte();
                    int dc = d != byte.MaxValue ? r.ReadInt32() : 0;
                    GuestCellsReceived++;
                    if (!Grid.IsValidCell(c)) continue;
                    host.known[c] = true; host.el[c] = el; host.mass[c] = m; host.temp[c] = t; host.dIdx[c] = d; host.dCnt[c] = dc;
                    if (Grid.Foundation[c] || !LocalDiffers(c)) continue;
                    Apply(c);
                    GuestCellsApplied++;
                }
            }
        }

        /// <summary>The guest's own simulation drifts the cells between updates: put the visible ones back.</summary>
        public static void GuestTick(float now)
        {
            if (host == null || now < nextPin) return;
            nextPin = now + SendInterval;
            Camera cam = Camera.main;
            if (cam == null) return;
            float z = cam.transform.position.z;
            Vector3 lo = cam.ViewportToWorldPoint(new Vector3(0f, 0f, z)), hi = cam.ViewportToWorldPoint(new Vector3(1f, 1f, z));
            ViewCells(lo.x, lo.y, hi.x, hi.y, out int x0, out int y0, out int x1, out int y1);
            int pinned = 0;
            for (int y = y0; y <= y1 && pinned < 4000; y++)
                for (int x = x0; x <= x1 && pinned < 4000; x++)
                {
                    int c = Grid.XYToCell(x, y);
                    if (!Grid.IsValidCell(c) || !host.known[c] || Grid.Foundation[c] || !LocalDiffers(c)) continue;
                    Apply(c);
                    pinned++;
                }
            GuestCellsPinned += pinned;
        }
    }
}
