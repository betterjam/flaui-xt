using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>Auto-generated from a RECORDED user session. A THIN test: it just declares the
    /// recorded steps; all resolution + self-healing retry lives in the UiSession engine.</summary>
    [TestFixture]
    public class Simple_item_open_testTest
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;

        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, "simple_item_open_test", "Recorded flow: simple_item_open_test", autoDismiss: false);
        [OneTimeTearDown] public void Teardown() { _s?.WriteReport("Recorded - simple_item_open_test", "simple_item_open_test-report.html"); _s?.Dispose(); }

        [Test]
        public void simple_item_open_test()
        {
            _s.Report.NewCase("Replay of recorded session (8 steps)");
            var flow = new System.Collections.Generic.List<UiSession.FlowStep>();
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Items\"", Act = () => _s.ClickEl("id", "btnItems", "Items", -1, "frmMenu", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Item List\"", Act = () => _s.ClickEl("id", "btnItemList", "Item List", -1, "frmMenu", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked \"btnQuickNav\"", Act = () => _s.ClickEl("id", "btnQuickNav", "", -1, "pnlLeft", "Pane") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked \"Item Row 0\"", Act = () => _s.ClickEl("name", "Item Row 0", "Item Row 0", -1, "dgvList", "Edit") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked \"frmItems\"", Act = () => _s.ClickEl("id", "frmItems", "", -1, "frmMain", "Window") });
            int failed = _s.RunFlow(flow);   // self-healing: a failed step steps BACK and retries
            _s.Step(failed == 0 ? "Recorded flow replayed faithfully - every step matched." : failed + " step(s) could not be replayed - the flow diverged.");
            _s.Report.SetStatus(failed == 0 ? "PASS" : "FAIL"); _s.Finish(failed == 0 ? "PASS" : "FAIL");
            Assert.That(failed, Is.EqualTo(0), failed + " recorded step(s) could not be replayed - the app flow diverged from the recording.");
        }
    }
}
