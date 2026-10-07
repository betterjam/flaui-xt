using System.Drawing;

namespace FlaUiPoc
{
    /// <summary>
    /// Detects whether the on-screen touch keyboard is showing in a captured frame, so the test can
    /// DISCARD / re-take frames that are covered by it. The keyboard is custom-drawn and not in the UIA
    /// tree, so we can't query it - but it ALWAYS looks the same and always sits in the bottom band of
    /// the window. So we perceptual-hash that band and compare to a learned "keyboard" reference:
    ///
    ///   detector.Learn(frameWhereKeyboardIsUp);   // once, when we know it's showing (e.g. a field is focused)
    ///   if (detector.HasKeyboard(frame)) { ...discard / retake... }
    ///
    /// The reference is LEARNED (not a magic constant) so it adapts to the build/resolution. The band is
    /// downscaled to 16x16 before hashing, so it's resolution-independent.
    /// </summary>
    public sealed class KeyboardDetector
    {
        private ulong? _ref;

        /// <summary>Hamming distance (0..256) below which a frame's bottom band matches the learned
        /// keyboard. Tune if needed - keyboard frames cluster well below this, form frames well above.</summary>
        public int Threshold { get; set; } = 28;

        /// <summary>The keyboard occupies roughly the bottom 45% of the window; we hash that band.</summary>
        public double BandTopFraction { get; set; } = 0.55;

        public bool Learned => _ref.HasValue;

        /// <summary>Learn the keyboard signature from a frame where it is definitely showing.</summary>
        public void Learn(Bitmap withKeyboard)
        {
            try { _ref = BandHash(withKeyboard); } catch { }
        }

        /// <summary>True if this frame's bottom band matches the learned keyboard. False if not learned.</summary>
        public bool HasKeyboard(Bitmap frame)
        {
            if (!_ref.HasValue || frame == null) return false;
            try { return Imaging.Hamming(BandHash(frame), _ref.Value) < Threshold; }
            catch { return false; }
        }

        /// <summary>Raw distance of a frame from the learned keyboard - for diagnostics/threshold tuning.</summary>
        public int Distance(Bitmap frame)
        {
            if (!_ref.HasValue || frame == null) return int.MaxValue;
            try { return Imaging.Hamming(BandHash(frame), _ref.Value); } catch { return int.MaxValue; }
        }

        // perceptual hash of the bottom band (downscaled, so resolution-independent)
        private ulong BandHash(Bitmap full)
        {
            int top = (int)(full.Height * BandTopFraction);
            int h = full.Height - top;
            if (h <= 4 || full.Width <= 4) return Imaging.AverageHash(full);
            using (var band = new Bitmap(full.Width, h))
            {
                using (var g = Graphics.FromImage(band))
                    g.DrawImage(full, new Rectangle(0, 0, full.Width, h),
                                      new Rectangle(0, top, full.Width, h), GraphicsUnit.Pixel);
                return Imaging.AverageHash(band);
            }
        }
    }
}
