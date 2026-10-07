using System.Drawing;

namespace FlaUiPoc
{
    /// <summary>
    /// Tiny perceptual-hash utilities shared by the screenshot machinery. A 16x16 grayscale
    /// average-hash (aHash) folded to 64 bits answers "is this basically the same screen?" - cheap,
    /// robust to cursor/caret flicker. Used by BurstAlbum to drop near-identical frames so a flurry
    /// of inputs becomes a handful of meaningful frames, not 30 repetitive ones. (FrameGuard keeps its
    /// own copy tuned for stuck-detection; this is the reusable version for grouping.)
    /// </summary>
    public static class Imaging
    {
        /// <summary>aHash of a bitmap. Two hashes are "the same screen" when Hamming() is small.</summary>
        public static ulong AverageHash(Bitmap src)
        {
            const int N = 16;                       // 256 cells -> 64-bit sample
            using (var small = new Bitmap(N, N))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, N, N);
                }
                var vals = new int[N * N];
                long sum = 0;
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        var p = small.GetPixel(x, y);
                        int lum = (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                        vals[y * N + x] = lum; sum += lum;
                    }
                int mean = (int)(sum / vals.Length);
                ulong hash = 0; int bit = 0;
                for (int i = 0; i < vals.Length && bit < 64; i += 4, bit++)
                    if (vals[i] >= mean) hash |= (1UL << bit);
                return hash;
            }
        }

        /// <summary>Distance between two aHashes, scaled to the 0..256 cell space (so thresholds read
        /// the same as FrameGuard.ChangeThreshold).</summary>
        public static int Hamming(ulong a, ulong b)
        {
            ulong x = a ^ b; int c = 0;
            while (x != 0) { c++; x &= (x - 1); }
            return c * 4;
        }

        /// <summary>Keep only the top <paramref name="pixels"/> rows of an image (drop everything below).
        /// Used to crop Exatouch's on-screen keyboard WINDOW out of a frame at its exact top edge.</summary>
        public static Bitmap CropTopPx(Bitmap src, int pixels)
        {
            if (pixels <= 0 || pixels >= src.Height) return new Bitmap(src);
            var dst = new Bitmap(src.Width, pixels);
            using (var g = Graphics.FromImage(dst))
                g.DrawImage(src, new Rectangle(0, 0, src.Width, pixels), new Rectangle(0, 0, src.Width, pixels), GraphicsUnit.Pixel);
            return dst;
        }

        /// <summary>Keep only the TOP <paramref name="keepFraction"/> of an image (drop the bottom band).
        /// Used to crop Exatouch's on-screen keyboard out of a frame when it's detected.</summary>
        public static Bitmap CropTop(Bitmap src, double keepFraction)
        {
            int h = (int)(src.Height * keepFraction);
            if (h <= 0 || h >= src.Height) return new Bitmap(src);
            var dst = new Bitmap(src.Width, h);
            using (var g = Graphics.FromImage(dst))
                g.DrawImage(src, new Rectangle(0, 0, src.Width, h), new Rectangle(0, 0, src.Width, h), GraphicsUnit.Pixel);
            return dst;
        }
    }
}
