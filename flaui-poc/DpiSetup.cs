using System;
using System.Runtime.InteropServices;
using NUnit.Framework;

// NOTE: intentionally in the GLOBAL namespace. An NUnit [SetUpFixture] with no namespace applies to
// the WHOLE test assembly, and its [OneTimeSetUp] runs ONCE before any test fixture - the earliest
// hook we get inside the test host.
//
// We use it to make the test-host process per-monitor DPI aware. This is the OTHER half of the
// ultrawide screenshot fix: on a multi-monitor rig with mixed scale factors, a DPI-unaware/system-
// aware process gets VIRTUALIZED coordinates - FlaUI's BoundingRectangle then reports lying
// coordinates and Capture.Rectangle grabs the wrong physical pixels on the secondary monitor
// (blank or shifted captures). Declaring per-monitor-v2 awareness makes coordinates true pixels.
//
// Why not an app.manifest? Because these tests are a CLASS LIBRARY - the running process is
// testhost.exe / the NUnit runner, not our assembly, so a manifest on this project is never applied.
// DPI awareness must therefore be set programmatically, as early as possible, before any window or
// screen is touched.
[SetUpFixture]
public sealed class DpiSetup
{
    // DPI_AWARENESS_CONTEXT handles (user32, Win10 1703+).
    private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
    private static readonly IntPtr PerMonitorAware   = new IntPtr(-3);

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("shcore.dll")] private static extern int  SetProcessDpiAwareness(int value); // 2 = Per-Monitor (Win8.1+)
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();               // system-DPI (Vista+)

    [OneTimeSetUp]
    public void MakeProcessDpiAware()
    {
        // Best -> worst, stop at the first that succeeds. Each may throw on older OSes (missing
        // export) or return false if awareness was already locked by the host; both are fine to ignore.
        try { if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return; } catch { }
        try { if (SetProcessDpiAwarenessContext(PerMonitorAware))   return; } catch { }
        try { if (SetProcessDpiAwareness(2) == 0)                   return; } catch { } // 0 == S_OK
        try { SetProcessDPIAware(); } catch { }
    }
}
