using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace FlaUiPoc
{
    /// <summary>
    /// The Exatouch on-screen keyboard is NOT a docked control - it is its own top-level window
    /// (ucKeyboard : csNativeForm, see Controls\ucKeyboard.cs). It floats over the bottom of the item
    /// form, which is exactly why it shows up in our screenshots even when we capture "only the main
    /// window": we capture a SCREEN region, and the keyboard window is painted on top of that region.
    ///
    /// Because it is a real window, we can find it the reliable way - by its window handle and bounds -
    /// instead of guessing from pixels (the old perceptual-hash detector missed most frames because the
    /// keyboard's preview row and key highlights kept shifting the band hash past the threshold).
    ///
    /// From the source the geometry is fixed: Width = 1024 (same as the form), Height = 360 normally or
    /// 410 in search mode, bottom-docked (Top = formBottom - Height). So the keyboard is the visible
    /// app-process window that is roughly form-width, 340-430 tall, and bottom-aligned with the form.
    /// We never touch the app - we only learn WHERE the keyboard is so the picture can be cropped to the
    /// form above it (the clean view), deterministically, on every frame.
    /// </summary>
    public static class KeyboardWindow
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        /// <summary>Screen-Y of the top edge of the visible keyboard window, or -1 if no keyboard is up.
        /// <paramref name="mainRect"/> is the form's screen rectangle (used to confirm width / bottom
        /// alignment so we never confuse it with another window).</summary>
        public static int VisibleTop(int processId, Rectangle mainRect)
        {
            int found = -1;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != (uint)processId) return true;
                if (!GetWindowRect(h, out var r)) return true;

                int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                if (ht < 340 || ht > 430) return true;                       // keyboard is 360 or 410 tall
                if (mainRect.Width > 0)
                {
                    if (Math.Abs(w - mainRect.Width) > 80) return true;       // ~ form width (1024)
                    if (Math.Abs(r.Bottom - mainRect.Bottom) > 90) return true; // bottom-docked on the form
                }
                else if (Math.Abs(w - 1024) > 120) return true;              // fallback: absolute width
                if (found < 0 || r.Top < found) found = r.Top;               // top-most candidate edge
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// <summary>Where to cut the capture (bitmap-Y) to drop the keyboard, or -1 to keep the whole frame.
        /// <paramref name="origin"/> is the capture's screen top-left (so screen-Y maps to bitmap-Y).</summary>
        public static int CropY(int processId, Rectangle mainRect, Rectangle origin, int bmpHeight)
        {
            int top = VisibleTop(processId, mainRect);
            if (top < 0) return -1;
            int cut = top - origin.Y;
            return (cut > 40 && cut < bmpHeight) ? cut : -1;
        }
    }
}
