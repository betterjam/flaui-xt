using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using WinForms = System.Windows.Forms;

namespace FlaUiPoc
{
    /// <summary>
    /// AppCapture takes the screenshot the USER would actually see: the main window PLUS any popup /
    /// secondary window, wherever Exatouch placed it - including a non-primary monitor on an ultrawide
    /// rig. The old <c>Capture.Element(mainWindow)</c> cropped to the main window's rectangle, so a
    /// <c>frmPopup</c> validation modal that opened offset / on another monitor was simply not in the
    /// bitmap (and the <c>Capture.Screen()</c> fallback only grabbed the PRIMARY monitor). This helper
    /// captures the bounding UNION of every visible app window, clamped to the virtual desktop, so the
    /// modal is always in frame.
    ///
    /// Each Grab returns the captured image AND the <paramref name="origin"/> rectangle - the top-left
    /// of what we shot, in screen coords. Highlight/marker drawing must offset element rectangles by
    /// this origin (NOT by the main window's position), or the boxes land in the wrong place once the
    /// capture spans more than the main window.
    /// </summary>
    public static class AppCapture
    {
        /// <summary>Capture across all visible top-level windows of <paramref name="app"/>.</summary>
        public static CaptureImage Grab(Application app, FlaUI.UIA3.UIA3Automation automation, out Rectangle origin)
        {
            IEnumerable<Window> wins = null;
            try { wins = app?.GetAllTopLevelWindows(automation); } catch { }
            return Grab(wins, out origin);
        }

        /// <summary>Capture across all visible windows belonging to <paramref name="win"/>'s process.
        /// For callers (e.g. FrameGuard) that hold a window but not the Application handle.</summary>
        public static CaptureImage GrabProcessOf(Window win, out Rectangle origin)
        {
            IEnumerable<Window> wins = null;
            try
            {
                int pid = win.Properties.ProcessId.ValueOrDefault;
                wins = win.Automation.GetDesktop().FindAllChildren()
                          .Where(e => { try { return e.Properties.ProcessId.ValueOrDefault == pid; } catch { return false; } })
                          .Select(e => e.AsWindow())
                          .ToList();
            }
            catch { }
            if ((wins == null || !wins.Any()) && win != null) wins = new[] { win };
            return Grab(wins, out origin);
        }

        /// <summary>Capture, draw nothing, write straight to <paramref name="path"/>.
        /// Convenience for steps that don't highlight a control.</summary>
        public static void Save(Application app, FlaUI.UIA3.UIA3Automation automation, string path)
        {
            using (var img = Grab(app, automation, out _))
                img.Bitmap.Save(path);
        }

        /// <summary>Core: union of the given windows' on-screen rectangles, clamped to the virtual
        /// desktop (all monitors). Falls back to the whole virtual desktop when nothing is enumerable -
        /// never to the primary monitor alone, so an off-monitor popup is still captured.</summary>
        public static CaptureImage Grab(IEnumerable<Window> windows, out Rectangle origin)
        {
            var rects = new List<Rectangle>();
            if (windows != null)
            {
                foreach (var w in windows)
                {
                    try { if (w != null && !w.IsOffscreen) rects.Add(w.BoundingRectangle); }
                    catch { /* a window can vanish mid-enumeration */ }
                }
            }

            var virt = WinForms.SystemInformation.VirtualScreen;
            if (rects.Count == 0)
            {
                origin = virt;
                return Capture.Rectangle(virt);
            }

            var union = Rectangle.FromLTRB(
                rects.Min(r => r.Left), rects.Min(r => r.Top),
                rects.Max(r => r.Right), rects.Max(r => r.Bottom));
            union.Intersect(virt);                 // never ask Capture for pixels that don't exist
            if (union.Width <= 0 || union.Height <= 0) union = virt;

            origin = union;
            return Capture.Rectangle(union);
        }
    }
}
