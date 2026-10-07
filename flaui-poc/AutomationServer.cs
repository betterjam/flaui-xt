using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>
    /// AutomationServer - a LIVE, reactive automation driver. Instead of running a fixed script and
    /// exiting, it launches Exatouch ONCE, holds a UiSession open, and serves a tiny command API on
    /// http://localhost:5200. A bot/agent sends one command at a time and gets back the REAL state -
    /// a fresh screenshot, whether the screen changed, whether the on-screen keyboard is showing, where
    /// we are, and any popup the watcher caught - then decides the next move. That's "a bot speaking
    /// directly with the app."
    ///
    /// Run it (it blocks, serving until /stop or kill):
    ///   dotnet test --filter Run_automation_driver
    ///
    /// Commands (all return JSON {ok, screen, changed, keyboard, blocked, foreground, popup, file}):
    ///   GET  /scene                          - current state + fresh screenshot, no action
    ///   POST /drive?action=click&by=name&value=Items
    ///   POST /drive?action=doubleclick&by=row
    ///   POST /drive?action=set&by=id&value=txtCost&to=42
    ///   POST /drive?action=home
    ///   POST /drive?action=learnkb           - learn the keyboard signature from the current frame
    ///   POST /stop
    ///
    /// Commands run sequentially on the listener thread, so UIA stays single-threaded; the ModalWatcher
    /// runs on its own Win32 thread.
    /// </summary>
    [TestFixture]
    [Explicit("Live automation driver - run manually, it serves http://localhost:5200 until stopped.")]
    public class AutomationServer
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;
        private Recorder _rec;        // record-the-user mode: a live test creator
        private readonly KeyboardDetector _kb = new KeyboardDetector();
        private ulong? _lastHash;     // for change detection between actions
        private int _lastWinCount = 1; // window-stack size last time we looked (to spot a window vanishing)
        private IntPtr _selected = IntPtr.Zero; // the layer the visor shows + the picture captures (0 = main)
        private readonly HashSet<long> _known = new HashSet<long>(); // handles seen so far (to spot NEW layers)
        private bool _seeded;                   // have we taken the first window snapshot yet?
        private string _captureMethod = "screen"; // how the last frame was grabbed (printwindow | screen)
        private string _extends = "";           // when extending: the parent test this recording branches off
        private static readonly JavaScriptSerializer _js = new JavaScriptSerializer();

        [Test]
        public void Run_automation_driver()
        {
            _s = UiSession.Launch(ExePath, OutDir, "live-test-creator", "🔴 Live Test Creator", liveFile: "flow-capture.json");
            // STUDIO MODE: the user opens windows on purpose (Cash payment, dialogs) and interacts with
            // them - the watcher must NOT close them. It still DETECTS them (for the layer chooser), it
            // just never auto-dismisses while the studio is alive.
            if (_s.Watcher != null) _s.Watcher.AutoDismiss = false;
            _rec = new Recorder(_s.App, _s.Automation, OutDir, _s.Live);
            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:5200/");
            listener.Start();
            TestContext.Progress.WriteLine("Automation driver listening on http://localhost:5200/");

            bool run = true;
            while (run)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                string json;
                try { json = Handle(ctx, ref run); }
                catch (Exception ex) { json = "{\"ok\":false,\"error\":\"" + J(ex.Message) + "\"}"; }
                var buf = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json";
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                try { ctx.Response.OutputStream.Write(buf, 0, buf.Length); ctx.Response.OutputStream.Close(); } catch { }
            }
            try { listener.Stop(); } catch { }
            _s?.Dispose();
        }

        private string Handle(HttpListenerContext ctx, ref bool run)
        {
            var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
            var q = ctx.Request.QueryString;

            if (path == "/stop") { run = false; return "{\"ok\":true,\"stopped\":true}"; }
            if (path == "/scene") return State(true, null, false, false);

            // ---- FLOW CAPTURE (the live test creator) ----
            if (path == "/picture")                       // the user took a DELIBERATE snapshot
            {
                string note = q["note"] ?? "";
                if (string.IsNullOrEmpty(note) || note == "picture")   // auto-describe: screen + last action
                {
                    var last = _rec.IsRecording ? _rec.LastLabel : "";
                    note = GuessScreen() + (string.IsNullOrEmpty(last) ? "" : " - after " + last);
                }
                IntPtr h = _selected != IntPtr.Zero ? _selected : MainHandle();   // the SELECTED layer (or main)
                // capture CLEAN (no highlight at snap time); the next click back-annotates this picture.
                string file = SavePicture(h, null);
                int ox = 0, oy = 0; if (GetWindowRect(h, out var wr)) { ox = wr.Left; oy = wr.Top; }
                if (_rec.IsRecording) _rec.MarkPicture(file, note, ox, oy);
                else { try { _s.Live?.AddStep("📸 " + note, file); } catch { } }
                return State(true, "📸 " + note, false, false);
            }

            if (path == "/layers")                        // what windows/modals are open right now?
                return Layers();

            if (path == "/inspect")                       // dump elements matching a name/id (for diagnosis)
                return Inspect(q["name"], q["id"]);

            if (path == "/select")                        // choose which layer the visor shows + picture grabs
            {
                _selected = new IntPtr(long.Parse(q["handle"] ?? "0"));
                return Preview();
            }

            if (path == "/preview")                       // the live VISOR frame of the selected layer
                return Preview();

            if (path == "/record/autodismiss")   // checkbox: should the generated test auto-dismiss modals?
            {
                _rec.TestAutoDismiss = (q["on"] == "true" || q["on"] == "1");
                return State(true, "test auto-dismiss modals = " + _rec.TestAutoDismiss, false, false);
            }
            if (path == "/record/clear") { _extends = ""; _rec.Clear(); return State(true, "cleared - capture is fresh", false, false); }
            if (path == "/record/start") { _extends = ""; _rec.Start(); return State(true, "● recording your interactions - drive the app by hand", false, false); }
            if (path == "/record/extend")     // EXTEND a parent test: replay it to its end, then record the DIFF
            {
                string parent = q["parent"] ?? "";
                if (string.IsNullOrEmpty(parent)) return State(false, "no parent test given to extend", false, false);
                try
                {
                    var steps = LoadEffectiveSteps(parent);
                    if (steps.Count == 0) return State(false, "parent '" + parent + "' has no recorded steps to replay", false, false);
                    _extends = parent;
                    try { _s.Front(); } catch { }
                    ReplaySteps(steps);              // drive the app to the trunk's end state (the branch point)
                    // DO NOT auto-record: replay leaves the app AT the branch point and hands control back to the
                    // user. They press ● Start recording when ready to capture the branch (the diff).
                    try { return State(true, "▶ replayed trunk '" + parent + "' (" + steps.Count + " steps) - you're at the branch point. Press Start recording to capture your branch.", false, false); }
                    catch { return "{\"ok\":true,\"note\":\"replayed trunk '" + J(parent) + "' (" + steps.Count + " steps) - you're at the branch point\"}"; }
                }
                catch (Exception ex)
                {
                    return "{\"ok\":false,\"error\":\"" + J(ex.Message) + "\",\"where\":\"" + J((ex.StackTrace ?? "").Split('\n').FirstOrDefault() ?? "") + "\"}";
                }
            }
            if (path == "/record/stop")
            {
                var steps = _rec.Stop();
                System.IO.File.WriteAllText(System.IO.Path.Combine(OutDir, "recording.json"), RecordingJson(steps));
                return "{\"ok\":true,\"recording\":false,\"steps\":" + steps.Count + ",\"detail\":" + RecordingJson(steps) + "}";
            }
            if (path == "/record/save")                   // turn the recording into a runnable test file
            {
                string cls = q["class"] ?? "RecordedFlowTest", name = q["name"] ?? "Recorded_flow";
                string parent = q["extends"] ?? _extends ?? "";   // this recording branches off a parent test?
                var steps = _rec.Snapshot();
                string pocDir = Paths.PocDir;
                // 1) the deterministic MAP: the captured flow as data, saved next to the test (reproducible)
                //    AND in the results dir keyed by method, so the suite's Steps & Actions tab can load it.
                //    For an extended test this map is the DIFF ONLY (the branch); the parent link is in extends.json.
                var mapJson = RecordingJson(steps);
                var mapFile = System.IO.Path.Combine(pocDir, cls + ".recording.json");
                System.IO.File.WriteAllText(mapFile, mapJson);
                System.IO.File.WriteAllText(System.IO.Path.Combine(OutDir, "recording-" + name + ".json"), mapJson);
                // 2) record the parent link so the suite shows the hierarchy and regen stays consistent
                int inheritedCount = !string.IsNullOrEmpty(parent) ? LoadEffectiveSteps(parent).Count : 0;
                if (!string.IsNullOrEmpty(parent)) MergeExtends(name, parent);
                // 3) the runnable FlaUI test - REFERENCE model: its BuildFlow calls the parent's BuildFlow, then
                //    adds these own steps. (The dashboard then regenerates the whole chain so the parent .cs has BuildFlow.)
                var csFile = System.IO.Path.Combine(pocDir, cls + ".cs");
                System.IO.File.WriteAllText(csFile, _rec.Generate(cls, name, string.IsNullOrEmpty(parent) ? null : parent));
                int total = inheritedCount + steps.Count;
                return "{\"ok\":true,\"file\":\"" + J(csFile) + "\",\"map\":\"" + J(mapFile) + "\",\"steps\":" + steps.Count
                       + ",\"extends\":\"" + J(parent) + "\",\"total\":" + total + "}";
            }
            if (path == "/resume")                        // user told us which window to continue on
            {
                var h = new IntPtr(long.Parse(q["handle"] ?? "0"));
                var win = WindowByHandle(h);
                if (win == null) return State(false, "that window is no longer there", false, false);
                _s.SetActiveWindow(win);
                try { win.SetForeground(); } catch { }
                return State(true, "continuing on: " + (NameOrTitle(win)), false, false);
            }

            if (path == "/drive")
            {
                string action = (q["action"] ?? "").ToLowerInvariant();
                string by = q["by"] ?? "name", value = q["value"] ?? "", to = q["to"] ?? "";
                _s.Diag?.Note("driver", $"action={action} by={by} value={value} to={to}");

                if (action == "learnkb")
                {
                    using (var img = CaptureMain()) { _kb.Learn(img.Bitmap); }
                    return State(true, "learned keyboard reference", false, false);
                }
                if (action == "hidekb")
                {
                    // Let the APP hide its own keyboard the way a user would: a tap on a neutral, non-input
                    // part of the form fires the form's Global.Keyboard.Exit() (csForm.cs). We click a static
                    // Label - never a textbox (which would re-summon it) and never a button (which would act).
                    var label = NeutralLabel();
                    if (label == null) return State(false, "no neutral label found to defocus onto", false, false);
                    _s.Front();
                    try { label.Click(); } catch { }
                    Thread.Sleep(400);
                    return State(true, "clicked a neutral label so the app hides its keyboard", false, false);
                }
                if (action == "home")
                {
                    var ok = _s.Click(_s.Win.FindFirstDescendant(cf => cf.ByAutomationId("btnHome")), "home");
                    return State(true, null, false, !ok);
                }
                if (action == "open")   // full nav to the first item's detail (verified hops)
                {
                    _s.ClickUntil(() => _s.Win.FindFirstDescendant(cf => cf.ByName("Items"))?.AsButton(),
                                  () => _s.Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList")) != null, "Home->Items");
                    _s.ClickUntil(() => _s.Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList"))?.AsButton(),
                                  () => _s.Win.FindFirstDescendant(cf => cf.ByAutomationId("dgvList")) != null, "Items->List");
                    _s.ClickUntil(() => Resolve("row", ""),
                                  () => _s.Win.FindFirstDescendant(cf => cf.ByAutomationId("txtSKU")) != null
                                        || _s.Win.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tab)) != null,
                                  "open first item", 4, e => e.AsGridRow()?.DoubleClick());
                    return State(true, "navigated to first item detail", false, false);
                }
                if (action == "tab")
                {
                    var t = _s.Win.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(value)))
                            ?? _s.Win.FindFirstDescendant(cf => cf.ByName(value));
                    if (t == null) return State(false, $"tab '{value}' not found", false, false);
                    _s.Click(t, "tab " + value);
                    return State(true, "tab " + value, false, false);
                }
                if (action == "fill")   // change values across the current screen, like the persistence test
                {
                    int n = 0; var rng = new Random();
                    foreach (var e in _s.Win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
                    {
                        try
                        {
                            var tb = e.AsTextBox();
                            if (!tb.IsEnabled || tb.IsReadOnly) continue;
                            var id = tb.Properties.AutomationId.ValueOrDefault ?? "";
                            if (string.IsNullOrEmpty(id)) continue;
                            bool numeric = new[] { "Price", "Cost", "Qty", "Markup", "Weight", "Limit", "Order", "Stock", "Age", "SKU", "PLU", "Part" }.Any(id.Contains);
                            tb.Text = numeric ? rng.Next(1, 9999).ToString() : (_s.Stamp + "_" + rng.Next(100, 999));
                            n++;
                        }
                        catch { }
                        _s.DrainModals("fill");   // a validation modal can fire on any field - watcher closes it
                    }
                    return State(true, $"filled {n} field(s) on this screen", false, false);
                }

                var el = Resolve(by, value);
                if (el == null) return State(false, $"target not found: {by}={value}", false, false);

                bool blocked = false;
                switch (action)
                {
                    case "click":       blocked = !_s.Click(el, $"{by}={value}"); break;
                    case "doubleclick": _s.Front(); _s.Diag?.Clickable($"dblclick {by}={value}", el);
                                        try { el.AsGridRow()?.DoubleClick(); } catch { try { el.DoubleClick(); } catch { } }
                                        Thread.Sleep(400); _s.DrainModals("doubleclick"); break;
                    case "set":         _s.Front(); _s.Diag?.Clickable($"set {value}", el);
                                        try { el.AsTextBox().Text = to; } catch { }
                                        Thread.Sleep(300); _s.DrainModals("set"); break;
                    default: return State(false, $"unknown action: {action}", false, false);
                }
                return State(true, null, false, blocked);
            }
            return "{\"ok\":false,\"error\":\"unknown path\"}";
        }

        // A static label in the upper half of the form - a safe place to "tap" so the focused textbox
        // leaves and the app hides its on-screen keyboard. Never an editable, never a button.
        private AutomationElement NeutralLabel()
        {
            var w = _s.Win; if (w == null) return null;
            try
            {
                var main = w.BoundingRectangle;
                return w.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                        .FirstOrDefault(t =>
                        {
                            try
                            {
                                var r = t.BoundingRectangle;
                                return !string.IsNullOrWhiteSpace(t.Properties.Name.ValueOrDefault)
                                       && r.Width > 0 && r.Height > 0
                                       && r.Top < main.Top + main.Height * 0.45;   // upper area, clear of the keyboard
                            }
                            catch { return false; }
                        });
            }
            catch { return null; }
        }

        // resolve an element by automationId / visible name / the first grid row
        private AutomationElement Resolve(string by, string value)
        {
            var w = _s.Win; if (w == null) return null;
            try
            {
                switch (by)
                {
                    case "id":  return w.FindFirstDescendant(cf => cf.ByAutomationId(value));
                    case "name":return w.FindFirstDescendant(cf => cf.ByName(value));
                    case "row": return w.FindFirstDescendant(cf => cf.ByAutomationId("dgvList"))?
                                       .FindAllChildren().FirstOrDefault(r => (r.Properties.Name.ValueOrDefault ?? "").StartsWith("Row "));
                    default: return null;
                }
            }
            catch { return null; }
        }

        // Build the JSON state response: capture a fresh frame, detect change + keyboard, guess the screen.
        private string State(bool ok, string note, bool persist, bool blocked)
        {
            string file = null; bool changed = false, keyboard = false;
            try
            {
                using (var img = CaptureMain())
                {
                    var bmp = img.Bitmap;
                    var hash = Imaging.AverageHash(bmp);
                    changed = !_lastHash.HasValue || Imaging.Hamming(hash, _lastHash.Value) >= 10;
                    _lastHash = hash;
                    // RELIABLE keyboard signal: is the keyboard WINDOW actually visible? (not a pixel guess)
                    keyboard = KeyboardWindow.VisibleTop(_s.App.ProcessId, _s.MainRect()) >= 0;
                    // only PERSIST a screenshot when asked (a deliberate picture) - polling /scene must not
                    // save or push frames, or the live view floods with pictures.
                    if (persist) { file = $"shot_{Guid.NewGuid():N}.png"; bmp.Save(System.IO.Path.Combine(OutDir, file)); }
                }
            }
            catch { }

            if (persist && file != null)
                try { _s.Live?.AddStep(note ?? GuessScreen(), file); } catch { }

            var fg = ForegroundTitle();
            var sb = new StringBuilder("{");
            sb.Append("\"ok\":").Append(ok ? "true" : "false");
            sb.Append(",\"screen\":\"").Append(J(GuessScreen())).Append("\"");
            sb.Append(",\"changed\":").Append(changed ? "true" : "false");
            sb.Append(",\"keyboard\":").Append(keyboard ? "true" : "false");
            sb.Append(",\"blocked\":").Append(blocked ? "true" : "false");
            sb.Append(",\"foreground\":\"").Append(J(fg)).Append("\"");
            sb.Append(",\"foregroundIsApp\":").Append(fg.IndexOf("Exatouch", StringComparison.OrdinalIgnoreCase) >= 0 ? "true" : "false");
            sb.Append(",\"file\":\"").Append(J(file)).Append("\"");

            // window stack + ambiguity: if a window disappeared, or several are open, the UI should ASK
            // the user where we are / which layer to continue on (human-in-the-loop capture).
            var wins = EnumAppWindows();
            int extra = wins.Count(w => !w.IsMain);
            bool disappeared = wins.Count < _lastWinCount;
            bool needsInput = extra >= 2 || (disappeared && extra >= 1);
            _lastWinCount = wins.Count;
            sb.Append(",\"windowCount\":").Append(wins.Count);
            sb.Append(",\"extraWindows\":").Append(extra);
            sb.Append(",\"disappeared\":").Append(disappeared ? "true" : "false");
            sb.Append(",\"needsInput\":").Append(needsInput ? "true" : "false");
            sb.Append(",\"windows\":[");
            for (int i = 0; i < wins.Count; i++)
            {
                var w = wins[i]; if (i > 0) sb.Append(",");
                var title = string.IsNullOrEmpty(w.Title) ? (w.IsMain ? "Exatouch (main)" : "(untitled window)") : w.Title;
                sb.Append("{\"handle\":").Append(w.Handle.ToInt64())
                  .Append(",\"title\":\"").Append(J(title)).Append("\"")
                  .Append(",\"w\":").Append(w.W).Append(",\"h\":").Append(w.H)
                  .Append(",\"main\":").Append(w.IsMain ? "true" : "false")
                  .Append(",\"dialog\":").Append((!w.IsMain && w.W <= 760 && w.H <= 560) ? "true" : "false")
                  .Append("}");
            }
            sb.Append("]");
            if (note != null) sb.Append(",\"note\":\"").Append(J(note)).Append("\"");
            sb.Append("}");
            return sb.ToString();
        }

        // a quick "where are we" guess from the controls on screen
        private string GuessScreen()
        {
            var w = _s.Win; if (w == null) return "unknown";
            try
            {
                if (w.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tab)) != null ||
                    w.FindFirstDescendant(cf => cf.ByAutomationId("txtSKU")) != null) return "Item Detail";
                if (w.FindFirstDescendant(cf => cf.ByAutomationId("dgvList")) != null) return "Item List";
                if (w.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList")) != null) return "Items module";
                if (w.FindFirstDescendant(cf => cf.ByName("Items")) != null) return "Home";
            }
            catch { }
            return "unknown";
        }

        private FlaUI.Core.Capturing.CaptureImage CaptureMain()
        {
            var w = _s.Win;
            if (w != null && !w.IsOffscreen) return FlaUI.Core.Capturing.Capture.Element(w);
            return AppCapture.Grab(_s.App, _s.Automation, out _);
        }

        // a deliberate, persisted screenshot (used by the 📸 picture action)
        private string SaveShot()
        {
            try { using (var img = CaptureMain()) { var f = $"shot_{Guid.NewGuid():N}.png"; img.Bitmap.Save(System.IO.Path.Combine(OutDir, f)); return f; } }
            catch { return null; }
        }

        private IntPtr MainHandle()
        { try { return _s.Win?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero; } catch { return IntPtr.Zero; } }

        // a deliberate picture of a layer (PrintWindow, else screen-grab), boxing the interacted element
        private string SavePicture(IntPtr h, System.Drawing.Rectangle? highlight)
        {
            try
            {
                using (var bmp = CaptureLayer(h, WindowByHandle(h), out var origin))
                {
                    if (bmp == null) return null;
                    if (highlight.HasValue) HighlightBox(bmp, origin, highlight.Value);
                    var f = $"shot_{Guid.NewGuid():N}.png";
                    bmp.Save(System.IO.Path.Combine(OutDir, f));
                    return f;
                }
            }
            catch { return null; }
        }

        // Capture a window's OWN pixels via PrintWindow (renders even when occluded, and excludes the
        // separate on-screen-keyboard window). Falls back to a FlaUI screen-grab if PrintWindow comes back
        // blank (some custom-drawn controls don't honor it). Reports which path was used via _captureMethod.
        private System.Drawing.Bitmap CaptureLayer(IntPtr h, Window fallback, out System.Drawing.Point origin)
        {
            origin = System.Drawing.Point.Empty;
            try
            {
                var pw = PrintWindowBitmap(h);
                if (pw != null && !LooksBlank(pw))
                {
                    if (GetWindowRect(h, out var r)) origin = new System.Drawing.Point(r.Left, r.Top);
                    _captureMethod = "printwindow";
                    return pw;
                }
                pw?.Dispose();
            }
            catch { }
            try
            {
                var w = fallback ?? WindowByHandle(h) ?? _s.Win;
                if (w != null && !w.IsOffscreen)
                {
                    // PrintWindow failed - to capture ONLY the selected layer (not whatever overlaps it),
                    // bring it to the front first so the screen-grab can't include another form on top.
                    try { w.SetForeground(); System.Threading.Thread.Sleep(150); } catch { }
                    var br = w.BoundingRectangle; origin = new System.Drawing.Point(br.X, br.Y);
                    using (var img = FlaUI.Core.Capturing.Capture.Element(w)) { _captureMethod = "screen"; return new System.Drawing.Bitmap(img.Bitmap); }
                }
            }
            catch { }
            return null;
        }

        private System.Drawing.Bitmap PrintWindowBitmap(IntPtr h)
        {
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) return null;
            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            if (w <= 0 || ht <= 0 || w > 10000 || ht > 10000) return null;
            var bmp = new System.Drawing.Bitmap(w, ht);
            bool okp = false;
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                var hdc = g.GetHdc();
                try { okp = PrintWindow(h, hdc, PW_RENDERFULLCONTENT); } finally { g.ReleaseHdc(hdc); }
            }
            if (!okp) { bmp.Dispose(); return null; }
            return bmp;
        }

        // is the bitmap almost entirely near-black? (a failed PrintWindow) - then we should fall back
        private static bool LooksBlank(System.Drawing.Bitmap b)
        {
            int dark = 0, n = 0, sx = Math.Max(1, b.Width / 40), sy = Math.Max(1, b.Height / 40);
            for (int y = 2; y < b.Height; y += sy)
                for (int x = 2; x < b.Width; x += sx) { var p = b.GetPixel(x, y); n++; if (p.R < 12 && p.G < 12 && p.B < 12) dark++; }
            return n > 0 && dark * 100 / n > 92;
        }

        private static void HighlightBox(System.Drawing.Bitmap bmp, System.Drawing.Point origin, System.Drawing.Rectangle target)
        {
            try
            {
                int x = target.X - origin.X, y = target.Y - origin.Y;
                var box = System.Drawing.Rectangle.Intersect(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                                                             new System.Drawing.Rectangle(x - 3, y - 3, target.Width + 6, target.Height + 6));
                if (box.Width <= 0 || box.Height <= 0) return;
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 4))
                using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(50, System.Drawing.Color.Red)))
                { g.FillRectangle(fill, box); g.DrawRectangle(pen, box); }
            }
            catch { }
        }

        // a new window appearing -> auto-select it for the visor; a selected window closing -> back to main
        private void AutoSelectNewLayers(List<AppWin> wins)
        {
            foreach (var w in wins)
            {
                long hl = w.Handle.ToInt64();
                if (_known.Add(hl) && _seeded && !w.IsMain) _selected = w.Handle;   // NEW layer -> show it
            }
            _known.RemoveWhere(h => !wins.Any(w => w.Handle.ToInt64() == h));
            if (_selected != IntPtr.Zero && !wins.Any(w => w.Handle == _selected)) _selected = IntPtr.Zero; // gone -> main
            _seeded = true;
        }

        private const uint PW_RENDERFULLCONTENT = 2;
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

        // the window the visor shows + the picture captures (selected layer, else the main form)
        private Window SelectedWindow(out string name, out long handle)
        {
            name = "Exatouch (main)"; handle = 0;
            if (_selected != IntPtr.Zero)
            {
                var w = WindowByHandle(_selected);
                if (w != null) { name = NameOrTitle(w); handle = _selected.ToInt64(); return w; }
                _selected = IntPtr.Zero;   // the chosen layer is gone -> fall back to main
            }
            var m = _s.Win;
            try { if (m != null) handle = m.Properties.NativeWindowHandle.ValueOrDefault.ToInt64(); } catch { }
            return m;
        }

        // The VISOR: capture the selected layer to a FIXED file (overwritten each poll, never part of the
        // recording), and return which layer it is + the current window list so the chooser stays fresh.
        // A newly-appeared layer is auto-selected (shown in the visor); the user can still pick another.
        private string Preview()
        {
            var wins = EnumAppWindows();
            AutoSelectNewLayers(wins);   // a new window pops -> the visor jumps to it automatically

            string layer = "Exatouch (main)"; long handle = 0; bool keyboard = false; bool ok = false;
            try
            {
                var w = SelectedWindow(out layer, out handle);
                if (w != null && !w.IsOffscreen)
                {
                    keyboard = KeyboardWindow.VisibleTop(_s.App.ProcessId, w.BoundingRectangle) >= 0;
                    using (var bmp = CaptureLayer(new IntPtr(handle), w, out _))
                        if (bmp != null) { bmp.Save(System.IO.Path.Combine(OutDir, "visor.png")); ok = true; }
                }
            }
            catch { }

            var sb = new StringBuilder("{");
            sb.Append("\"ok\":").Append(ok ? "true" : "false");
            sb.Append(",\"file\":\"visor.png\"");
            sb.Append(",\"method\":\"").Append(_captureMethod).Append("\"");
            sb.Append(",\"layer\":\"").Append(J(layer)).Append("\"");
            sb.Append(",\"handle\":").Append(handle);
            sb.Append(",\"keyboard\":").Append(keyboard ? "true" : "false");
            sb.Append(",\"windows\":[");
            for (int i = 0; i < wins.Count; i++)
            {
                var w = wins[i]; if (i > 0) sb.Append(",");
                var title = string.IsNullOrEmpty(w.Title) ? (w.IsMain ? "Exatouch (main)" : "(untitled window)") : w.Title;
                bool sel = (handle != 0 && w.Handle.ToInt64() == handle) || (handle == 0 && w.IsMain && _selected == IntPtr.Zero);
                sb.Append("{\"handle\":").Append(w.Handle.ToInt64())
                  .Append(",\"title\":\"").Append(J(title)).Append("\"")
                  .Append(",\"w\":").Append(w.W).Append(",\"h\":").Append(w.H)
                  .Append(",\"main\":").Append(w.IsMain ? "true" : "false")
                  .Append(",\"dialog\":").Append((!w.IsMain && w.W <= 760 && w.H <= 560) ? "true" : "false")
                  .Append(",\"selected\":").Append(sel ? "true" : "false")
                  .Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private static string ForegroundTitle()
        {
            try { int n = GetWindowTextLength(GetForegroundWindow()); if (n <= 0) return ""; var sb = new StringBuilder(n + 1); GetWindowText(GetForegroundWindow(), sb, sb.Capacity); return sb.ToString(); }
            catch { return ""; }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);

        // ---- window-stack helpers (find layers / modals, choose where to continue) ----
        private struct AppWin { public IntPtr Handle; public string Title; public int W, H; public bool IsMain; }

        private List<AppWin> EnumAppWindows()
        {
            var list = new List<AppWin>();
            int appPid = -1; try { appPid = _s.App.ProcessId; } catch { return list; }   // app process gone -> no windows (never throw)
            IntPtr main = IntPtr.Zero;
            try { main = _s.Win.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != (uint)appPid) return true;
                if (!GetWindowRect(h, out var r)) return true;
                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (w < 60 || ht < 60) return true;                 // skip tiny utility/tooltip windows
                if (ht >= 340 && ht <= 430 && Math.Abs(w - 1024) <= 80) return true; // that's the on-screen keyboard, not a layer
                list.Add(new AppWin { Handle = h, Title = WinTitle(h), W = w, H = ht, IsMain = (h == main) });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        // dump every element matching a Name (or AutomationId) with its parent chain - to see WHY a
        // recorded selector resolves to the wrong control.
        private string Inspect(string name, string id)
        {
            var w = _s.Win;
            var sb = new StringBuilder("{\"ok\":true,\"query\":\"").Append(J(name ?? id ?? "")).Append("\",\"matches\":[");
            try
            {
                AutomationElement[] els = !string.IsNullOrEmpty(id)
                    ? w.FindAllDescendants(cf => cf.ByAutomationId(id))
                    : w.FindAllDescendants(cf => cf.ByName(name ?? ""));
                var walker = _s.Automation.TreeWalkerFactory.GetControlViewWalker();
                int n = 0;
                foreach (var e in els)
                {
                    if (n >= 25) break;
                    string eid = "", en = "", ect = "", pid = "", pn = "";
                    try { eid = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
                    try { en = e.Properties.Name.ValueOrDefault ?? ""; } catch { }
                    try { ect = e.Properties.ControlType.ValueOrDefault.ToString(); } catch { }
                    try { var p = walker.GetParent(e); if (p != null) { pid = p.Properties.AutomationId.ValueOrDefault ?? ""; pn = p.Properties.Name.ValueOrDefault ?? ""; } } catch { }
                    var r = e.BoundingRectangle;
                    if (n > 0) sb.Append(",");
                    sb.Append("{\"id\":\"").Append(J(eid)).Append("\",\"name\":\"").Append(J(en)).Append("\",\"ct\":\"").Append(J(ect))
                      .Append("\",\"parentId\":\"").Append(J(pid)).Append("\",\"parentName\":\"").Append(J(pn))
                      .Append("\",\"x\":").Append((int)r.X).Append(",\"y\":").Append((int)r.Y).Append(",\"w\":").Append((int)r.Width).Append(",\"h\":").Append((int)r.Height).Append("}");
                    n++;
                }
                sb.Append("],\"count\":").Append(n);
            }
            catch (Exception ex) { sb.Append("],\"error\":\"").Append(J(ex.Message)).Append("\""); }
            sb.Append("}");
            return sb.ToString();
        }

        private string Layers()
        {
            var wins = EnumAppWindows();
            var sb = new StringBuilder("{\"ok\":true,\"count\":").Append(wins.Count).Append(",\"windows\":[");
            for (int i = 0; i < wins.Count; i++)
            {
                var w = wins[i]; if (i > 0) sb.Append(",");
                var title = string.IsNullOrEmpty(w.Title) ? (w.IsMain ? "Exatouch (main)" : "(untitled window)") : w.Title;
                sb.Append("{\"handle\":").Append(w.Handle.ToInt64())
                  .Append(",\"title\":\"").Append(J(title)).Append("\"")
                  .Append(",\"w\":").Append(w.W).Append(",\"h\":").Append(w.H)
                  .Append(",\"main\":").Append(w.IsMain ? "true" : "false")
                  .Append(",\"dialog\":").Append((!w.IsMain && w.W <= 760 && w.H <= 560) ? "true" : "false").Append("}");
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private Window WindowByHandle(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            // FromHandle resolves ANY window (incl. modal dialogs that GetAllTopLevelWindows misses).
            try { var w = _s.Automation.FromHandle(h)?.AsWindow(); if (w != null) return w; } catch { }
            try { foreach (var w in _s.App.GetAllTopLevelWindows(_s.Automation))
                    if (w.Properties.NativeWindowHandle.ValueOrDefault.ToInt64() == h.ToInt64()) return w; }
            catch { }
            return null;
        }

        private static string NameOrTitle(Window w)
        { try { var t = w.Title; if (!string.IsNullOrEmpty(t)) return t; } catch { } try { return w.Properties.Name.ValueOrDefault ?? "window"; } catch { return "window"; } }

        private static string WinTitle(IntPtr h)
        { try { int n = GetWindowTextLength(h); if (n <= 0) return ""; var sb = new StringBuilder(n + 1); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); } catch { return ""; } }

        // ---- test inheritance ("extends"): load a parent's flattened flow, replay it, link the child ----
        // the parent of a recording (from extends.json), "" if it's a root test
        private string ParentOf(string name)
        {
            var f = System.IO.Path.Combine(OutDir, "extends.json");
            if (!System.IO.File.Exists(f)) return "";
            try { var d = _js.Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(f)); if (d != null && d.ContainsKey(name) && d[name] != null) return Convert.ToString(d[name]); }
            catch { }
            return "";
        }
        // a recording's effective steps = its parent-chain's steps (trunk) + its own diff (branch), flattened
        private List<Recorder.Step> LoadEffectiveSteps(string name, HashSet<string> seen = null)
        {
            seen = seen ?? new HashSet<string>();
            var outp = new List<Recorder.Step>();
            if (string.IsNullOrEmpty(name) || !seen.Add(name)) return outp;   // cycle guard
            var parent = ParentOf(name);
            if (!string.IsNullOrEmpty(parent)) outp.AddRange(LoadEffectiveSteps(parent, seen));
            var f = System.IO.Path.Combine(OutDir, "recording-" + name + ".json");
            if (System.IO.File.Exists(f))
            {
                try { foreach (var d in _js.Deserialize<List<Dictionary<string, object>>>(System.IO.File.ReadAllText(f))) outp.Add(ToStep(d)); }
                catch { }
            }
            return outp;
        }
        private static Recorder.Step ToStep(Dictionary<string, object> d)
        {
            Func<string, string> S = k => d.ContainsKey(k) && d[k] != null ? Convert.ToString(d[k]) : "";
            int idx = -1; if (d.ContainsKey("index") && d["index"] != null) int.TryParse(Convert.ToString(d["index"]), out idx);
            return new Recorder.Step { Action = S("action"), By = S("by"), Key = S("key"), Value = S("value"), ControlType = S("controlType"), Label = S("label"), Parent = S("parent"), Index = idx, File = S("file") };
        }
        // replay the parent's steps through the engine primitives - drives the app to the branch point
        private void ReplaySteps(List<Recorder.Step> steps)
        {
            foreach (var s in steps)
            {
                try
                {
                    switch (s.Action)
                    {
                        case "picture": break;
                        case "tab": _s.SelectTab(s.Key); break;
                        case "set":  _s.SetField(s.Key, s.Value); break;
                        case "doubleclick": if (s.By == "row") _s.OpenRow(); else _s.DblClickEl(s.By, s.Key, s.Value, s.Index, s.Parent, s.ControlType); break;
                        default:            if (s.By == "row") _s.OpenRow(); else _s.ClickEl(s.By, s.Key, s.Value, s.Index, s.Parent, s.ControlType); break;
                    }
                }
                catch { }
                Thread.Sleep(120);
            }
        }
        // link child -> parent in extends.json (merge, so multiple branches off one trunk accumulate)
        private void MergeExtends(string child, string parent)
        {
            var f = System.IO.Path.Combine(OutDir, "extends.json");
            Dictionary<string, object> d = null;
            if (System.IO.File.Exists(f)) { try { d = _js.Deserialize<Dictionary<string, object>>(System.IO.File.ReadAllText(f)); } catch { } }
            d = d ?? new Dictionary<string, object>();
            d[child] = parent;
            System.IO.File.WriteAllText(f, _js.Serialize(d));
        }

        private static string RecordingJson(List<Recorder.Step> steps)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i]; if (i > 0) sb.Append(",");
                sb.Append("{\"n\":").Append(i + 1)
                  .Append(",\"action\":\"").Append(J(s.Action)).Append("\"")
                  .Append(",\"by\":\"").Append(J(s.By)).Append("\"")
                  .Append(",\"key\":\"").Append(J(s.Key)).Append("\"")
                  .Append(",\"value\":\"").Append(J(s.Value)).Append("\"")
                  .Append(",\"controlType\":\"").Append(J(s.ControlType)).Append("\"")
                  .Append(",\"label\":\"").Append(J(s.Label)).Append("\"")
                  .Append(",\"file\":\"").Append(J(s.File)).Append("\"")
                  .Append(",\"index\":").Append(s.Index)
                  .Append(",\"parent\":\"").Append(J(s.Parent)).Append("\"")
                  .Append(",\"keyboard\":").Append(s.Keyboard ? "true" : "false").Append("}");
            }
            return sb.Append("]").ToString();
        }

        private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
