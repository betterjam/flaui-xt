using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using WinScreen = System.Windows.Forms.Screen;   // only need Screen; avoid Application ambiguity

namespace FlaUiPoc
{
    /// <summary>
    /// WindowTracker catches the case where Exatouch opens a form as its OWN top-level window that
    /// can land ANYWHERE on screen - including a second monitor (e.g. the CFD/customer display) or
    /// an offset position - which GetMainWindow() never sees and a fixed-position test would miss.
    ///
    /// Usage:
    ///   var snap = tracker.Snapshot();                       // before the action
    ///   button.Click();                                      // opens the form somewhere
    ///   var form = tracker.WaitForNewWindow(snap);           // the form, wherever it opened
    ///   if (form != null) {
    ///       var info = tracker.Describe(form.Window);        // position + monitor + is-it-unexpected
    ///       _frames.Capture(form.Window);                    // screenshot the REAL form, not the main window
    ///   }
    ///
    /// It compares the set of top-level window handles before/after, so a brand-new window is detected
    /// regardless of where it rendered. It also reports the screen/monitor the form is on and whether
    /// that looks unexpected (not on the primary monitor, or far from the main window).
    /// </summary>
    public sealed class WindowTracker
    {
        private readonly Application _app;
        private readonly FlaUI.UIA3.UIA3Automation _automation;

        public WindowTracker(Application app, FlaUI.UIA3.UIA3Automation automation)
        {
            _app = app; _automation = automation;
        }

        /// <summary>A point-in-time set of the top-level window handles + their rectangles.</summary>
        public sealed class Snap
        {
            public Dictionary<IntPtr, Rectangle> Windows = new Dictionary<IntPtr, Rectangle>();
        }

        public sealed class NewWindow
        {
            public Window Window;
            public Rectangle Rect;       // where it opened (screen coords)
        }

        /// <summary>Result of describing a window's placement.</summary>
        public sealed class Placement
        {
            public Rectangle Rect;
            public string MonitorName;   // e.g. "\\.\DISPLAY2"
            public bool PrimaryMonitor;  // is it on the primary display?
            public bool Unexpected;      // not primary, or far from the main window
            public string Summary;       // human line for the step text
        }

        /// <summary>Capture the current set of top-level windows.</summary>
        public Snap Snapshot()
        {
            var s = new Snap();
            foreach (var w in SafeTopLevel())
            {
                try { s.Windows[w.Properties.NativeWindowHandle.ValueOrDefault] = ToRect(w.BoundingRectangle); }
                catch { /* a window may vanish mid-enumeration */ }
            }
            return s;
        }

        /// <summary>
        /// Poll for a top-level window that did NOT exist in <paramref name="before"/> - the form that
        /// just opened, wherever it landed. Returns null if none appeared within the timeout.
        /// </summary>
        public NewWindow WaitForNewWindow(Snap before, int timeoutMs = 6000, int pollMs = 250)
        {
            int waited = 0;
            while (waited <= timeoutMs)
            {
                foreach (var w in SafeTopLevel())
                {
                    IntPtr h;
                    try { h = w.Properties.NativeWindowHandle.ValueOrDefault; } catch { continue; }
                    if (h == IntPtr.Zero) continue;
                    if (!before.Windows.ContainsKey(h))
                    {
                        // a genuinely new top-level window
                        try { return new NewWindow { Window = w, Rect = ToRect(w.BoundingRectangle) }; }
                        catch { }
                    }
                }
                System.Threading.Thread.Sleep(pollMs);
                waited += pollMs;
            }
            return null;
        }

        /// <summary>Describe where a window opened: which monitor, and whether that's unexpected.</summary>
        public Placement Describe(Window win, Window mainWindow = null)
        {
            var p = new Placement();
            try { p.Rect = ToRect(win.BoundingRectangle); } catch { p.Rect = Rectangle.Empty; }

            var screen = WinScreen.FromRectangle(p.Rect);
            p.MonitorName = screen.DeviceName;
            p.PrimaryMonitor = screen.Primary;

            // "unexpected" = not on the primary monitor, OR its center is well outside the main window's
            // monitor (the form drifted to another screen / a far corner).
            bool farFromMain = false;
            if (mainWindow != null)
            {
                try
                {
                    var mainScreen = WinScreen.FromRectangle(ToRect(mainWindow.BoundingRectangle));
                    farFromMain = mainScreen.DeviceName != screen.DeviceName;
                }
                catch { }
            }
            p.Unexpected = !p.PrimaryMonitor || farFromMain;

            var mon = p.PrimaryMonitor ? "primary monitor" : $"monitor '{screen.DeviceName}' (NOT primary)";
            p.Summary = $"opened at ({p.Rect.X},{p.Rect.Y}) {p.Rect.Width}x{p.Rect.Height} on {mon}"
                      + (p.Unexpected ? " - UNEXPECTED position" : "");
            return p;
        }

        // --- helpers ---------------------------------------------------------------------------
        private IEnumerable<Window> SafeTopLevel()
        {
            try { return _app.GetAllTopLevelWindows(_automation) ?? Array.Empty<Window>(); }
            catch { return Array.Empty<Window>(); }
        }

        private static Rectangle ToRect(System.Drawing.Rectangle r) => r;  // FlaUI exposes System.Drawing.Rectangle
    }
}
