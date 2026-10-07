using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using FlaUI.Core.AutomationElements;

namespace FlaUiPoc
{
    /// <summary>
    /// Diag is the MACHINE-LEVEL diagnostics recorder: separate from the human-friendly report, it
    /// captures the REAL situation the automation sees so the AI agent can troubleshoot from facts,
    /// not from prose. It writes one JSON object per line to diag-&lt;test&gt;.jsonl.
    ///
    /// The headline check is <see cref="Clickable"/>: before a click it asks Windows WindowFromPoint at
    /// the target's click point - if the window actually under that pixel is NOT our target's window,
    /// the click is being eaten by something on top (a modal/overlay). That is the "noise" you hear when
    /// the automation clicks a window that a new one has covered, and the reason a step gets "stuck".
    /// It also snapshots the whole window scene (foreground + every top-level window + overlaps) and the
    /// target element's real state (enabled / offscreen / readonly / rect).
    /// </summary>
    public sealed class Diag
    {
        private readonly string _path;
        private readonly int _pid;
        private readonly object _lock = new object();

        public Diag(string outDir, string testName, int processId)
        {
            Directory.CreateDirectory(outDir);
            _path = System.IO.Path.Combine(outDir, $"diag-{testName}.jsonl");
            _pid = processId;
            try { File.WriteAllText(_path, ""); } catch { }   // truncate per run
        }

        public string LogPath => _path;

        // ---- public recording API --------------------------------------------------------------

        /// <summary>A free-form structured note.</summary>
        public void Note(string ctx, string msg) =>
            Write("note", new[] { Kv("ctx", ctx), Kv("msg", msg) });

        /// <summary>An exception with type + message (full machine detail).</summary>
        public void Error(string ctx, Exception ex) =>
            Write("error", new[] { Kv("ctx", ctx), Kv("type", ex?.GetType().Name), Kv("msg", ex?.Message) });

        /// <summary>Snapshot the whole window scene: foreground window, and every top-level window of our
        /// process with its rect - so an overlapping modal/popup is visible in the record.</summary>
        public void Scene(string ctx)
        {
            try
            {
                var fg = GetForegroundWindow();
                GetWindowThreadProcessId(fg, out int fgPid);
                var wins = TopLevelOfProcess();
                var fields = new List<string>
                {
                    Kv("ctx", ctx),
                    Kv("foreground", Title(fg)),
                    KvRaw("foregroundIsOurApp", (fgPid == _pid).ToString().ToLowerInvariant()),
                    KvRaw("topLevelCount", wins.Count.ToString()),
                    KvRaw("windows", "[" + string.Join(",", wins.Select(w =>
                        "{" + string.Join(",", new[] {
                            Kv("title", w.title), Kv("class", w.cls),
                            KvRaw("rect", $"[{w.r.Left},{w.r.Top},{w.r.Right},{w.r.Bottom}]"),
                            KvRaw("foreground", (w.hwnd == fg).ToString().ToLowerInvariant())
                        }) + "}")) + "]")
                };
                // more than one of our top-level windows = something is overlaying the main screen
                if (wins.Count > 1) fields.Add(KvRaw("overlayPresent", "true"));
                Write("scene", fields);
            }
            catch (Exception ex) { Error("scene:" + ctx, ex); }
        }

