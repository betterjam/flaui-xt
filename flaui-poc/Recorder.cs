using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUiPoc
{
    /// <summary>
    /// Records a REAL user session against Exatouch and turns it into a replayable test. Instead of us
    /// synthesizing actions (and fighting things like the on-screen keyboard the app only hides on a
    /// genuine focus change), the human drives the app by hand; we watch with a low-level mouse hook and
    /// a UIA focus listener, and translate each interaction into a SEMANTIC step:
    ///
    ///   - a click            -> resolve the UIA element under the cursor (by AutomationId / Name / a grid
    ///                           row / a tab) and record "click id=txtSKU", not a raw (x,y) - so replay
    ///                           survives the window opening in a different place.
    ///   - typing into a field-> when focus leaves an editable, read its final .Text and record
    ///                           "set id=txtCost value=42" (the exact value the user entered).
    ///
    /// On stop we have an ordered list of steps + a screenshot per step. Generate() emits a NUnit test
    /// that replays the flow through the same UiSession rules (capture, highlight, modal-watch), so the
    /// recorded human flow becomes a dependable, re-runnable custom test.
    /// </summary>
    public sealed class Recorder : IDisposable
    {
        public sealed class Step
        {
            public string Action;       // click | doubleclick | set | tab
            public string By;           // id | name | row | tab
            public string Key;          // AutomationId or Name used to find it on replay
            public string Value;        // typed text (for set)
            public string ControlType;  // for the human-readable comment
            public string Label;        // friendly description
            public string File;         // screenshot filename
            public bool Keyboard;       // was the on-screen keyboard up at this step
            public int Index = -1;      // position among same-name siblings (disambiguates duplicate "Close")
            public string Parent;       // nearest ancestor's id/name, to scope the search to the right container
            public int Ox, Oy;          // for a picture: the captured window's screen top-left (to box clicks onto it)
        }

        private readonly FlaUI.Core.Application _app;
        private readonly FlaUI.UIA3.UIA3Automation _auto;
        private readonly string _outDir;
        private readonly LiveLog _live;                      // stream each recorded step to the live view
        private readonly List<Step> _steps = new List<Step>();
        private readonly object _gate = new object();

        // low-level mouse hook, pumped on its own thread
        private Thread _thread;
        private IntPtr _hook;
        private uint _hookThreadId;
        private volatile bool _running;

        // the editable the user is currently typing into (captured on entry, value read on leave)
        private AutomationElement _editTarget;
        private string _editId;
        private string _editStartText;   // the field's value when the user entered it (for "from X to Y")
        private string _editLabel;       // the field's human label (from its caption), for readable steps
        private Rectangle? _lastRect;    // screen bounds of the last interacted element (for picture highlight)

        public bool IsRecording => _running;
        public int Count { get { lock (_gate) return _steps.Count; } }
        /// <summary>Screen bounds of the last interacted element (so a picture can box it).</summary>
        public Rectangle? LastRect { get { lock (_gate) return _lastRect; } }
        /// <summary>When false (the default), recording an action does NOT take a screenshot - the user
        /// decides when to take a picture (the 📸 button -> MarkPicture). Actions are still recorded as
        /// text steps so the flow is captured without flooding the view with images.</summary>
        public bool CaptureShots { get; set; } = false;

        /// <summary>Should the GENERATED test auto-dismiss modals during replay? Off by default - the test
        /// sees the popups it triggers. The studio's checkbox turns it on per recording.</summary>
        public bool TestAutoDismiss { get; set; } = false;

        public Recorder(FlaUI.Core.Application app, FlaUI.UIA3.UIA3Automation auto, string outDir, LiveLog live = null)
        {
            _app = app; _auto = auto; _outDir = outDir; _live = live;
        }

        /// <summary>Wipe the captured flow back to empty AND clear the live view - start fresh.</summary>
        public void Clear()
        {
            lock (_gate) _steps.Clear();
            _editTarget = null; _editId = null; _editStartText = null; _editLabel = null; _lastRect = null;
            try { _live?.Reset(); } catch { }
        }

        public void Start()
        {
            if (_running) return;
            Clear();
            _running = true;
            _thread = new Thread(HookLoop) { IsBackground = true, Name = "rec-hook" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        public List<Step> Stop()
        {
            if (!_running) return Snapshot();
            _running = false;
            FlushPendingEdit();
            try { if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { }
            try { _thread?.Join(1500); } catch { }
            return Snapshot();
        }

        public List<Step> Snapshot() { lock (_gate) return new List<Step>(_steps); }

        // ---- the hook thread: install WH_MOUSE_LL, pump messages until QUIT ----
        private void HookLoop()
        {
            _hookThreadId = GetCurrentThreadId();
            using (var proc = System.Diagnostics.Process.GetCurrentProcess())
            using (var mod = proc.MainModule)
                _hook = SetWindowsHookEx(WH_MOUSE_LL, MouseProc, GetModuleHandle(mod.ModuleName), 0);

            MSG msg;
            while (_running && GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
            { TranslateMessage(ref msg); DispatchMessage(ref msg); }

            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
        }

        // a left-button-up anywhere is a user click: resolve what was under the cursor and record it
        private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (code >= 0 && (wParam == (IntPtr)WM_LBUTTONUP || wParam == (IntPtr)WM_LBUTTONDBLCLK))
                {
                    var ll = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    bool dbl = wParam == (IntPtr)WM_LBUTTONDBLCLK;
                    OnUserClick(ll.pt.x, ll.pt.y, dbl);
                }
            }
            catch { }
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        private void OnUserClick(int x, int y, bool dbl)
        {
            // only record clicks that landed inside our app
            AutomationElement el = null;
            try { el = _auto.FromPoint(new Point(x, y)); } catch { }
            if (el == null) return;
            try { if (el.Properties.ProcessId.ValueOrDefault != _app.ProcessId) { return; } } catch { return; }

            // a click moves focus: whatever we were typing into is now committed - record its final value
            FlushPendingEdit();

            var ct = SafeCtrlType(el);
            _lastRect = RectOf(el);   // remember WHERE this interaction happened (to box it in a picture)
            var vname = NameOf(el);   // the VISIBLE text on the control (what a human reads)

            // typing target? remember it, its caption, and its CURRENT value; record the net change on leave.
            if (ct == ControlType.Edit && IsEditable(el))
            {
                _editTarget = el;
                _editId = IdOrName(el, out _);
                _editLabel = FieldLabel(el, _editId);
                try { _editStartText = el.AsTextBox().Text ?? ""; } catch { _editStartText = ""; }
                return;
            }

            // a tab?
            if (ct == ControlType.TabItem)
            {
                Capture("tab", "tab", vname, null, "TabItem", $"Opened the \"{vname}\" tab");
                return;
            }

            // a grid row / cell (select or open an item)?
            if (IsGridRow(el))
            {
                var rn = string.IsNullOrEmpty(vname) ? "row" : vname;
                Capture(dbl ? "doubleclick" : "click", "row", rn, rn, "DataItem", dbl ? $"Opened \"{rn}\"" : $"Selected \"{rn}\"");
                return;
            }

            // a button / generic control. Identify it UNIQUELY so replay clicks the SAME element:
            //   - prefer a STABLE AutomationId; a purely-numeric/dynamic id (e.g. "6426290") is unreliable,
            //     so fall back to the visible Name then.
            //   - record the sibling INDEX (which "Close" of several) and the nearest container PARENT,
            //     so replay can pick the exact one instead of the first match.
            string id = ""; try { id = el.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            bool stableId = !string.IsNullOrEmpty(id) && !IsDynamicId(id);
            string by, key;
            if (stableId) { by = "id"; key = id; }
            else if (!string.IsNullOrEmpty(vname)) { by = "name"; key = vname; }
            else if (!string.IsNullOrEmpty(id)) { by = "id"; key = id; }
            else return;   // nothing to identify it by - skip

            int idx = SiblingIndex(el, vname, ct);   // -1 if unique
            string parent = ParentKey(el);
            string human = !string.IsNullOrEmpty(vname) ? vname : key;
            string tw = HumanType(ct);
            string lbl = (dbl ? "Double-clicked " : "Clicked ") + (string.IsNullOrEmpty(tw) ? "" : tw + " ") + $"\"{human}\""
                         + (idx >= 0 ? $" (#{idx + 1})" : "");
            Capture(dbl ? "doubleclick" : "click", by, key, vname, ct.ToString(), lbl, idx, parent);
        }

        // read the final text of the field the user was editing and record the NET change as plain text:
        // "txtCost changed value from \"23\" to \"42\"". No change -> nothing recorded.
        private void FlushPendingEdit()
        {
            var t = _editTarget; var id = _editId; var start = _editStartText ?? "";
            _editTarget = null; _editId = null; _editStartText = null; _editLabel = null;
            if (t == null || string.IsNullOrEmpty(id)) return;
            string val = null; try { val = t.AsTextBox().Text; } catch { }
            if (val == null || val == start) return;                      // unchanged -> not a step
            _lastRect = RectOf(t);                                        // box this field if a picture follows
            var fn = string.IsNullOrEmpty(_editLabel) ? id : _editLabel;  // the field's human caption
            var lbl = $"Set \"{fn}\" to \"{val}\"" + (string.IsNullOrEmpty(start) ? "" : $" (was \"{start}\")");
            Capture("set", "id", id, val, "Edit", lbl);
        }

        // record a step. By default NO screenshot is taken (the user decides when to take a picture);
        // the action is captured as a TEXT step so the flow log fills without flooding images.
        private void Capture(string action, string by, string key, string value, string ct, string label, int index = -1, string parent = null)
        {
            string file = null; bool kb = false;
            if (CaptureShots)
            {
                try
                {
                    Thread.Sleep(120);
                    var w = _app.GetMainWindow(_auto);
                    if (w != null && !w.IsOffscreen)
                        using (var img = FlaUI.Core.Capturing.Capture.Element(w))
                        {
                            kb = KeyboardWindow.VisibleTop(_app.ProcessId, w.BoundingRectangle) >= 0;
                            file = $"rec_{Guid.NewGuid():N}.png";
                            img.Bitmap.Save(Path.Combine(_outDir, file));
                        }
                }
                catch { }
            }
            lock (_gate)
            {
                var last = _steps.Count > 0 ? _steps[_steps.Count - 1] : null;
                if (last != null && last.Action == action && last.Key == key && last.Value == value)
                    return;                                            // drop an immediate duplicate
                _steps.Add(new Step { Action = action, By = by, Key = key, Value = value, ControlType = ct, Label = label, File = file, Keyboard = kb, Index = index, Parent = parent });
            }
            // NOTE: no highlight at record time - we just save the clicked element's INFO (id/name/index/
            // parent/type). The ACTUAL TEST highlights it later by resolving the element at replay.
            try { _live?.AddStep($"#{Count}  {label}", file); } catch { }   // text line in the action log
        }

        /// <summary>The user took a deliberate picture: snapshot the form NOW, draw a red box around the
        /// element from the last interaction (so the picture shows exactly what we detected), attach it to
        /// the flow as a "picture" step, and stream it to the video viewer.</summary>
        public string TakePicture(string note, Window target = null)
        {
            string file = null;
            try
            {
                var w = target ?? _app.GetMainWindow(_auto);
                if (w != null && !w.IsOffscreen)
                    using (var img = FlaUI.Core.Capturing.Capture.Element(w))
                    {
                        var bmp = img.Bitmap;
                        if (_lastRect.HasValue) Highlight(bmp, w.BoundingRectangle, _lastRect.Value);
                        file = $"shot_{Guid.NewGuid():N}.png";
                        bmp.Save(Path.Combine(_outDir, file));
                    }
            }
            catch { }
            MarkPicture(file, note);
            return file;
        }

        /// <summary>Attach a picture (already saved as <paramref name="file"/>) to the flow + video viewer.
        /// <paramref name="ox"/>/<paramref name="oy"/> are the captured window's screen top-left, so a later
        /// click can draw its highlight onto THIS picture (back-annotation).</summary>
        public void MarkPicture(string file, string note, int ox = 0, int oy = 0)
        {
            lock (_gate)
                _steps.Add(new Step { Action = "picture", By = "", Key = "", Value = "", ControlType = "", Label = note ?? "picture", File = file, Ox = ox, Oy = oy });
            try { _live?.AddStep(note ?? "picture", file); } catch { }   // plain ASCII - no emoji to mangle
        }

        private static Rectangle? RectOf(AutomationElement e)
        { try { var r = e.BoundingRectangle; if (r.Width > 0 && r.Height > 0) return r; } catch { } return null; }

        // an AutomationId we can't trust to be the same next run: purely numeric / very long / guid-like.
        private static bool IsDynamicId(string id)
        {
            if (string.IsNullOrEmpty(id)) return true;
            if (id.Length >= 6 && id.All(char.IsDigit)) return true;             // e.g. "6426290"
            if (id.Length >= 24 && id.Count(c => c == '-') >= 2) return true;     // guid-ish
            return false;
        }

        // index of this element among ALL same-name, same-type elements in the window (-1 if it's unique).
        // This is what lets replay pick the RIGHT "Close" when several exist.
        private int SiblingIndex(AutomationElement el, string name, ControlType ct)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            try
            {
                var w = _app.GetMainWindow(_auto); if (w == null) return -1;
                var all = w.FindAllDescendants(cf => cf.ByName(name).And(cf.ByControlType(ct)));
                if (all.Length <= 1) return -1;                                   // unique - no index needed
                for (int i = 0; i < all.Length; i++) { try { if (all[i].Equals(el)) return i; } catch { } }
            }
            catch { }
            return -1;
        }

        // the nearest ancestor that has a stable id/name - replay scopes the search to it so a duplicate
        // name in another container (a different dialog) is never matched by mistake.
        private string ParentKey(AutomationElement el)
        {
            try
            {
                var walker = _auto.TreeWalkerFactory.GetControlViewWalker();
                var p = walker.GetParent(el);
                int hops = 0;
                while (p != null && hops++ < 6)
                {
                    var pid = ""; try { pid = p.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
                    if (!string.IsNullOrEmpty(pid) && !IsDynamicId(pid)) return pid;
                    p = walker.GetParent(p);
                }
            }
            catch { }
            return null;
        }

        /// <summary>The label of the most recent NON-picture step (for picture context like "after ...").
        /// Skipping pictures stops notes from nesting ("after 📷 after 📷 ...").</summary>
        public string LastLabel
        {
            get { lock (_gate) { for (int i = _steps.Count - 1; i >= 0; i--) if (_steps[i].Action != "picture") return _steps[i].Label; return ""; } }
        }

        // a human word for a control type, for readable steps ("button", "item", ...)
        private static string HumanType(ControlType ct)
        {
            switch (ct)
            {
                case ControlType.Button: return "button";
                case ControlType.CheckBox: return "checkbox";
                case ControlType.RadioButton: return "option";
                case ControlType.MenuItem: return "menu";
                case ControlType.ListItem:
                case ControlType.DataItem: return "item";
                case ControlType.Hyperlink: return "link";
                case ControlType.Tab:
                case ControlType.TabItem: return "tab";
                default: return "";
            }
        }

        // the human caption of a field: its associated label (LabeledBy), else the automation id
        private string FieldLabel(AutomationElement e, string fallback)
        {
            try
            {
                var lb = e.Properties.LabeledBy.ValueOrDefault;
                if (lb != null) { var n = lb.Properties.Name.ValueOrDefault; if (!string.IsNullOrEmpty(n)) return n.TrimEnd(':', ' ', '*'); }
            }
            catch { }
            return fallback;
        }

        // draw a red box around the interacted element, translated from screen coords into the capture
        private static void Highlight(Bitmap bmp, Rectangle origin, Rectangle target)
        {
            try
            {
                int x = target.X - origin.X, y = target.Y - origin.Y;
                var box = Rectangle.Intersect(new Rectangle(0, 0, bmp.Width, bmp.Height),
                                              new Rectangle(x - 3, y - 3, target.Width + 6, target.Height + 6));
                if (box.Width <= 0 || box.Height <= 0) return;
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                using (var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 4))
                using (var fill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(50, System.Drawing.Color.Red)))
                { g.FillRectangle(fill, box); g.DrawRectangle(pen, box); }
            }
            catch { }
        }

        // ---- generate a replayable NUnit test from the recorded steps ----
        // REFERENCE model: emit a static BuildFlow that (if parentName is set) calls the parent's BuildFlow
        // first - by reference, not copied - then appends this test's OWN recorded steps. So the parent is the
        // single source of truth and editing it flows to every descendant.
        public string Generate(string className, string testName, string parentName = null)
        {
            var steps = Snapshot();   // this test's OWN steps only (the diff); the parent is referenced
            var sb = new StringBuilder();
            sb.AppendLine("using NUnit.Framework;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace FlaUiPoc");
            sb.AppendLine("{");
            sb.AppendLine("    /// <summary>Auto-generated from a RECORDED user session. A THIN test: it declares its OWN steps and,");
            sb.AppendLine("    /// if it extends a parent, REFERENCES the parent's BuildFlow (recursively). Engine does the rest.</summary>");
            if (!string.IsNullOrEmpty(parentName))
                sb.AppendLine($"    // EXTENDS {parentName} - this test's flow = the parent's BuildFlow (by reference) + the steps below.");
            sb.AppendLine("    [TestFixture]");
            sb.AppendLine($"    public class {className}");
            sb.AppendLine("    {");
            sb.AppendLine("        private static readonly string OutDir = Paths.OutDir;");
            sb.AppendLine("        private static readonly string ExePath = Paths.ExePath;");
            sb.AppendLine("        private UiSession _s;");
            sb.AppendLine();
            sb.AppendLine("        /// <summary>Append this test's flow: the parent's steps first (by reference, recursively), then");
            sb.AppendLine("        /// this test's own. Shared (static) so children can compose this test.</summary>");
            sb.AppendLine("        public static void BuildFlow(UiSession s, List<UiSession.FlowStep> flow)");
            sb.AppendLine("        {");
            if (!string.IsNullOrEmpty(parentName))
            {
                var pcls = char.ToUpper(parentName[0]) + parentName.Substring(1) + "Test";
                sb.AppendLine($"            {pcls}.BuildFlow(s, flow);   // REFERENCE the parent (trunk) - single source of truth, not a copy");
            }
            foreach (var s in steps)
            {
                if (s.Action == "picture") continue;   // pictures are skipped; each action self-documents
                var lbl = Esc(s.Label);
                string act;
                switch (s.Action)
                {
                    case "tab":        act = $"() => s.SelectTab(\"{Esc(s.Key)}\")"; break;
                    case "set":        act = $"() => s.SetField(\"{Esc(s.Key)}\", \"{Esc(s.Value)}\")"; break;
                    case "doubleclick":act = s.By == "row" ? "() => s.OpenRow()"
                                           : $"() => s.DblClickEl(\"{s.By}\", \"{Esc(s.Key)}\", \"{Esc(s.Value)}\", {s.Index}, \"{Esc(s.Parent)}\", \"{Esc(s.ControlType)}\")"; break;
                    default:           act = s.By == "row" ? "() => s.OpenRow()"
                                           : $"() => s.ClickEl(\"{s.By}\", \"{Esc(s.Key)}\", \"{Esc(s.Value)}\", {s.Index}, \"{Esc(s.Parent)}\", \"{Esc(s.ControlType)}\")"; break;
                }
                sb.AppendLine($"            flow.Add(new UiSession.FlowStep {{ Label = \"{lbl}\", Act = {act} }});");
            }
            sb.AppendLine("        }");
            sb.AppendLine();
            sb.AppendLine($"        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, \"{testName}\", \"Recorded flow: {testName}\", autoDismiss: {(TestAutoDismiss ? "true" : "false")});");
            sb.AppendLine($"        [OneTimeTearDown] public void Teardown() {{ _s?.WriteReport(\"Recorded - {testName}\", \"{testName}-report.html\"); _s?.Dispose(); }}");
            sb.AppendLine();
            sb.AppendLine("        [Test]");
            sb.AppendLine($"        public void {testName}()");
            sb.AppendLine("        {");
            sb.AppendLine("            var flow = new List<UiSession.FlowStep>();");
            sb.AppendLine("            BuildFlow(_s, flow);   // parent (by reference) + this test's own steps");
            sb.AppendLine("            _s.Report.NewCase(\"Replay of recorded session (\" + flow.Count + \" steps)\");");
            sb.AppendLine("            int failed = _s.RunFlow(flow);   // self-healing: a failed step steps BACK and retries");
            sb.AppendLine("            _s.Step(failed == 0 ? \"Recorded flow replayed faithfully - every step matched.\" : failed + \" step(s) could not be replayed - the flow diverged.\");");
            sb.AppendLine("            _s.Report.SetStatus(failed == 0 ? \"PASS\" : \"FAIL\"); _s.Finish(failed == 0 ? \"PASS\" : \"FAIL\");");
            sb.AppendLine("            Assert.That(failed, Is.EqualTo(0), failed + \" recorded step(s) could not be replayed - the app flow diverged from the recording.\");");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        // ---- helpers ----
        // escape for a C# string/comment literal - newlines/tabs MUST become spaces or the generated code breaks
        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
        private ControlType SafeCtrlType(AutomationElement e) { try { return e.Properties.ControlType.ValueOrDefault; } catch { return ControlType.Custom; } }
        // visible name with whitespace collapsed ("Item\nList" -> "Item List") so labels + replay keys are clean
        private static string NameOf(AutomationElement e)
        {
            try
            {
                var n = e.Properties.Name.ValueOrDefault ?? "";
                n = n.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
                while (n.Contains("  ")) n = n.Replace("  ", " ");
                return n.Trim();
            }
            catch { return ""; }
        }
        private bool IsEditable(AutomationElement e) { try { var t = e.AsTextBox(); return t.IsEnabled && !t.IsReadOnly; } catch { return false; } }
        private bool IsGridRow(AutomationElement e)
        { try { return (e.Properties.Name.ValueOrDefault ?? "").StartsWith("Row ") || e.Properties.ControlType.ValueOrDefault == ControlType.DataItem; } catch { return false; } }
        private string IdOrName(AutomationElement e, out string by)
        {
            by = "id";
            try { var id = e.Properties.AutomationId.ValueOrDefault; if (!string.IsNullOrEmpty(id)) return id; } catch { }
            by = "name";
            try { var n = e.Properties.Name.ValueOrDefault; if (!string.IsNullOrEmpty(n)) return n; } catch { }
            return "";
        }

        public void Dispose() { try { Stop(); } catch { } }

        // ---- Win32 ----
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203, WM_QUIT = 0x0012;
        private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc cb, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] private static extern IntPtr GetModuleHandle(string name);
    }
}
