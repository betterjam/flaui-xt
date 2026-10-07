using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>
    /// UiSession is the reusable test-execution SERVICE: launch the app once, then drive it through a
    /// single object that applies the same RULES to every test, so a new test inherits them for free
    /// instead of copy-pasting capture/watcher/highlight code:
    ///
    ///   - multi-window capture (AppCapture)            - screenshots span every app window
    ///   - change detection (Imaging)                   - a step that didn't move the screen is labelled
    ///   - always-highlight                             - the acted control is boxed in every frame
    ///   - popup watcher (ModalWatcher)                 - any modal is caught, screenshot, clicked closed
    ///   - grouped, no-keyboard editing (BurstAlbum)    - Group().Edit/Toggle: one frame per field, and
    ///                                                    values are set via the UIA ValuePattern WITHOUT
    ///                                                    focusing (so the on-screen touch keyboard never
    ///                                                    pops up and covers the form)
    ///
    /// A test becomes:
    ///   _s = UiSession.Launch(ExePath, OutDir, "title");
    ///   _s.Step("opened the item", el);
    ///   using (var g = _s.Group("Editing Pricing")) { g.Edit(priceBox, "5"); g.Toggle(onSale, true); }
    ///   _s.DrainModals("edits");
    ///   ... _s.Finish("PASS"); _s.WriteReport("Title", "file.html"); _s.Dispose();
    /// </summary>
    public sealed class UiSession : IDisposable
    {
        public FlaUI.Core.Application App { get; private set; }
        public FlaUI.UIA3.UIA3Automation Automation { get; private set; }
        public Window Win => App?.GetMainWindow(Automation);
        public ReportBuilder Report { get; } = new ReportBuilder();
        public LiveLog Live { get; private set; }
        public Learnings Learn { get; private set; }
        public ModalWatcher Watcher { get; private set; }
        public WindowTracker Tracker { get; private set; }
        public Diag Diag { get; private set; }            // machine-level diagnostics for troubleshooting
        public DebugBridge Debug { get; private set; }    // interactive pause/inspect server (debug mode only)
        public KeyboardDetector Kb { get; } = new KeyboardDetector();   // crops the keyboard out of frames
        public string OutDir { get; private set; }
        public string Stamp { get; private set; }

        // ---- reusable RULES (defaults applied to every test that uses the session) ----
        public bool HighlightAlways { get; set; } = true;
        public bool DetectNoChange { get; set; } = true;
        public int StepChangeThreshold { get; set; } = 10;
        public int SettleMs { get; set; } = 250;

        private ulong? _lastStepHash;

        private UiSession() { }

        /// <summary>Launch the app and start the session (app + automation + report/live + watcher + diag).</summary>
        public static UiSession Launch(string exePath, string outDir, string testName, string liveTitle, int startupMs = 8000, string liveFile = "live-run.json", bool autoDismiss = false)
        {
            var s = new UiSession();
            Directory.CreateDirectory(outDir);
            s.OutDir = outDir;
            s.Stamp = "T" + DateTime.Now.ToString("MMdd_HHmm");
            s.Learn = new Learnings(outDir);
            bool dbg = Environment.GetEnvironmentVariable("EXA_DEBUG") == "1";
            s.Live = new LiveLog(outDir, liveTitle, liveFile, dbg);   // flag debug from the FIRST write
            s.Automation = new FlaUI.UIA3.UIA3Automation();
            s.App = FlaUI.Core.Application.Launch(exePath);
            s.Diag = new Diag(outDir, testName, s.App.ProcessId);
            s.Tracker = new WindowTracker(s.App, s.Automation);
            Thread.Sleep(startupMs);
            try { s.Win?.SetForeground(); } catch { }
            Thread.Sleep(500);
            s.Watcher = ModalWatcher.StartFor(s.App, s.Automation, outDir);
            // By default DON'T auto-close modals: a test should SEE the popups it triggers. A test opts in
            // to auto-dismiss only when it explicitly wants the watcher to clear validation popups for it.
            if (s.Watcher != null) s.Watcher.AutoDismiss = autoDismiss;
            // Debug mode (flagged on the live stream above): start the interactive pause/inspect server.
            if (dbg) { try { s.Debug = new DebugBridge(s); } catch { } }
            return s;
        }

        // ---- SOLID NAVIGATION primitives -------------------------------------------------------
        // Bring the app's window to the foreground so a click lands on the CONTROL, not just a
        // window-activate. The diag proved clicks were landing while a browser/IDE held the
        // foreground (Windows dings, the click is lost) - this is the fix for that.
        public void Front()
        {
            try { (_detail ?? Win)?.SetForeground(); } catch { }
            Thread.Sleep(180);
        }
        private Window _detail;   // set when the item-detail opens as its own window (off-screen, etc.)

        /// <summary>The main form's screen rectangle (used to locate the keyboard window relative to it).</summary>
        internal Rectangle MainRect()
        { try { var w = Win; return w != null ? w.BoundingRectangle : Rectangle.Empty; } catch { return Rectangle.Empty; } }
        /// <summary>Tell the session a detached form is now the active surface (so Front() raises IT).</summary>
        public void SetActiveWindow(Window w) { _detail = w; }
        public void ClearActiveWindow() { _detail = null; }

        /// <summary>One solid click: foreground the app, record the REAL clickability (diag flags a click
        /// that's covered by an overlapping window), click, then drain any popup the click spawned.
        /// Returns false if the target was missing or blocked (caller can retry).</summary>
        public bool Click(AutomationElement el, string label)
        {
            if (el == null) { Diag?.Note("click", label + ": element is null"); return false; }
            Front();
            bool hittable = Diag?.Clickable(label, el) ?? true;
            try { el.Click(); }
            catch (Exception ex) { Diag?.Error("click:" + label, ex); return false; }
            Thread.Sleep(250);
            DrainModals(label);   // a click can raise a validation popup - pull what the watcher caught
            return hittable;
        }

        /// <summary>Poll for an element until it appears (the UI may still be rendering). null on timeout.</summary>
        public AutomationElement WaitFor(Func<AutomationElement> find, int timeoutMs = 4000, int pollMs = 250)
        {
            int waited = 0;
            while (waited <= timeoutMs)
            {
                try { var e = find(); if (e != null) return e; } catch { }
                Thread.Sleep(pollMs); waited += pollMs;
            }
            return null;
        }

        /// <summary>The anti-"stuck" hop: click the control and CONFIRM the screen actually advanced
        /// (success() becomes true), retrying and draining modals between tries. A navigation only counts
        /// when the target screen really arrived - never marches on assuming a click worked.</summary>
        public bool ClickUntil(Func<AutomationElement> find, Func<bool> success, string label,
                               int tries = 4, Action<AutomationElement> action = null)
        {
            for (int i = 1; i <= tries; i++)
            {
                Front();
                var el = WaitFor(find, 3000);
                if (el == null) { Diag?.Note("nav", $"{label}: target control not found (try {i}/{tries})"); DrainModals(label); continue; }
                if (action == null) Click(el, $"{label} (try {i})");
                else
                {
                    Front();
                    Diag?.Clickable($"{label} (try {i})", el);
                    try { action(el); } catch (Exception ex) { Diag?.Error("nav:" + label, ex); }
                    Thread.Sleep(250); DrainModals(label);
                }
                Thread.Sleep(500);
                DrainModals(label);
                if (Safe(success)) return true;
                Diag?.Note("nav", $"{label}: acted but screen did not advance (try {i}/{tries})");
            }
            Diag?.Note("nav", $"{label}: FAILED after {tries} tries");
            return false;
        }
        private static bool Safe(Func<bool> f) { try { return f(); } catch { return false; } }

        /// <summary>One replay step: a human label + an action that returns true when it succeeded.</summary>
        public sealed class FlowStep { public string Label; public Func<bool> Act; }

        /// <summary>Run a recorded flow with SELF-HEALING: when a step fails (control not found), step BACK
        /// and re-run the previous step to restore the screen, then retry the failed step - up to
        /// <paramref name="backRetries"/> times. A navigation that didn't land the first time gets another
        /// chance instead of cascading into "could not find" for every step after it.</summary>
        public int RunFlow(System.Collections.Generic.List<FlowStep> steps, int backRetries = 2)
        {
            int failed = 0;
            for (int i = 0; i < steps.Count; i++)
            {
                if (Debug != null && Debug.Stopped) break;   // Stop pressed - abort the remaining steps
                var s = steps[i];
                Debug?.Gate(i + 1, steps.Count, s.Label);   // debug mode: block here while paused, then proceed
                if (Debug != null && Debug.Stopped) break;
                if (Safe(s.Act)) continue;   // succeeded first try - the step's own action handles its frame

                // failed: log ONE step and UPDATE its image on each retry (the failure image is replaced).
                // Step back PROGRESSIVELY: retry 1 redoes the previous step, retry 2 redoes the previous TWO
                // (previous-to-last included), etc. - rebuilding more navigation context each attempt.
                int li = LiveStep($"\"{s.Label}\" not found - stepping back to retry…");
                bool ok = false;
                for (int attempt = 1; attempt <= backRetries && !ok; attempt++)
                {
                    int from = Math.Max(0, i - attempt);                       // how far back to replay
                    for (int j = from; j < i; j++) Safe(steps[j].Act);         // re-run those steps in order
                    Thread.Sleep(400);
                    DrainModals("retry");
                    ok = Safe(s.Act);
                    int backN = i - from;
                    LiveUpdate(li, ok
                        ? $"\"{s.Label}\" recovered after replaying the previous {backN} step(s)."
                        : $"\"{s.Label}\" retry {attempt}/{backRetries} (went back {backN} step(s)) — still not found…");
                }
                if (!ok) { failed++; Step($"\"{s.Label}\" failed after {backRetries} back-step retries."); }
            }
            Debug?.HoldEnd();   // debug mode: hold at the end so the run can't close on its own (Stop to end)
            return failed;   // 0 = a faithful replay; >0 = the flow diverged (the test must FAIL)
        }

        // ---- REPLAY PRIMITIVES (engine-level, shared by every recorded test) --------------------------
        // The recorder emits thin tests that just declare steps calling these - the resolution + retry
        // logic lives HERE, in the engine, not copy-pasted into each generated test.

        /// <summary>Click the UNIQUELY-resolved element (id -> name -> parent-scoped -> sibling index).
        /// Returns false if it couldn't be found (RunFlow then retries).</summary>
        public bool ClickEl(string by, string key, string name, int index, string parent, string ct)
        {
            var el = WaitFor(() => Resolve(by, key, name, index, parent, ct), 4000);
            if (el == null) return false;
            PictureHighlight(el, "Click \"" + (!string.IsNullOrEmpty(name) ? name : key) + "\"");   // box where we click
            Click(el, !string.IsNullOrEmpty(name) ? name : key);
            return true;
        }

        public bool DblClickEl(string by, string key, string name, int index, string parent, string ct)
        {
            var el = WaitFor(() => Resolve(by, key, name, index, parent, ct), 4000);
            if (el == null) return false;
            PictureHighlight(el, "Double-click \"" + (!string.IsNullOrEmpty(name) ? name : key) + "\"");
            Front(); try { el.DoubleClick(); } catch { } DrainModals("dbl");
            return true;
        }

        /// <summary>Open the first grid row (verified) - selecting an item from a list.</summary>
        public bool OpenRow() =>
            ClickUntil(() => Win.FindFirstDescendant(cf => cf.ByAutomationId("dgvList"))?
                            .FindAllChildren().FirstOrDefault(r => (r.Properties.Name.ValueOrDefault ?? "").StartsWith("Row ")),
                       () => Win.FindFirstDescendant(cf => cf.ByAutomationId("txtSKU")) != null
                             || Win.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tab)) != null,
                       "open row", 4, e => e.AsGridRow()?.DoubleClick());

        public bool SelectTab(string name)
        {
            var t = WaitFor(() => Win.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(name)))
                                  ?? Win.FindFirstDescendant(cf => cf.ByName(name)), 4000);
            if (t == null) return false;
            PictureHighlight(t, "Open the \"" + name + "\" tab");
            Click(t, "tab " + name);
            return true;
        }

        public bool SetField(string id, string value)
        {
            var el = WaitFor(() => Win.FindFirstDescendant(cf => cf.ByAutomationId(id)), 4000)?.AsTextBox();
            if (el == null) return false;
            using (var g = Group("set " + id)) g.Edit(el, value);
            DrainModals("set " + id);
            return true;
        }

        // resolve the UNIQUE element across ALL the app's windows (the main MDI frame AND any modal dialog
        // like frmPicture/frmCash, which are SEPARATE top-level windows - not children of the main window).
        // Scope to the recorded parent container, disambiguate duplicates by the recorded sibling index,
        // and stay TYPE-AWARE so "Pay" the Button never matches "Pay" the TabItem.
        private AutomationElement Resolve(string by, string key, string name, int index, string parent, string ct)
        {
            var windows = AppWindows();
            ControlType? t = null;
            if (!string.IsNullOrEmpty(ct)) { try { t = (ControlType)Enum.Parse(typeof(ControlType), ct); } catch { } }

            // pick the container(s) to search: the recorded parent (found across ALL windows), else every window
            AutomationElement[] roots;
            if (!string.IsNullOrEmpty(parent))
            {
                var container = FindContainer(windows, parent);
                if (container == null) return null;   // container/dialog not on screen yet -> FAIL so RunFlow retries
                roots = new[] { container };
            }
            else roots = windows;

            foreach (var root in roots)
            {
                var el = ResolveIn(root, by, key, name, index, t);
                if (el != null) return el;
            }
            return null;   // strict: better to fail + retry than click the wrong control
        }

        // all top-level windows of the app (main + modals). FindAllChildren on the desktop by process id
        // catches modal dialogs that App.GetAllTopLevelWindows can miss.
        private AutomationElement[] AppWindows()
        {
            try
            {
                var wins = Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(App.ProcessId));
                if (wins != null && wins.Length > 0) return wins;
            }
            catch { }
            try { var w = Win; if (w != null) return new[] { w }; } catch { }
            return new AutomationElement[0];
        }

        // find the recorded parent container by AutomationId - it may BE a top-level window (a modal) or a
        // descendant of one.
        private static AutomationElement FindContainer(AutomationElement[] windows, string parentId)
        {
            foreach (var w in windows)
            {
                try
                {
                    if ((w.Properties.AutomationId.ValueOrDefault ?? "") == parentId) return w;
                    var d = w.FindFirstDescendant(cf => cf.ByAutomationId(parentId));
                    if (d != null) return d;
                }
                catch { }
            }
            return null;
        }

        // within one container/window: index disambiguation, then stable id, then type-aware name
        private static AutomationElement ResolveIn(AutomationElement root, string by, string key, string name, int index, ControlType? t)
        {
            if (index >= 0 && !string.IsNullOrEmpty(name) && t.HasValue)
            {
                try { var all = root.FindAllDescendants(cf => cf.ByName(name).And(cf.ByControlType(t.Value))); if (index < all.Length) return all[index]; }
                catch { }
            }
            AutomationElement el = null;
            if (by == "id" && !string.IsNullOrEmpty(key)) { try { el = root.FindFirstDescendant(cf => cf.ByAutomationId(key)); } catch { } }
            if (el == null && !string.IsNullOrEmpty(name))
            {
                try { el = t.HasValue ? root.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(t.Value)))
                                      : root.FindFirstDescendant(cf => cf.ByName(name)); }
                catch { }
            }
            return el;
        }

        /// <summary>One narrative step: capture (all windows), detect "no visible change", box the acted
        /// control, and append to the report + live stream.</summary>
        public void Step(string msg, AutomationElement highlight = null)
        {
            string png = null, file = null;
            try
            {
                file = $"shot_{Guid.NewGuid():N}.png";
                png = Path.Combine(OutDir, file);
                Rectangle origin;
                using (var img = AppCapture.Grab(App, Automation, out origin))
                using (var bmp = img.Bitmap)
                {
                    if (DetectNoChange)
                    {
                        var h = Imaging.AverageHash(bmp);
                        if (_lastStepHash.HasValue && Imaging.Hamming(h, _lastStepHash.Value) < StepChangeThreshold)
                            msg += "  (no visible change)";
                        _lastStepHash = h;
                    }
                    if (HighlightAlways && highlight != null) Box(bmp, origin, highlight);
                    int cut = KeyboardWindow.CropY(App.ProcessId, MainRect(), origin, bmp.Height);
                    if (cut > 0) { using (var c = Imaging.CropTopPx(bmp, cut)) c.Save(png); }
                    else bmp.Save(png);
                }
            }
            catch { png = null; }
            Report.AddStep(msg, png);
            Live?.AddStep(msg, file);
            TestContext.WriteLine(msg);
        }

        /// <summary>Grab a fresh frame to a file (keyboard cropped) and return its filename.</summary>
        private string CaptureToFile()
        {
            try
            {
                var file = $"shot_{Guid.NewGuid():N}.png";
                var png = Path.Combine(OutDir, file);
                Rectangle origin;
                using (var img = AppCapture.Grab(App, Automation, out origin))
                using (var bmp = img.Bitmap)
                {
                    int cut = KeyboardWindow.CropY(App.ProcessId, MainRect(), origin, bmp.Height);
                    if (cut > 0) { using (var c = Imaging.CropTopPx(bmp, cut)) c.Save(png); }
                    else bmp.Save(png);
                }
                return file;
            }
            catch { return null; }
        }

        /// <summary>A documented PICTURE step: capture ONLY the Exatouch window via PrintWindow (its own
        /// pixels - never the whole desktop, never an overlapping window), keyboard-cropped, then log it.
        /// This is what recorded-flow pictures use, so a replay frame is always just the app.</summary>
        public void Picture(string label)
        {
            string file = null, png = null;
            try
            {
                var w = Win;
                IntPtr h = IntPtr.Zero; try { h = w.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                file = $"shot_{Guid.NewGuid():N}.png";
                png = Path.Combine(OutDir, file);

                System.Drawing.Point origin;
                var bmp = PrintWindowBitmap(h, out origin);       // Exatouch pixels only (ignores overlap/desktop)
                if (bmp == null && w != null && !w.IsOffscreen)   // fallback: front the window, grab its region
                {
                    try { w.SetForeground(); Thread.Sleep(150); } catch { }
                    var br = w.BoundingRectangle; origin = new System.Drawing.Point(br.X, br.Y);
                    using (var img = FlaUI.Core.Capturing.Capture.Element(w)) bmp = new Bitmap(img.Bitmap);
                }
                if (bmp == null) { png = null; }
                else using (bmp)
                {
                    int cut = KeyboardWindow.CropY(App.ProcessId, MainRect(), new Rectangle(origin.X, origin.Y, bmp.Width, bmp.Height), bmp.Height);
                    if (cut > 0) { using (var c = Imaging.CropTopPx(bmp, cut)) c.Save(png); }
                    else bmp.Save(png);
                }
            }
            catch { png = null; }
            Report.AddStep(label, png);
            Live?.AddStep(label, file);
            TestContext.WriteLine(label);
        }

        /// <summary>Replay-time highlight: capture the Exatouch window (PrintWindow, app-only) with the
        /// resolved <paramref name="el"/> boxed - so each action shows EXACTLY where it clicked, accurately
        /// (we resolved the real element). Captured BEFORE the click, on the source screen.</summary>
        public void PictureHighlight(AutomationElement el, string label)
        {
            string file = null, png = null;
            try
            {
                var w = Win;
                IntPtr h = IntPtr.Zero; try { h = w.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
                file = $"shot_{Guid.NewGuid():N}.png";
                png = Path.Combine(OutDir, file);
                System.Drawing.Point origin;
                var bmp = PrintWindowBitmap(h, out origin);
                if (bmp == null && w != null && !w.IsOffscreen)
                {
                    try { w.SetForeground(); Thread.Sleep(120); } catch { }
                    var br = w.BoundingRectangle; origin = new System.Drawing.Point(br.X, br.Y);
                    using (var img = FlaUI.Core.Capturing.Capture.Element(w)) bmp = new Bitmap(img.Bitmap);
                }
                if (bmp == null) { png = null; }
                else using (bmp)
                {
                    Box(bmp, new Rectangle(origin.X, origin.Y, bmp.Width, bmp.Height), el);   // red box on the element
                    int cut = KeyboardWindow.CropY(App.ProcessId, MainRect(), new Rectangle(origin.X, origin.Y, bmp.Width, bmp.Height), bmp.Height);
                    if (cut > 0) { using (var c = Imaging.CropTopPx(bmp, cut)) c.Save(png); }
                    else bmp.Save(png);
                }
            }
            catch { png = null; }
            Report.AddStep(label, png);
            Live?.AddStep(label, file);
            TestContext.WriteLine(label);
        }

        // PrintWindow capture of a single window (renders its own pixels even when covered). null if blank.
        private static Bitmap PrintWindowBitmap(IntPtr h, out System.Drawing.Point origin)
        {
            origin = System.Drawing.Point.Empty;
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return null;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0 || w > 10000 || ht > 10000) return null;
            var bmp = new Bitmap(w, ht);
            bool ok = false;
            using (var g = Graphics.FromImage(bmp)) { var hdc = g.GetHdc(); try { ok = PrintWindow(h, hdc, 2); } finally { g.ReleaseHdc(hdc); } }
            if (!ok) { bmp.Dispose(); return null; }
            // mostly-black => PrintWindow didn't render; let the caller fall back
            int dark = 0, n = 0, sx = Math.Max(1, w / 40), sy = Math.Max(1, ht / 40);
            for (int y = 2; y < ht; y += sy) for (int x = 2; x < w; x += sx) { var p = bmp.GetPixel(x, y); n++; if (p.R < 12 && p.G < 12 && p.B < 12) dark++; }
            if (n > 0 && dark * 100 / n > 92) { bmp.Dispose(); return null; }
            origin = new System.Drawing.Point(r.Left, r.Top);
            return bmp;
        }
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

        /// <summary>Add a LIVE-only step (returns its index) - for retry progress that updates in place.</summary>
        public int LiveStep(string msg) { var f = CaptureToFile(); TestContext.WriteLine(msg); return Live?.AddStep(msg, f) ?? -1; }

        /// <summary>Replace a live step's text + IMAGE in place (so a retry shows its result, not the old failure).</summary>
        public void LiveUpdate(int index, string msg) { if (index < 0) return; var f = CaptureToFile(); Live?.UpdateStep(index, msg, f); TestContext.WriteLine(msg); }

        /// <summary>Open a grouped capture for a flurry of edits. Use Group(...).Edit/Toggle so each field
        /// becomes one highlighted tile (the input is boxed BEFORE it's edited).</summary>
        public BurstAlbum Group(string title) =>
            new BurstAlbum(App, Automation, OutDir, Report, Live, title)
            { MainWindowOnly = true, Dedup = false, SettleMs = SettleMs, Kb = Kb };

        /// <summary>Learn the on-screen keyboard's signature from the current frame (call it when the
        /// keyboard is showing - e.g. just after an item opens with a field focused). Once learned, Step
        /// and Group() automatically crop the keyboard band out of any frame where it appears.</summary>
        public void LearnKeyboard()
        {
            try
            {
                using (var img = (Win != null && !Win.IsOffscreen)
                       ? FlaUI.Core.Capturing.Capture.Element(Win)
                       : AppCapture.Grab(App, Automation, out _))
                    Kb.Learn(img.Bitmap);
            }
            catch { }
        }

        /// <summary>Pull any popups the watcher caught into the report (single-threaded, on the caller).</summary>
        public int DrainModals(string context) => Watcher?.DrainTo(Report, Live, context) ?? 0;

        /// <summary>Pause/resume the watcher's auto-dismiss around a critical section (e.g. a save-retry
        /// loop that must own dismissal itself).</summary>
        public void PauseWatcher(bool paused) { if (Watcher != null) Watcher.Paused = paused; }

        public void Finish(string status) => Live?.Finish(status);
        public string WriteReport(string title, string file) => Report.Write(OutDir, title, file);

        public void Dispose()
        {
            try { Debug?.Dispose(); } catch { }
            try { DrainModals("teardown"); } catch { }
            try { Watcher?.Dispose(); } catch { }
            try { App?.Kill(); } catch { }
            Automation?.Dispose();
        }

        // draw a red box around the acted control, translated into the capture's coordinates
        private static void Box(Bitmap bmp, Rectangle origin, AutomationElement target)
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
    }
}
