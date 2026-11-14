using System;
using System.Globalization;

namespace MultiViewExperiment
{
    /// <summary>Cheap per-image numbers so the log alone says roughly what each PNG contains.</summary>
    internal struct ImageStats
    {
        public float meanLuma;     // 0..255
        public float blackFrac;    // luma < 12
        public float magentaFrac;  // the extra camera's clear colour: nothing was drawn there
        public float dominantFrac; // share of the most common 4-bit-per-channel colour (flat = missing content)
        public float magentaLeft;  // magentaFrac of the left / right half (useful for the "edge" shots)
        public float magentaRight;

        public static ImageStats Compute(byte[] rgba, int w, int h)
        {
            var hist = new int[4096];
            double luma = 0;
            long black = 0, magenta = 0, magL = 0, magR = 0;
            int half = w / 2;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;
                    int r = rgba[i], g = rgba[i + 1], b = rgba[i + 2];
                    double l = 0.299 * r + 0.587 * g + 0.114 * b;
                    luma += l;
                    if (l < 12) black++;
                    if (r > 235 && g < 20 && b > 235)
                    {
                        magenta++;
                        if (x < half) magL++; else magR++;
                    }
                    hist[((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4)]++;
                }
            }
            int max = 0;
            foreach (int c in hist) if (c > max) max = c;
            float n = w * (float)h, nh = half * (float)h, nr = (w - half) * (float)h;
            return new ImageStats
            {
                meanLuma = (float)(luma / n),
                blackFrac = black / n,
                magentaFrac = magenta / n,
                dominantFrac = max / n,
                magentaLeft = magL / nh,
                magentaRight = magR / nr,
            };
        }

        /// <summary>Fraction of pixels whose largest channel difference exceeds 24, and mean abs diff.</summary>
        public static void Diff(byte[] a, byte[] b, out float changedFrac, out float meanAbsDiff)
        {
            int len = Math.Min(a.Length, b.Length);
            long changed = 0, sum = 0;
            for (int i = 0; i + 3 < len; i += 4)
            {
                int dr = Math.Abs(a[i] - b[i]), dg = Math.Abs(a[i + 1] - b[i + 1]), db = Math.Abs(a[i + 2] - b[i + 2]);
                int m = Math.Max(dr, Math.Max(dg, db));
                if (m > 24) changed++;
                sum += dr + dg + db;
            }
            float px = len / 4f;
            changedFrac = changed / px;
            meanAbsDiff = sum / (px * 3f);
        }

        public override string ToString() => string.Format(CultureInfo.InvariantCulture,
            "luma={0,6:F1} black={1,6:P1} magenta={2,6:P1} (L {4:P0} / R {5:P0}) dominant={3,6:P1}",
            meanLuma, blackFrac, magentaFrac, dominantFrac, magentaLeft, magentaRight);
    }
}
