using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace FlaUiPoc
{
    /// <summary>
    /// Reusable base for FlaUI tests with CAPTURE-ON-FAILURE: run normally, and only when a
    /// test fails, capture evidence to diagnose:
    ///   - screenshot at the moment of failure
    ///   - UIA tree dump (which controls were present -> why X wasn't found)
    /// Artifacts are attached to the NUnit report. Passing tests leave nothing heavy behind.
    ///
    /// Headless: set FLAUI_HEADLESS=offscreen to move the window out of view (it keeps
    /// rendering, so the tests still work — but video can't be recorded off-screen).
    /// </summary>
    public abstract class FlaUiTestBase
    {
        protected static readonly string ExePath = Paths.ExePath;
        protected Application App;
        protected UIA3Automation Automation;

        private string _artifactDir;
        protected virtual int StartupWaitMs => 8000;

        [SetUp]
        public void BaseSetUp()
        {
            _artifactDir = Path.Combine(
                TestContext.CurrentContext.WorkDirectory, "artifacts",
                TestContext.CurrentContext.Test.Name.Replace(" ", "_"));
            Directory.CreateDirectory(_artifactDir);

            Automation = new UIA3Automation();
            App = Application.Launch(ExePath);
            Thread.Sleep(StartupWaitMs);
            MoveOffscreenIfRequested();
        }

        [TearDown]
        public void BaseTearDown()
        {
            var failed = TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Failed;
            if (failed)
            {
                try { CaptureScreenshot(); } catch (Exception e) { TestContext.WriteLine("screenshot failed: " + e.Message); }
                try { DumpUiaTree();      } catch (Exception e) { TestContext.WriteLine("uia dump failed: " + e.Message); }
            }
            try { App?.Close(); } catch { }
            try { App?.Kill();  } catch { }
            Automation?.Dispose();
        }

        // Move the app out of view for "headless" runs (real mouse clicks need it visible,
        // so only use this for inspection/assertion-style tests, not click-driven ones).
        private void MoveOffscreenIfRequested()
        {
            if (Environment.GetEnvironmentVariable("FLAUI_HEADLESS") != "offscreen") return;
            try
            {
                var win = App.GetMainWindow(Automation);
                var vs = System.Windows.Forms.SystemInformation.VirtualScreen;
                win?.Move(vs.Right + 100, vs.Top);
                TestContext.WriteLine("headless(offscreen): window moved out of view.");
            }
            catch (Exception e) { TestContext.WriteLine("headless move failed: " + e.Message); }
        }

        private void CaptureScreenshot()
        {
            var png = Path.Combine(_artifactDir, "failure-screenshot.png");
            AppCapture.Save(App, Automation, png);   // capture the popup/error wherever it landed, across all monitors
            SafeAttach(png, "Screenshot at failure");
            TestContext.WriteLine("Screenshot: " + png);
        }

        private void DumpUiaTree()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# UIA tree at failure - {DateTime.Now:u}");
            Window win = null;
            try { win = App.GetMainWindow(Automation); } catch { }
            if (win == null) sb.AppendLine("(could not get main window)");
            else
            {
                sb.AppendLine($"Window: '{win.Title}'");
                foreach (var el in win.FindAllDescendants())
                    sb.AppendLine($"  [{el.Properties.ControlType.ValueOrDefault}] " +
                                  $"AutomationId='{el.Properties.AutomationId.ValueOrDefault}' " +
                                  $"Name='{el.Properties.Name.ValueOrDefault}'");
            }
            var txt = Path.Combine(_artifactDir, "failure-uia-tree.txt");
            File.WriteAllText(txt, sb.ToString());
            SafeAttach(txt, "UIA tree at failure");
            TestContext.WriteLine("UIA dump: " + txt);
        }

        private static void SafeAttach(string path, string desc)
        {
            try { TestContext.AddTestAttachment(path, desc); } catch { }
        }
    }
}
