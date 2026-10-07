using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WinScreen = System.Windows.Forms.Screen;

namespace FlaUiPoc
{
    /// <summary>
    /// ModalWatcher catches validation/warning popups that appear at ANY moment - including the ones
    /// the test never expected and the ones that open at some arbitrary screen position (Exatouch
    /// places forms wherever), which a checkpoint-only check would miss entirely.
    ///
    /// It runs on a BACKGROUND THREAD and deliberately uses ONLY Win32 + GDI (EnumWindows / GetWindowText
    /// / CopyFromScreen) - never UI Automation. UIA (COM) is not safe to call concurrently from a second
    /// thread while the main test drives it, so the watcher avoids it completely: it finds popup windows
    /// of the app's process by handle, reads their text via Win32, screenshots them via GDI, and (unless
    /// paused) dismisses them by posting a click to their Close/OK button.
    ///
    /// Detections are queued; the MAIN thread drains them into the report (DrainInto), so ReportBuilder /
    /// LiveLog / Learnings are only ever touched from one thread.
    /// </summary>
    public sealed class ModalWatcher : IDisposable
    {
        public sealed class Detection
        {
            public string Message;        // the body text of the popup (longest child string)
            public string MonitorName;    // e.g. "\\.\DISPLAY2"
            public bool PrimaryMonitor;
            public bool Dismissed;         // did the watcher click Close/OK?
            public string PngFile;         // screenshot filename in the results dir
            public string Stamp;           // HH:mm:ss when seen
        }

        private readonly int _pid;
        private readonly IntPtr _mainHwnd;
        private readonly string _outDir;
        private readonly ConcurrentQueue<Detection> _queue = new ConcurrentQueue<Detection>();
        private readonly HashSet<IntPtr> _openModals = new HashSet<IntPtr>();
        private Thread _thread;
        private volatile bool _stop;

        /// <summary>When true the watcher still DETECTS + CAPTURES popups but does NOT click them away -
        /// so a critical section (e.g. the Save retry loop) can own the dismissal itself.</summary>
        public volatile bool Paused;
        /// <summary>Whether the watcher clicks popups away. Default true. Set FALSE for tests that already
        /// have their own modal-dismissal/retry logic - then the watcher only records evidence and never
        /// interferes with their flow (the safe, purely-additive adoption mode used by StartFor).</summary>
        public volatile bool AutoDismiss = true;
        public int PollMs { get; set; } = 350;

        public ModalWatcher(int processId, IntPtr mainWindowHandle, string outDir)
        {
            _pid = processId; _mainHwnd = mainWindowHandle; _outDir = outDir;
        }

