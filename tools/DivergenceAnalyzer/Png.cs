using System.IO.Compression;

namespace DivergenceAnalyzer;

/// <summary>Minimal RGB PNG writer (no external packages).</summary>
static class Png
{
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <param name="rgb">width*height*3 bytes, top row first.</param>
    public static void Write(string file, int width, int height, byte[] rgb)
    {
        using var fs = File.Create(file);
        fs.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        var ihdr = new byte[13];
        BE(ihdr, 0, (uint)width); BE(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(fs, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgb, y * width * 3, width * 3);
            }
        }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; BE(len, 0, (uint)data.Length); s.Write(len);
        var td = new byte[4 + data.Length];
        for (int i = 0; i < 4; i++) td[i] = (byte)type[i];
        Buffer.BlockCopy(data, 0, td, 4, data.Length);
        s.Write(td);
        uint c = 0xFFFFFFFFu;
        foreach (byte b in td) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        var crc = new byte[4]; BE(crc, 0, c ^ 0xFFFFFFFFu); s.Write(crc);
    }

    private static void BE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
}