        /// <summary>Record the target's real state AND whether it's actually hittable. Returns true if a
        /// click should land on the target; false if another window is covering the click point (BLOCKED).
        /// Either way the facts are written to the diag log.</summary>
        public bool Clickable(string action, AutomationElement el)
        {
            var fields = new List<string> { Kv("action", action) };
            bool ok = false;
            try
            {
                string id = "", name = ""; bool enabled = false, offscreen = true;
                System.Drawing.Rectangle r = default;
                try { id = el.Properties.AutomationId.ValueOrDefault ?? ""; } catch { }
                try { name = el.Properties.Name.ValueOrDefault ?? ""; } catch { }
                try { enabled = el.IsEnabled; } catch { }
                try { offscreen = el.IsOffscreen; } catch { }
                try { r = el.BoundingRectangle; } catch { }

                fields.Add(Kv("id", id));
                fields.Add(Kv("name", name));
                fields.Add(KvRaw("enabled", enabled.ToString().ToLowerInvariant()));
                fields.Add(KvRaw("offscreen", offscreen.ToString().ToLowerInvariant()));
                fields.Add(KvRaw("rect", $"[{(int)r.Left},{(int)r.Top},{(int)r.Right},{(int)r.Bottom}]"));

                if (r.Width > 0 && r.Height > 0)
                {
                    int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
                    fields.Add(KvRaw("clickPoint", $"[{cx},{cy}]"));

                    var atPoint = WindowFromPoint(new POINT { x = cx, y = cy });
                    var rootAtPoint = GetAncestor(atPoint, GA_ROOT);
                    GetWindowThreadProcessId(rootAtPoint, out int pidAtPoint);

                    // the element's own top-level window
                    var elHwnd = NativeHandle(el);
                    var elRoot = elHwnd != IntPtr.Zero ? GetAncestor(elHwnd, GA_ROOT) : IntPtr.Zero;

                    fields.Add(Kv("windowUnderPoint", Title(rootAtPoint)));
                    fields.Add(Kv("classUnderPoint", ClassOf(atPoint)));

                    bool otherProcess = pidAtPoint != 0 && pidAtPoint != _pid;
                    bool otherWindow = elRoot != IntPtr.Zero && rootAtPoint != IntPtr.Zero && rootAtPoint != elRoot;
                    if (offscreen) { fields.Add(KvRaw("blocked", "true")); fields.Add(Kv("blockedReason", "target offscreen")); }
                    else if (!enabled) { fields.Add(KvRaw("blocked", "true")); fields.Add(Kv("blockedReason", "target disabled")); }
                    else if (otherProcess) { fields.Add(KvRaw("blocked", "true")); fields.Add(Kv("blockedReason", "covered by another PROCESS window")); }
                    else if (otherWindow) { fields.Add(KvRaw("blocked", "true")); fields.Add(Kv("blockedReason", "covered by another top-level window of our app (modal/overlay)")); }
                    else { ok = enabled && !offscreen; fields.Add(KvRaw("blocked", (!ok).ToString().ToLowerInvariant())); }
                }
                else { fields.Add(KvRaw("blocked", "true")); fields.Add(Kv("blockedReason", "no bounding rectangle")); }
            }
            catch (Exception ex) { fields.Add(Kv("probeError", ex.Message)); }
            Write("clickable", fields);
            return ok;
        }

        // ---- helpers ---------------------------------------------------------------------------
        private List<(IntPtr hwnd, string title, string cls, RECT r)> TopLevelOfProcess()
        {
            var list = new List<(IntPtr, string, string, RECT)>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out int wp);
                if (wp != _pid) return true;
                GetWindowRect(h, out var r);
                list.Add((h, Title(h), ClassOf(h), r));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private static IntPtr NativeHandle(AutomationElement el)
        {
            try { return el.Properties.NativeWindowHandle.ValueOrDefault; } catch { return IntPtr.Zero; }
        }

        private static string Title(IntPtr h)
        {
            try { int n = GetWindowTextLength(h); if (n <= 0) return ""; var sb = new StringBuilder(n + 1); GetWindowText(h, sb, sb.Capacity); return sb.ToString(); }
            catch { return ""; }
        }
        private static string ClassOf(IntPtr h)
        {
            try { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); } catch { return ""; }
        }

        private void Write(string kind, IEnumerable<string> fields)
        {
            try
            {
                var line = "{" + Kv("t", DateTime.Now.ToString("HH:mm:ss.fff")) + "," + Kv("kind", kind) +
                           "," + string.Join(",", fields) + "}";
                lock (_lock) { File.AppendAllText(_path, line + "\n"); }
            }
            catch { }
        }

        private static string Kv(string k, string v) => $"\"{k}\":\"{J(v)}\"";
        private static string KvRaw(string k, string v) => $"\"{k}\":{v}";
        private static string J(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

        // ---- Win32 -----------------------------------------------------------------------------
        private const uint GA_ROOT = 2;
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    }
}