        public void Start()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "ModalWatcher" };
            _thread.Start();
        }

        /// <summary>One-liner for fixtures: resolve the app's pid + main window and start watching.
        /// Returns null (never throws) if the app handle can't be read yet.</summary>
        public static ModalWatcher StartFor(FlaUI.Core.Application app, FlaUI.UIA3.UIA3Automation automation, string outDir)
        {
            try
            {
                IntPtr h = IntPtr.Zero;
                try { h = app.GetMainWindow(automation)?.Properties.NativeWindowHandle.ValueOrDefault ?? IntPtr.Zero; } catch { }
                var w = new ModalWatcher(app.ProcessId, h, outDir);
                // AutoDismiss stays true: catch the popup, screenshot it, then click its Close/OK so the
                // flow isn't left blocked. Tests with their own save-retry pause the watcher around save.
                w.Start();
                return w;
            }
            catch { return null; }
        }

        /// <summary>Drain caught popups straight into a report + live log (on the calling thread).
        /// The simple adoption path for fixtures that don't have their own learnings pipeline.</summary>
        public int DrainTo(ReportBuilder report, LiveLog live, string context)
        {
            return DrainInto(d =>
            {
                var where = d.PrimaryMonitor ? "primary monitor" : $"monitor '{d.MonitorName}' (not primary)";
                var line = $"POPUP caught during '{context}' at {d.Stamp} on {where}: \"{d.Message}\"" +
                           (d.Dismissed ? " - auto-dismissed." : " - left open.");
                var png = d.PngFile != null ? Path.Combine(_outDir, d.PngFile) : null;
                report?.AddStep(line, png);
                live?.AddStep(line, d.PngFile);
            });
        }

        /// <summary>Move all popups seen since the last drain into <paramref name="sink"/> (called on the
        /// main thread, so the report/learnings stay single-threaded). Returns how many were drained.</summary>
        public int DrainInto(Action<Detection> sink)
        {
            int n = 0;
            while (_queue.TryDequeue(out var d)) { try { sink(d); } catch { } n++; }
            return n;
        }

        public void Dispose()
        {
            _stop = true;
            try { _thread?.Join(1500); } catch { }
        }

        private void Loop()
        {
            while (!_stop)
            {
                try { Scan(); } catch { /* never let the watcher throw */ }
                Thread.Sleep(PollMs);
            }
        }

        private void Scan()
        {
            var current = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out int wpid);
                if (wpid != _pid) return true;          // only our app's windows
                if (h == _mainHwnd) return true;        // never the main shell
                if (!LooksLikeDialog(h, out var closeBtn)) return true;
                current.Add(h);
                if (!_openModals.Contains(h))
                {
                    _openModals.Add(h);                 // first time we see THIS popup
                    Capture(h, closeBtn);
                }
                return true;
            }, IntPtr.Zero);

            // forget popups that have since closed, so a future one with a recycled handle re-fires
            _openModals.RemoveWhere(h => !current.Contains(h));
        }

        // A "dialog" = an app window (not main) that is a validation popup. We detect it two ways, because
        // Exatouch's Close button is sometimes a custom-drawn control whose TEXT Win32 can't read:
        //   (a) a child button captioned Close / OK / Yes  -> definitely a dialog; OR
        //   (b) a SMALL (dialog-sized) window that has any button child -> a popup even if its text is
        //       unreadable (this is what was letting the "future price" modal slip through).
        private bool LooksLikeDialog(IntPtr hwnd, out IntPtr closeButton)
        {
            closeButton = IntPtr.Zero;
            IntPtr named = IntPtr.Zero, anyButton = IntPtr.Zero;
            EnumChildWindows(hwnd, (child, l) =>
            {
                if (ClassOf(child).IndexOf("BUTTON", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (anyButton == IntPtr.Zero) anyButton = child;
                    var txt = GetText(child).Trim();
                    if (txt.Equals("Close", StringComparison.OrdinalIgnoreCase) ||
                        txt.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                        txt.Equals("Yes", StringComparison.OrdinalIgnoreCase))
                        named = child;
                }
                return true;
            }, IntPtr.Zero);
            closeButton = named != IntPtr.Zero ? named : anyButton;
            if (named != IntPtr.Zero) return true;
            return anyButton != IntPtr.Zero && IsDialogSized(hwnd);
        }

        // A validation popup is small; the main shell and the item-detail form are large.
        private bool IsDialogSized(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var r)) return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            return w > 50 && h > 50 && w <= 760 && h <= 520;
        }

        private static string ClassOf(IntPtr hwnd)
        {
            try { var sb = new StringBuilder(256); GetClassName(hwnd, sb, sb.Capacity); return sb.ToString(); }
            catch { return ""; }
        }

        private void Capture(IntPtr hwnd, IntPtr closeButton)
        {
            var det = new Detection { Stamp = DateTime.Now.ToString("HH:mm:ss") };

            // message = the longest text among the popup's child controls
            string longest = "";
            EnumChildWindows(hwnd, (child, l) =>
            {
                var t = GetText(child);
                if (!string.IsNullOrWhiteSpace(t) && t.Length > longest.Length &&
                    !t.Equals("Close", StringComparison.OrdinalIgnoreCase) &&
                    !t.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    longest = t;
                return true;
            }, IntPtr.Zero);
            det.Message = string.IsNullOrWhiteSpace(longest) ? "(no message text)" : longest.Trim();

            // where did it open? (monitor / primary)
            if (GetWindowRect(hwnd, out var r))
            {
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                try
                {
                    var scr = WinScreen.FromRectangle(rect);
                    det.MonitorName = scr.DeviceName; det.PrimaryMonitor = scr.Primary;
                }
                catch { }

                // GDI screenshot of just the popup (thread-safe; no UIA)
                try
                {
                    int w = Math.Max(1, rect.Width), h = Math.Max(1, rect.Height);
                    var file = $"modal_{Guid.NewGuid():N}.png";
                    using (var bmp = new Bitmap(w, h))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new Size(w, h));
                        bmp.Save(Path.Combine(_outDir, file));
                    }
                    det.PngFile = file;
                }
                catch { }
            }

            // dismiss (Win32, no UIA, no mouse move): click the button AND post WM_CLOSE to the popup.
            // Belt-and-suspenders because Exatouch's Close can be a custom button that ignores BM_CLICK -
            // WM_CLOSE then closes the popup form itself. (We only reach here for confirmed dialogs.)
            if (AutoDismiss && !Paused)
            {
                if (closeButton != IntPtr.Zero)
                    try { SendMessage(closeButton, BM_CLICK, IntPtr.Zero, IntPtr.Zero); det.Dismissed = true; } catch { }
                try { PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); det.Dismissed = true; } catch { }
            }

            _queue.Enqueue(det);
        }

        private static string GetText(IntPtr hwnd)
        {
            // Exatouch is ANOTHER process: GetWindowText returns "" for its child controls (labels), so
            // we ask the control directly with WM_GETTEXT (works cross-process). Timeout-guarded so a
            // hung target can never stall the watcher.
            var sb = new StringBuilder(1024);
            var ok = SendMessageTimeout(hwnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb, SMTO_ABORTIFHUNG, 250, out _);
            if (ok != IntPtr.Zero && sb.Length > 0) return sb.ToString();
            // fallback for caption-bearing windows
            int len = GetWindowTextLength(hwnd);
            if (len > 0) { var cap = new StringBuilder(len + 1); GetWindowText(hwnd, cap, cap.Capacity); return cap.ToString(); }
            return "";
        }

        // --- Win32 ---------------------------------------------------------------------------------
        private const uint BM_CLICK = 0x00F5;
        private const uint WM_GETTEXT = 0x000D;
        private const uint WM_CLOSE = 0x0010;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam, uint flags, uint timeout, out IntPtr result);
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr p);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int pid);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    }
}
