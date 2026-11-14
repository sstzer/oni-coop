// Shared between the ONI mod (net472 / Unity Mono) and the relay server (net10.0).
// Keep this file free of Unity, game and modern-runtime-only APIs.

namespace OniCoop.Net
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text;

    public static class Proto
    {
        /// <summary>Bump on any wire-format change; relay and every client must match.</summary>
        public const ushort Version = 1;
        public const int DefaultPort = 47810;

        /// <summary>Largest TCP frame (type byte + payload).</summary>
        public const int MaxFrame = 1 << 20;
        public const int SaveChunk = 192 * 1024;
        /// <summary>Unreliable packets are kept under a typical path MTU (no IP fragmentation).</summary>
        public const int MaxDatagram = 1200;

        public const ushort TargetHost = 0;
        public const ushort TargetAll = 0xFFFF;
    }

    public enum Msg : byte
    {
        // ---- TCP (reliable, ordered)
        Hello = 1,          // C→S  HelloMsg
        Welcome = 2,        // S→C  WelcomeMsg
        Reject = 3,         // S→C  RejectMsg, then close
        Members = 4,        // S→C  MemberInfo[] (full list, sent on every change)
        State = 5,          // C→S  StateMsg
        Notice = 6,         // S→C  string
        SaveRequest = 7,    // S→host  u32 requestId
        SaveBegin = 8,      // host→S, S→guest  SaveBeginMsg
        SaveChunk = 9,      // u32 requestId + raw bytes
        SaveEnd = 10,       // u32 requestId
        SaveFailed = 11,    // host→S  u32 requestId + string reason
        Relay = 12,         // C→S  u16 target + u8 channel + bytes      (generic forwarding for later steps)
        RelayFrom = 13,     // S→C  u16 from + u8 channel + bytes
        UnreliableOverTcp = 14, // either way: an unreliable packet carried on TCP because UDP is not working
        Bye = 15,           // either way: string reason

        // ---- UDP (unreliable). C→S datagrams: [type][u16 id][u64 key][payload]; S→C: [type][payload]
        UdpHello = 40,
        UdpAck = 41,
        Ping = 42,          // u32 seq + i64 clientMs
        Pong = 43,          // echo
        Presence = 44,      // C→S PresenceData; S→C u16 from + PresenceData
    }

    public enum Role : byte { Guest = 0, Host = 1 }

    /// <summary>Where a member is. Ordered: later = further along.</summary>
    public enum MemberState : byte { Lobby = 0, WaitingForWorld = 1, Downloading = 2, Loading = 3, InGame = 4 }

    public enum RejectCode : byte
    {
        BadToken = 1, Protocol = 2, Incompatible = 3, RoomHasHost = 4, Full = 5, BadRequest = 6, ServerError = 7,
    }

    // ===================================================================== binary helpers

    public static class Wire
    {
        public static void WriteStrings(this BinaryWriter w, IList<string> list)
        {
            int n = list == null ? 0 : list.Count;
            w.Write((ushort)n);
            for (int i = 0; i < n; i++) w.Write(list[i] ?? "");
        }

        public static List<string> ReadStrings(this BinaryReader r)
        {
            int n = r.ReadUInt16();
            var list = new List<string>(n);
            for (int i = 0; i < n; i++) list.Add(r.ReadString());
            return list;
        }

        public static byte[] Build(Action<BinaryWriter> body)
        {
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms, Encoding.UTF8))
            {
                body(w);
                w.Flush();
                return ms.ToArray();
            }
        }

        public static BinaryReader Reader(byte[] data, int offset = 0) =>
            new BinaryReader(new MemoryStream(data, offset, data.Length - offset, false), Encoding.UTF8);

        /// <summary>TCP frame: [i32 length of (type + payload)][u8 type][payload].</summary>
        public static byte[] Frame(Msg type, byte[] payload, int offset = 0, int count = -1)
        {
            if (count < 0) count = payload == null ? 0 : payload.Length - offset;
            if (count + 1 > Proto.MaxFrame) throw new InvalidOperationException($"frame too large: {count + 1}");
            var f = new byte[5 + count];
            WriteI32(f, 0, count + 1);
            f[4] = (byte)type;
            if (count > 0) Buffer.BlockCopy(payload, offset, f, 5, count);
            return f;
        }

        public static void WriteI32(byte[] b, int at, int v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }

        public static int ReadI32(byte[] b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        /// <summary>Reads exactly n bytes or returns false at a clean end of stream.</summary>
        public static bool ReadExactly(Stream s, byte[] buf, int n)
        {
            int got = 0;
            while (got < n)
            {
                int k = s.Read(buf, got, n - got);
                if (k <= 0)
                {
                    if (got == 0) return false;
                    throw new EndOfStreamException("connection closed mid-frame");
                }
                got += k;
            }
            return true;
        }

        /// <summary>Blocking frame read. Returns null at end of stream.</summary>
        public static byte[] ReadFrame(Stream s, byte[] header4, out Msg type)
        {
            type = 0;
            if (!ReadExactly(s, header4, 4)) return null;
            int len = ReadI32(header4, 0);
            if (len < 1 || len > Proto.MaxFrame) throw new InvalidDataException($"bad frame length {len}");
            var body = new byte[len];
            if (!ReadExactly(s, body, len)) throw new EndOfStreamException("connection closed mid-frame");
            type = (Msg)body[0];
            var payload = new byte[len - 1];
            Buffer.BlockCopy(body, 1, payload, 0, len - 1);
            return payload;
        }

        public static string Hex(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static bool FixedTimeEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            byte[] x = Encoding.UTF8.GetBytes(a), y = Encoding.UTF8.GetBytes(b);
            int diff = x.Length ^ y.Length;
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) diff |= x[i] ^ y[i];
            return diff == 0;
        }
    }
}
