using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace FlaUiPoc
{
    /// <summary>
    /// A "burst album": when a test does a flurry of small actions in a short window (e.g. typing into
    /// 20 fields), one screenshot per action floods the report with near-identical images, and a single
    /// screenshot loses the granular story. BurstAlbum captures a frame per action and emits the whole
    /// set as ONE grouped step (rendered Messenger-style: a mosaic you click to expand).
    ///
    /// The key method is <see cref="Act"/>: it wraps an action so the frame is ALWAYS captured AFTER the
    /// action settles and BEFORE the next action runs. UI changes "happen too fast" otherwise - the next
    /// field is set before the previous one rendered, so frames are lost or look identical. Act makes
    /// capture synchronous and ordered.
    ///
    /// Usage:
    ///   using (var album = new BurstAlbum(app, automation, outDir, report, live, "Editing Pricing tab")
    ///                          { MainWindowOnly = true, Dedup = false, SettleMs = 250 })
    ///   {
    ///       foreach (var f in fields)
    ///           album.Act($"{f.Id} = {f.Value}", () => { Focus(f); f.Box.Text = f.Value; });
    ///   }   // Dispose() commits the group
    /// </summary>
    public sealed class BurstAlbum : IDisposable
    {
        private readonly FlaUI.Core.Application _app;
        private readonly FlaUI.UIA3.UIA3Automation _automation;
        private readonly string _outDir;
        private readonly ReportBuilder _report;
        private readonly LiveLog _live;
        private readonly string _title;

        private readonly List<string> _captions = new List<string>();
        private readonly List<string> _files = new List<string>();   // filenames (live JSON)
        private readonly List<string> _paths = new List<string>();   // full paths (HTML report)
        private ulong? _lastHash;
        private int _collapsed;
        private bool _committed;
        private int _liveIdx = -1;   // index of the live step we keep updating as the burst grows

        /// <summary>Max frames stored in one album; extras past this are counted, not stored.</summary>
        public int MaxFrames { get; set; } = 30;
        /// <summary>Drop a frame that looks identical to the last kept one? For DELIBERATE per-field
        /// edits we set this false (we KNOW each action changed something - keep them all).</summary>
        public bool Dedup { get; set; } = true;
        /// <summary>Hash distance (0..256) below which two frames are "the same" (only used when Dedup).</summary>
        public int ChangeThreshold { get; set; } = 6;
        /// <summary>Capture just the MAIN window (tight, so a single field's change is clearly visible)
        /// instead of the all-windows union. Modals are caught separately by ModalWatcher, so a burst
        /// frame doesn't need to span other windows.</summary>
        public bool MainWindowOnly { get; set; }
        /// <summary>Settle delay after an action before the frame is grabbed - lets the UI repaint so the
        /// capture reflects the change. This is what stops "it happened too fast to see".</summary>
        public int SettleMs { get; set; } = 200;
        /// <summary>When set (and it has learned the keyboard), frames where the on-screen keyboard is
        /// showing get the keyboard band CROPPED off before saving - so the evidence shows the form, not
        /// the keyboard. We can't hide the keyboard without re-coding the app, so we crop it from the shot.</summary>
        public KeyboardDetector Kb { get; set; }
        /// <summary>Before each frame, click the on-screen keyboard's own Hide button (the keyboard glyph
        /// at the bottom-left) so the picture shows the form, not the keyboard. The glyph is custom-drawn
        /// (not in the UIA tree), so it's clicked by position relative to the window's bottom-left - this
        /// is a normal user action (the button exists precisely to hide the keyboard).</summary>
        public bool CollapseKeyboard { get; set; }

        public BurstAlbum(FlaUI.Core.Application app, FlaUI.UIA3.UIA3Automation automation, string outDir,
                          ReportBuilder report, LiveLog live, string title)
        {
            _app = app; _automation = automation; _outDir = outDir;
            _report = report; _live = live; _title = title;
            // Show the album in the live stream immediately, then grow it frame-by-frame.
            _liveIdx = _live?.BeginAlbum(_title + " - starting...") ?? -1;
        }

        /// <summary>Run <paramref name="action"/>, let the UI settle, then capture a frame for it -
        /// guaranteeing the screenshot lands before the caller moves to the next action. Returns false
        /// if the action threw (the frame is still captured, showing the state at failure).</summary>
        public bool Act(string caption, Action action) => Act(caption, null, action);

        /// <summary>RULE-bearing edit: highlight + capture the field we are ABOUT to edit (anticipatory -
        /// the frame shows the boxed target before it changes), THEN set its value. Returns false if the
        /// set threw.</summary>
        public bool Edit(TextBox box, string value, string caption = null)
        {
            Snap(caption ?? $"{IdOf(box)} := {value}", box);   // highlight the NEXT input, then edit it
            bool ok = true;
            try { box.Text = value; } catch { ok = false; }
            if (SettleMs > 0) Thread.Sleep(SettleMs);
            return ok;
        }

        /// <summary>RULE-bearing toggle: highlight + capture the checkbox we are ABOUT to flip, then flip it.</summary>
        public bool Toggle(CheckBox cb, bool target, string caption = null)
        {
            Snap(caption ?? $"{IdOf(cb)} := {target}", cb);
            bool ok = true;
            try { cb.IsChecked = target; } catch { ok = false; }
            if (SettleMs > 0) Thread.Sleep(SettleMs);
            return ok;
        }

        private static string IdOf(AutomationElement e)
        { try { return e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { return ""; } }

        /// <summary>Run <paramref name="action"/>, settle, then capture a frame with <paramref name="highlight"/>
        /// boxed - so every frame shows EXACTLY which control this step touched (mandatory highlight).</summary>
        public bool Act(string caption, AutomationElement highlight, Action action)
        {
            bool ok = true;
            try { action(); } catch { ok = false; }
            if (SettleMs > 0) Thread.Sleep(SettleMs);
            Snap(caption, highlight);
            return ok;
        }

        /// <summary>Capture one frame for <paramref name="caption"/> right now, boxing <paramref name="highlight"/>
        /// if given. Collapses a frame identical to the previous kept one only when Dedup is on (the hash is
        /// taken BEFORE the highlight box so dedup reflects the real screen, not the box).</summary>
        public void Snap(string caption, AutomationElement highlight = null)
        {
            try
            {
                if (CollapseKeyboard) ClickKeyboardHide();
                Rectangle origin;
                using (var img = Grab(out origin))
                using (var bmp = img.Bitmap)
                {
                    var hash = Imaging.AverageHash(bmp);
                    if (Dedup && _lastHash.HasValue && Imaging.Hamming(hash, _lastHash.Value) < ChangeThreshold)
                    { _collapsed++; UpdateLive(caption); return; }

                    if (_paths.Count >= MaxFrames) { _collapsed++; UpdateLive(caption); return; }

                    if (highlight != null) Mark(bmp, origin, highlight);
                    var file = $"shot_{Guid.NewGuid():N}.png";
                    var path = Path.Combine(_outDir, file);
                    int cut = KeyboardWindow.CropY(_app.ProcessId, origin, origin, bmp.Height);
                    if (cut > 0) { using (var c = Imaging.CropTopPx(bmp, cut)) c.Save(path); }
                    else bmp.Save(path);
                    if (Dedup) _lastHash = hash;
                    _captions.Add(caption ?? "");
                    _files.Add(file);
                    _paths.Add(path);
                    UpdateLive(caption);
                }
            }
            catch { /* a dropped frame must never break the test */ }
        }

        // capture the main window (tight) or the all-windows union; report the capture's top-left so a
        // highlight box can be translated from screen coords into the image.
        private CaptureImage Grab(out Rectangle origin)
        {
            if (MainWindowOnly)
            {
                try
                {
                    var w = _app.GetMainWindow(_automation);
                    if (w != null && !w.IsOffscreen) { origin = w.BoundingRectangle; return Capture.Element(w); }
                }
                catch { }
            }
            return AppCapture.Grab(_app, _automation, out origin);
        }

        // Click the on-screen keyboard's Hide glyph (bottom-left key of the keyboard). It's custom-drawn
        // and not in the UIA tree, so we click it by position. No-op if the keyboard isn't showing.
        private void ClickKeyboardHide()
        {
            try
            {
                var w = _app.GetMainWindow(_automation);
                if (w == null || w.IsOffscreen) return;
                var r = w.BoundingRectangle;
                FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point((int)(r.Left + 66), (int)(r.Bottom - 44)));
                Thread.Sleep(250);
            }
            catch { }
        }

        // draw a red box around the control this frame is about (translated into capture coords)
        private static void Mark(Bitmap bmp, Rectangle origin, AutomationElement target)
        {
            try
            {
                var tb = target.BoundingRectangle;
                if (tb.Width <= 0 || tb.Height <= 0) return;
                int x = (int)(tb.X - origin.X), y = (int)(tb.Y - origin.Y), w = (int)tb.Width, h = (int)tb.Height;
                var box = Rectangle.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                              new Rectangle(x - 3, y - 3, w + 6, h + 6));
                if (box.Width <= 0 || box.Height <= 0) return;
                using (var g = Graphics.FromImage(bmp))
                using (var pen = new Pen(Color.Red, 4))
                using (var fill = new SolidBrush(Color.FromArgb(50, Color.Red)))
                { g.FillRectangle(fill, box); g.DrawRectangle(pen, box); }
            }
            catch { }
        }

        // push the growing album into the live stream so the dashboard FOLLOWS the burst in real time
        private void UpdateLive(string latest)
        {
            if (_liveIdx < 0) return;
            string extra = _collapsed > 0 ? $" (+{_collapsed} collapsed)" : "";
            var text = _paths.Count == 0
                ? _title + " - capturing..."
                : _title + $" - {_paths.Count} frame(s){extra}, latest: {latest}";
            _live?.UpdateAlbum(_liveIdx, text, _files);
        }

        /// <summary>Finalize: write the grouped step to the HTML report and stamp the final live text.
        /// Idempotent (safe via Dispose()).</summary>
        public void Commit()
        {
            if (_committed) return;
            _committed = true;

            string extra = _collapsed > 0 ? $" (+{_collapsed} near-identical collapsed)" : "";
            if (_paths.Count == 0)
            {
                var noneMsg = _title + " - no visible change captured" + extra;
                _report?.AddStep(noneMsg, null);
                if (_liveIdx >= 0) _live?.UpdateAlbum(_liveIdx, noneMsg, _files);
                return;
            }

            var msg = _title + $" - {_paths.Count} frame(s){extra}";
            _report?.AddAlbum(msg, _captions, _paths);
            if (_liveIdx >= 0) _live?.UpdateAlbum(_liveIdx, msg, _files);
        }

        public void Dispose() => Commit();
    }
}
