using System;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUiPoc
{
    /// <summary>
    /// Interactive TEST DEBUGGER. When a test runs in debug mode (env EXA_DEBUG=1), UiSession starts this
    /// little HTTP control server and RunFlow GATES before every step. So you can pause at a precise
    /// instant, then I jump in and read the LIVE app status right at that point - the current screen, the
    /// open layers/modals, and any element by name/id - then we step one action at a time or resume.
    /// Nothing is dismissed or done automatically; you drive the pause.
    ///
    ///   POST /pause /resume /step /stop
    ///   GET  /status                  -> {paused, step, total, label, screen}
    ///   GET  /scene                   -> saves a fresh screenshot + state (so the dashboard can show it)
    ///   GET  /layers                  -> open windows/modals
    ///   GET  /inspect?name=Pay        -> every element named "Pay" with its type + parent (or ?id=btnOK)
    /// </summary>
    public sealed class DebugBridge : IDisposable
    {
        private readonly UiSession _s;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _run = true;
        private volatile bool _paused = false;     // you trigger the pause (precise instant)
        private volatile bool _stepOnce = false;
        private volatile bool _stopped = false;    // hard stop: RunFlow aborts the remaining steps
        public bool Stopped => _stopped;
        private int _curStep, _total;
        private string _curLabel = "";

        public DebugBridge(UiSession s, int port = 5201) { _s = s; Start(port); }

        private void Start(int port)
        {
            try { _listener = new HttpListener(); _listener.Prefixes.Add($"http://localhost:{port}/"); _listener.Start(); }
            catch { _listener = null; return; }
            _thread = new Thread(Loop) { IsBackground = true, Name = "debug-bridge" };
            _thread.Start();
        }

        /// <summary>RunFlow calls this before each step; it BLOCKS while paused (until /resume or /step).</summary>
        public void Gate(int step, int total, string label)
        {
            _curStep = step; _total = total; _curLabel = label ?? "";
            while (_run)
            {
                if (_stopped) return;     // stop requested -> RunFlow checks Stopped and breaks
                if (!_paused) return;
                if (_stepOnce) { _stepOnce = false; return; }
                Thread.Sleep(150);
            }
        }

        /// <summary>After the last step, HOLD (force-paused) so the run can't finish/close on its own - the
        /// app stays open for final inspection until you press Stop (or Resume to let it complete + close).</summary>
        public void HoldEnd()
        {
            _paused = true; _curStep = _total; _curLabel = "✔ flow finished — paused (Stop to end, Resume to close)";
            while (_run)
            {
                if (_stopped) return;
                if (_stepOnce) { _stepOnce = false; return; }
                if (!_paused) return;     // explicit Resume = let it complete + close
                Thread.Sleep(150);
            }
        }

        private void Loop()
        {
            while (_run)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); } catch { break; }
                string json;
                try { json = Handle(ctx); } catch (Exception ex) { json = "{\"ok\":false,\"error\":\"" + J(ex.Message) + "\"}"; }
                var buf = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.AddHeader("Access-Control-Allow-Origin", "*");
                try { ctx.Response.OutputStream.Write(buf, 0, buf.Length); ctx.Response.OutputStream.Close(); } catch { }
            }
            try { _listener?.Stop(); } catch { }
        }

        private string Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url.AbsolutePath.ToLowerInvariant();
            var q = ctx.Request.QueryString;
            switch (path)
            {
                case "/pause": _paused = true; return Status();
                case "/resume": _paused = false; _stepOnce = false; return Status();
                case "/step": _stepOnce = true; return Status();
                case "/status": return Status();
                case "/scene": return Scene();
                case "/layers": return Layers();
                case "/inspect": return Inspect(q["name"], q["id"]);
                case "/at": return ElementAtCursor();
                case "/stop": _stopped = true; _paused = false; return "{\"ok\":true,\"stopped\":true}";
                default: return "{\"ok\":false,\"error\":\"unknown path\"}";
            }
        }

        private string Status() =>
            "{\"ok\":true,\"paused\":" + (_paused ? "true" : "false")
            + ",\"step\":" + _curStep + ",\"total\":" + _total
            + ",\"label\":\"" + J(_curLabel) + "\",\"screen\":\"" + J(GuessScreen()) + "\"}";

        private string Scene()
        {
            string file = null;
            try
            {
                var w = _s.Win;
                if (w != null && !w.IsOffscreen)
                    using (var img = FlaUI.Core.Capturing.Capture.Element(w))
                    {
                        file = $"debug_{Guid.NewGuid():N}.png";
                        img.Bitmap.Save(System.IO.Path.Combine(_s.OutDir, file));
                    }
            }
            catch { }
            return "{\"ok\":true,\"paused\":" + (_paused ? "true" : "false")
                 + ",\"step\":" + _curStep + ",\"total\":" + _total
                 + ",\"label\":\"" + J(_curLabel) + "\",\"screen\":\"" + J(GuessScreen()) + "\",\"file\":\"" + J(file) + "\"}";
        }

        private string GuessScreen()
        {
            var w = _s.Win; if (w == null) return "unknown";
            try
            {
                if (w.FindFirstDescendant(cf => cf.ByAutomationId("tpgRegister")) != null) return "Register";
                if (w.FindFirstDescendant(cf => cf.ByAutomationId("txtSKU")) != null) return "Item Detail";
                if (w.FindFirstDescendant(cf => cf.ByAutomationId("dgvList")) != null) return "Item List";
                if (w.FindFirstDescendant(cf => cf.ByName("Items")) != null) return "Home";
            }
            catch { }
            return "unknown";
        }

        // every element matching a Name (or AutomationId), with its type + parent - to see exactly what a
        // selector would resolve to at this instant.
        private string Inspect(string name, string id)
        {
            var w = _s.Win;
            var sb = new StringBuilder("{\"ok\":true,\"query\":\"").Append(J(name ?? id ?? "")).Append("\",\"matches\":[");
            try
            {
                var els = !string.IsNullOrEmpty(id)
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
            return sb.Append("}").ToString();
        }

        // the element currently under the mouse cursor (point at it in the app -> see what it is). Lets the
        // UI highlight the matching entry in its element list. Non-invasive: hovering changes no app state.
        private string ElementAtCursor()
        {
            if (!GetCursorPos(out var pt)) return "{\"ok\":false}";
            AutomationElement e = null;
            try { e = _s.Automation.FromPoint(new System.Drawing.Point(pt.X, pt.Y)); } catch { }
            if (e == null) return "{\"ok\":false,\"x\":" + pt.X + ",\"y\":" + pt.Y + "}";
            string eid = "", en = "", ect = "", pid = "", pn = "";
            try { eid = e.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
            try { en = e.Properties.Name.ValueOrDefault ?? ""; } catch { }
            try { ect = e.Properties.ControlType.ValueOrDefault.ToString(); } catch { }
            try { var p = _s.Automation.TreeWalkerFactory.GetControlViewWalker().GetParent(e); if (p != null) { pid = p.Properties.AutomationId.ValueOrDefault ?? ""; pn = p.Properties.Name.ValueOrDefault ?? ""; } } catch { }
            var r = e.BoundingRectangle;
            return "{\"ok\":true,\"id\":\"" + J(eid) + "\",\"name\":\"" + J(en) + "\",\"ct\":\"" + J(ect)
                 + "\",\"parentId\":\"" + J(pid) + "\",\"parentName\":\"" + J(pn) + "\",\"x\":" + (int)r.X + ",\"y\":" + (int)r.Y + ",\"w\":" + (int)r.Width + ",\"h\":" + (int)r.Height + "}";
        }

        private string Layers()
        {
            var sb = new StringBuilder("{\"ok\":true,\"windows\":[");
            IntPtr main = IntPtr.Zero; try { main = _s.Win.Properties.NativeWindowHandle.ValueOrDefault; } catch { }
            int n = 0;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != (uint)_s.App.ProcessId) return true;
                if (!GetWindowRect(h, out var r)) return true;
                int wdt = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (wdt < 60 || ht < 60) return true;
                if (ht >= 340 && ht <= 430 && Math.Abs(wdt - 1024) <= 80) return true;   // keyboard
                var t = WinTitle(h);
                if (n > 0) sb.Append(",");
                sb.Append("{\"handle\":").Append(h.ToInt64()).Append(",\"title\":\"").Append(J(string.IsNullOrEmpty(t) ? (h == main ? "Exatouch (main)" : "(untitled)") : t))
                  .Append("\",\"w\":").Append(wdt).Append(",\"h\":").Append(ht).Append(",\"main\":").Append(h == main ? "true" : "false").Append("}");
                n++;
                return true;
            }, IntPtr.Zero);
            return sb.Append("],\"count\":").Append(n).Append("}").ToString();
        }

        public void Dispose() { _run = false; _paused = false; try { _listener?.Stop(); } catch { } }

        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

        private delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        private static string WinTitle(IntPtr h) { try { int len = GetWindowTextLength(h); if (len <= 0) return ""; var sb = new StringBuilder(len + 1); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); } catch { return ""; } }
    }
}
