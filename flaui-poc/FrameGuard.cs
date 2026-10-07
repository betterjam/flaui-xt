using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace FlaUiPoc
{
    /// <summary>
    /// FrameGuard makes screenshots MEANINGFUL: it remembers the last frame's perceptual hash so it
    /// can tell whether the screen ACTUALLY changed between steps. Two jobs:
    ///
    ///   1. De-duplication - if a capture is (near) identical to the previous one, we don't keep a
    ///      redundant image. The step says "(no visible change)" and reuses the prior frame. This is
    ///      what stops the "bunch of repeated images" when a test is stuck (e.g. the user bumped the
    ///      mouse and the app froze) from flooding the report/video.
    ///
    ///   2. Stuck detection / progress-gated steps - WaitForNewScreen(action) retries an action until
    ///      the screen changes. If after N consecutive tries the frame is STILL identical, the screen
    ///      never advanced -> the step is "stuck" and the caller should FAIL ("no new screen reached"),
    ///      rather than march on as if it worked.
    ///
    /// The hash is a tiny 16x16 grayscale average-hash (aHash): robust to JPEG-ish noise / cursor
    /// flicker, cheap to compute, and good enough to answer "is this basically the same screen?".
    /// </summary>
    public sealed class FrameGuard
    {
        private readonly string _outDir;
        private ulong? _lastHash;
        private string _lastFile;            // last STORED image file (reused when a frame is a dup)

        public FrameGuard(string outDir) { _outDir = outDir; }

        /// <summary>How different two frames must be (in changed 16x16 cells, out of 256) to count as
        /// a NEW screen. Small cursor/caret blinks stay below this; a real screen change blows past it.</summary>
        public int ChangeThreshold { get; set; } = 12;

        /// <summary>Result of a capture: the file to show, whether it changed, and the distance.</summary>
        public sealed class Frame
        {
            public string File;        // image filename to reference (may be the PREVIOUS file if dup)
            public bool Changed;       // did the screen actually change vs the previous frame?
            public bool Captured;      // did we manage to grab a bitmap at all?
            public int Distance;       // hamming distance from the previous frame (0 = identical)
        }

        /// <summary>Capture the window (or screen) now and decide if it changed vs the last frame.
        /// On a duplicate we DO NOT write a new file - we reuse the previous one. When
        /// <paramref name="highlight"/> is given we ALWAYS shoot the whole CONTAINER (window) and draw
        /// a box around that element - so an open dropdown/select/field is shown IN CONTEXT, never a
        /// bare crop of the element alone.</summary>
        public Frame Capture(Window win, AutomationElement highlight = null)
        {
            try
            {
                // Capture across ALL of the app's windows (main + any popup on any monitor), not just
                // the main window - so an off-monitor validation modal is actually in the frame.
                Rectangle origin;
                using (var cap = AppCapture.GrabProcessOf(win, out origin))
                using (var bmp = cap.Bitmap)
                {
                    if (highlight != null)
                        DrawHighlight(bmp, origin, highlight);
                    ulong hash = AverageHash(bmp);
                    int dist = _lastHash.HasValue ? Hamming(hash, _lastHash.Value) : int.MaxValue;
                    bool changed = !_lastHash.HasValue || dist >= ChangeThreshold;

                    if (changed)
                    {
                        var file = $"shot_{Guid.NewGuid():N}.png";
                        bmp.Save(Path.Combine(_outDir, file), ImageFormat.Png);
                        _lastHash = hash; _lastFile = file;
                        return new Frame { File = file, Changed = true, Captured = true, Distance = dist };
                    }
                    // duplicate: reuse the previous stored frame, don't pile up identical PNGs
                    return new Frame { File = _lastFile, Changed = false, Captured = true, Distance = dist };
                }
            }
            catch
            {
                return new Frame { File = _lastFile, Changed = false, Captured = false, Distance = 0 };
            }
        }

        /// <summary>
        /// Run <paramref name="action"/>, then wait for the screen to change - retrying the action up to
        /// <paramref name="tries"/> times. Returns the first CHANGED frame, or the last (stuck) frame if
        /// the screen never advanced. Check <see cref="Frame.Changed"/>: false => stuck, the caller should
        /// fail the step ("no new screen after N tries"). This makes "a new screen appeared" the real
        /// criterion for moving on - exactly what a frozen/looping app should NOT pass.
        /// </summary>
        public Frame WaitForNewScreen(Window win, Action action, int tries = 3, int settleMs = 1200)
        {
            // Capture a BASELINE of the screen right now (before the action), so we compare the
            // after-action frames against THIS screen - not whatever a previous step left in _lastHash.
            ulong baseHash; bool haveBase = false;
            try
            {
                using (var cap0 = AppCapture.GrabProcessOf(win, out _))
                using (var b0 = cap0.Bitmap) { baseHash = AverageHash(b0); haveBase = true; }
            }
            catch { baseHash = 0; }

            Frame f = null;
            for (int i = 0; i < tries; i++)
            {
                try { action?.Invoke(); } catch { /* keep trying; a stuck UI may throw */ }
                Thread.Sleep(settleMs);
                f = Capture(win);
                // "changed" relative to the BASELINE we took before the action (robust), not just to
                // the rolling _lastHash. If we couldn't take a baseline, fall back to f.Changed.
                bool advanced = haveBase ? (f.Captured && f.Distance >= ChangeThreshold)
                                         : f.Changed;
                // Capture() compares to _lastHash; recompute distance vs the action baseline explicitly:
                if (haveBase && f.Captured)
                {
                    try
                    {
                        using (var capN = AppCapture.GrabProcessOf(win, out _))
                        using (var bN = capN.Bitmap)
                        { advanced = Hamming(AverageHash(bN), baseHash) >= ChangeThreshold; }
                    }
                    catch { }
                }
                if (advanced) { f.Changed = true; return f; }
            }
            return f ?? new Frame { File = _lastFile, Changed = false, Captured = false };
        }

        /// <summary>Draw a highlight box (and a small label) around <paramref name="el"/> on a capture
        /// whose top-left is <paramref name="origin"/>. The element's BoundingRectangle is in screen
        /// coords; translate it into the capture's coordinate space (origin = captured-rect top-left).</summary>
        private static void DrawHighlight(Bitmap winShot, Rectangle origin, AutomationElement el)
        {
            try
            {
                var er = el.BoundingRectangle;         // element in screen coords
                if (er.Width <= 0 || er.Height <= 0) return;
                int x = er.X - origin.X, y = er.Y - origin.Y;   // -> capture coords
                var box = Rectangle.Intersect(new Rectangle(0, 0, winShot.Width, winShot.Height),
                                              new Rectangle(x - 3, y - 3, er.Width + 6, er.Height + 6));
                if (box.Width <= 0 || box.Height <= 0) return;
                using (var g = Graphics.FromImage(winShot))
                using (var pen = new Pen(Color.FromArgb(255, 56, 189, 248), 3f))
                using (var fill = new SolidBrush(Color.FromArgb(40, 56, 189, 248)))
                {
                    g.FillRectangle(fill, box);
                    g.DrawRectangle(pen, box);
                }
            }
            catch { /* highlight is best-effort; never fail a capture over it */ }
        }

        // --- average-hash (aHash) over a 16x16 grayscale downscale -------------------------------
        private static ulong AverageHash(Bitmap src)
        {
            const int N = 16;                       // 256 cells -> 256-bit space folded to 64-bit sample
            using (var small = new Bitmap(N, N))
            {
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, N, N);
                }
                // grayscale values + mean
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
                // fold 256 bits into 64 by sampling every 4th cell (stable, plenty for "same screen?")
                ulong hash = 0; int bit = 0;
                for (int i = 0; i < vals.Length && bit < 64; i += 4, bit++)
                    if (vals[i] >= mean) hash |= (1UL << bit);
                return hash;
            }
        }

        private static int Hamming(ulong a, ulong b)
        {
            ulong x = a ^ b; int c = 0;
            while (x != 0) { c++; x &= (x - 1); }
            // scale the 64-bit popcount up to the 0..256 cell space the threshold is expressed in
            return c * 4;
        }
    }
}
