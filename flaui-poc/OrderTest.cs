using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>Auto-generated from a RECORDED user session. A THIN test: it just declares the
    /// recorded steps; all resolution + self-healing retry lives in the UiSession engine.</summary>
    [TestFixture]
    public class OrderTest
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;

        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, "order", "Recorded flow: order", autoDismiss: false);
        [OneTimeTearDown] public void Teardown() { _s?.WriteReport("Recorded - order", "order-report.html"); _s?.Dispose(); }

        [Test]
        public void order()
        {
            _s.Report.NewCase("Replay of recorded session (17 steps)");
            var flow = new System.Collections.Generic.List<UiSession.FlowStep>();
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Register\"", Act = () => _s.ClickEl("id", "btnRegister", "Register", -1, "frmMenu", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"CatCol Row 1\"", Act = () => _s.ClickEl("name", "CatCol Row 1", "CatCol Row 1", -1, "dgvCat", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol1 Row 1\"", Act = () => _s.ClickEl("name", "ITCol1 Row 1", "ITCol1 Row 1", -1, "dgvItems", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol1 Row 2\"", Act = () => _s.ClickEl("name", "ITCol1 Row 2", "ITCol1 Row 2", -1, "dgvItems", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol1 Row 3\"", Act = () => _s.ClickEl("name", "ITCol1 Row 3", "ITCol1 Row 3", -1, "dgvItems", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Cash\"", Act = () => _s.ClickEl("name", "Cash", "Cash", -1, "tpgRegister", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Pay\"", Act = () => _s.ClickEl("id", "btnOK", "Pay", -1, "frmCash", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Close\"", Act = () => _s.ClickEl("id", "btnCancel", "Close", -1, "frmPicture", "Button") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Close\"", Act = () => _s.ClickEl("id", "btnCancel", "Close", -1, "frmPicture", "Button") });
            int failed = _s.RunFlow(flow);   // self-healing: a failed step steps BACK and retries
            _s.Step(failed == 0 ? "Recorded flow replayed faithfully - every step matched." : failed + " step(s) could not be replayed - the flow diverged.");
            _s.Report.SetStatus(failed == 0 ? "PASS" : "FAIL"); _s.Finish(failed == 0 ? "PASS" : "FAIL");
            Assert.That(failed, Is.EqualTo(0), failed + " recorded step(s) could not be replayed - the app flow diverged from the recording.");
        }
    }
}
