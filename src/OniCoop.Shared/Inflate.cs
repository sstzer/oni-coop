namespace OniCoop.Net
{
    using System;
    using System.IO;

    /// <summary>Raw deflate (RFC 1951) decoder into a reused buffer, and one reused reader over it.
    ///
    /// Why: in the game's Mono, every System.IO.Compression.DeflateStream costs managed garbage, and reading small
    /// values through it (BinaryReader on top) costs more per read, so a guest decoding ~30 messages a second made
    /// hundreds of KB/s of garbage from decompression alone; each garbage collection freezes the whole game for
    /// 0.4-0.7 s (no incremental collector in this Unity). This decodes into one growing buffer and hands out one
    /// BinaryReader over it: nothing is allocated per message once the buffer is big enough.
    ///
    /// The decoder follows zlib's reference decoder "puff" (canonical Huffman, decoded bit by bit): small and simple;
    /// fast enough for these message sizes (well under a millisecond for tens of KB).
    ///
    /// Not reentrant: the reader is valid until the next Open. A message must be read completely before another one is
    /// opened (OpenCopy gives a private reader when that can't be guaranteed).</summary>
    public static class Inflate
    {
        private static byte[] output = new byte[64 * 1024];
        private static int outCnt;
        private static byte[] input;
        private static int inPos, inEnd, bitbuf, bitcnt;
        private static readonly SliceStream slice = new SliceStream();
        private static readonly BinaryReader reader = new SharedReader(slice);

        /// <summary>The shared reader survives "using" (callers written for a reader of their own).</summary>
        private sealed class SharedReader : BinaryReader
        {
            public SharedReader(Stream s) : base(s) { }
            protected override void Dispose(bool disposing) { }
        }
        public static long Messages, Bytes;

        /// <summary>A reader over the decompressed message (the shared one: valid until the next Open).</summary>
        public static BinaryReader Open(byte[] compressed)
        {
            int n = Run(compressed);
            slice.Reset(output, n);
            Messages++;
            Bytes += n;
            return reader;
        }

        /// <summary>A reader of its own (for code that may open another message before it is done).</summary>
        public static BinaryReader OpenCopy(byte[] compressed)
        {
            int n = Run(compressed);
            var copy = new byte[n];
            Buffer.BlockCopy(output, 0, copy, 0, n);
            return new BinaryReader(new MemoryStream(copy, false));
        }

        /// <summary>Whether the reader has bytes left (instead of reading until EndOfStreamException: exceptions are
        /// garbage too).</summary>
        public static bool More(BinaryReader r) => r.BaseStream.Position < r.BaseStream.Length;

        private static byte[] strBuf = new byte[256];
        private static readonly System.Collections.Generic.Dictionary<long, string> interned = new System.Collections.Generic.Dictionary<long, string>();
        public static long InternHits, InternMisses;

        /// <summary>A string as BinaryWriter.Write(string) wrote it, without a new string each time for short ones that
        /// come again and again (ids of status items, effects, amounts, anim files...): they are looked up by a hash of
        /// their bytes.</summary>
        public static string ReadId(BinaryReader r)
        {
            int len = 0, shift = 0;
            byte b;
            do
            {
                b = r.ReadByte();
                len |= (b & 0x7f) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0 && shift < 35);
            if (len == 0) return "";
            if (len > 160) return System.Text.Encoding.UTF8.GetString(r.ReadBytes(len)); // long text: rarely the same twice
            if (strBuf.Length < len) strBuf = new byte[Math.Max(len, strBuf.Length * 2)];
            int got = 0;
            while (got < len)
            {
                int n = r.Read(strBuf, got, len - got);
                if (n <= 0) throw new EndOfStreamException();
                got += n;
            }
            // FNV-1a, 64 bit, with the length: two different ids meeting is not a practical concern
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < len; i++) { h ^= strBuf[i]; h *= 1099511628211UL; }
            long key = (long)(h ^ ((ulong)len << 56));
            if (interned.TryGetValue(key, out string s)) { InternHits++; return s; }
            InternMisses++;
            if (interned.Count > 50000) interned.Clear(); // (numbers in texts could grow it without bound)
            s = System.Text.Encoding.UTF8.GetString(strBuf, 0, len);
            interned[key] = s;
            return s;
        }

        /// <summary>Decompress into the shared buffer; the decompressed length.</summary>
        public static int Run(byte[] data)
        {
            input = data; inPos = 0; inEnd = data.Length; bitbuf = 0; bitcnt = 0; outCnt = 0;
            if (inEnd == 0) return 0; // (a compressor that was given nothing may write nothing)
            int last;
            do
            {
                last = Bits(1);
                int type = Bits(2);
                if (type == 0) Stored();
                else if (type == 1) Fixed();
                else if (type == 2) Dynamic();
                else throw new InvalidDataException("inflate: bad block type");
            }
            while (last == 0);
            input = null;
            return outCnt;
        }

        /// <summary>The shared buffer (valid up to the length Run returned).</summary>
        public static byte[] Output => output;

        private static void Ensure(int n)
        {
            if (n <= output.Length) return;
            int size = output.Length;
            while (size < n) size *= 2;
            var bigger = new byte[size];
            Buffer.BlockCopy(output, 0, bigger, 0, outCnt);
            output = bigger;
        }

        private static int Bits(int need)
        {
            int val = bitbuf;
            while (bitcnt < need)
            {
                if (inPos >= inEnd) throw new InvalidDataException("inflate: out of input");
                val |= input[inPos++] << bitcnt;
                bitcnt += 8;
            }
            bitbuf = val >> need;
            bitcnt -= need;
            return val & ((1 << need) - 1);
        }

        private static void Stored()
        {
            bitbuf = 0; bitcnt = 0; // the rest of the current byte is padding
            if (inPos + 4 > inEnd) throw new InvalidDataException("inflate: out of input");
            int len = input[inPos] | (input[inPos + 1] << 8);
            int nlen = input[inPos + 2] | (input[inPos + 3] << 8);
            inPos += 4;
            if (len != (~nlen & 0xffff)) throw new InvalidDataException("inflate: bad stored block");
            if (inPos + len > inEnd) throw new InvalidDataException("inflate: out of input");
            Ensure(outCnt + len);
            Buffer.BlockCopy(input, inPos, output, outCnt, len);
            outCnt += len;
            inPos += len;
        }

        private sealed class Huffman
        {
            public readonly short[] count = new short[16];
            public readonly short[] symbol;
            public Huffman(int n) { symbol = new short[n]; }
        }

        private static readonly Huffman lencode = new Huffman(288), distcode = new Huffman(30);
        private static readonly Huffman fixedLen = new Huffman(288), fixedDist = new Huffman(30);
        private static bool fixedBuilt;
        private static readonly short[] lengths = new short[288 + 32];
        private static readonly short[] offs = new short[16];

        private static int Decode(Huffman h)
        {
            int code = 0, first = 0, index = 0;
            for (int len = 1; len <= 15; len++)
            {
                code |= Bits(1);
                int count = h.count[len];
                if (code - count < first) return h.symbol[index + (code - first)];
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw new InvalidDataException("inflate: bad code");
        }

        private static int Construct(Huffman h, short[] length, int off, int n)
        {
            for (int len = 0; len <= 15; len++) h.count[len] = 0;
            for (int sym = 0; sym < n; sym++) h.count[length[off + sym]]++;
            if (h.count[0] == n) return 0;
            int left = 1;
            for (int len = 1; len <= 15; len++)
            {
                left <<= 1;
                left -= h.count[len];
                if (left < 0) return left;
            }
            offs[1] = 0;
            for (int len = 1; len < 15; len++) offs[len + 1] = (short)(offs[len] + h.count[len]);
            for (int sym = 0; sym < n; sym++)
                if (length[off + sym] != 0) h.symbol[offs[length[off + sym]]++] = (short)sym;
            return left;
        }

        private static readonly short[] LBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
        private static readonly short[] LExt = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
        private static readonly short[] DBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
        private static readonly short[] DExt = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
        private static readonly short[] Order = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

        private static void Codes(Huffman lc, Huffman dc)
        {
            int symbol;
            do
            {
                symbol = Decode(lc);
                if (symbol < 256)
                {
                    if (outCnt >= output.Length) Ensure(outCnt + 1);
                    output[outCnt++] = (byte)symbol;
                }
                else if (symbol > 256)
                {
                    symbol -= 257;
                    if (symbol >= 29) throw new InvalidDataException("inflate: bad length");
                    int len = LBase[symbol] + Bits(LExt[symbol]);
                    int ds = Decode(dc);
                    if (ds >= 30) throw new InvalidDataException("inflate: bad distance");
                    int dist = DBase[ds] + Bits(DExt[ds]);
                    if (dist > outCnt) throw new InvalidDataException("inflate: distance too far back");
                    Ensure(outCnt + len);
                    byte[] o = output;
                    for (int i = 0; i < len; i++) { o[outCnt] = o[outCnt - dist]; outCnt++; }
                }
            }
            while (symbol != 256);
        }

        private static void Fixed()
        {
            if (!fixedBuilt)
            {
                int sym = 0;
                for (; sym < 144; sym++) lengths[sym] = 8;
                for (; sym < 256; sym++) lengths[sym] = 9;
                for (; sym < 280; sym++) lengths[sym] = 7;
                for (; sym < 288; sym++) lengths[sym] = 8;
                Construct(fixedLen, lengths, 0, 288);
                for (sym = 0; sym < 30; sym++) lengths[sym] = 5;
                Construct(fixedDist, lengths, 0, 30);
                fixedBuilt = true;
            }
            Codes(fixedLen, fixedDist);
        }

        private static void Dynamic()
        {
            int nlen = Bits(5) + 257, ndist = Bits(5) + 1, ncode = Bits(4) + 4;
            if (nlen > 286 || ndist > 30) throw new InvalidDataException("inflate: bad counts");
            int index;
            for (index = 0; index < ncode; index++) lengths[Order[index]] = (short)Bits(3);
            for (; index < 19; index++) lengths[Order[index]] = 0;
            if (Construct(lencode, lengths, 0, 19) != 0) throw new InvalidDataException("inflate: bad code lengths");
            index = 0;
            while (index < nlen + ndist)
            {
                int symbol = Decode(lencode);
                if (symbol < 16) lengths[index++] = (short)symbol;
                else
                {
                    int len = 0;
                    if (symbol == 16)
                    {
                        if (index == 0) throw new InvalidDataException("inflate: repeat with no first length");
                        len = lengths[index - 1];
                        symbol = 3 + Bits(2);
                    }
                    else if (symbol == 17) symbol = 3 + Bits(3);
                    else symbol = 11 + Bits(7);
                    if (index + symbol > nlen + ndist) throw new InvalidDataException("inflate: too many lengths");
                    while (symbol-- > 0) lengths[index++] = (short)len;
                }
            }
            if (lengths[256] == 0) throw new InvalidDataException("inflate: no end-of-block code");
            int err = Construct(lencode, lengths, 0, nlen);
            if (err < 0 || (err > 0 && nlen - lencode.count[0] != 1)) throw new InvalidDataException("inflate: bad literal/length code");
            err = Construct(distcode, lengths, nlen, ndist);
            if (err < 0 || (err > 0 && ndist - distcode.count[0] != 1)) throw new InvalidDataException("inflate: bad distance code");
            Codes(lencode, distcode);
        }

        /// <summary>A read-only stream over part of a buffer that can be pointed at new contents.</summary>
        private sealed class SliceStream : Stream
        {
            private byte[] buf = new byte[0];
            private int len, pos;

            public void Reset(byte[] b, int l) { buf = b; len = l; pos = 0; }

            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => len;
            public override long Position { get => pos; set => pos = (int)Math.Max(0, Math.Min(len, value)); }
            public override void Flush() { }

            public override int Read(byte[] dst, int offset, int count)
            {
                int n = Math.Min(count, len - pos);
                if (n <= 0) return 0;
                Buffer.BlockCopy(buf, pos, dst, offset, n);
                pos += n;
                return n;
            }

            public override int ReadByte() => pos < len ? buf[pos++] : -1;

            public override long Seek(long offset, SeekOrigin origin)
            {
                long p = origin == SeekOrigin.Begin ? offset : origin == SeekOrigin.Current ? pos + offset : len + offset;
                Position = p;
                return pos;
            }

            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { } // shared: never closed
        }
    }
}
