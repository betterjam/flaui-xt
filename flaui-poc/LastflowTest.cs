using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>Auto-generated from a RECORDED user session (regenerated from its saved map). A THIN test:
    /// it just declares the steps; resolution + self-healing retry live in the UiSession engine.</summary>
    [TestFixture]
    public class LastflowTest
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;

        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, "lastflow", "Recorded flow: lastflow", autoDismiss: false);
        [OneTimeTearDown] public void Teardown() { _s?.WriteReport("Recorded - lastflow", "lastflow-report.html"); _s?.Dispose(); }

        [Test]
        public void lastflow()
        {
            _s.Report.NewCase("Replay of recorded session (33 steps)");
            var flow = new System.Collections.Generic.List<UiSession.FlowStep>();
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Register\" (#1)", Act = () => _s.ClickEl("id", "btnRegister", "Register", 0, "frmMenu", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"CatCol Row 1\"", Act = () => _s.ClickEl("name", "CatCol Row 1", "CatCol Row 1", -1, "dgvCat", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol1 Row 1\"", Act = () => _s.ClickEl("name", "ITCol1 Row 1", "ITCol1 Row 1", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol2 Row 1\"", Act = () => _s.ClickEl("name", "ITCol2 Row 1", "ITCol2 Row 1", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol1 Row 2\"", Act = () => _s.ClickEl("name", "ITCol1 Row 2", "ITCol1 Row 2", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol2 Row 2\"", Act = () => _s.ClickEl("name", "ITCol2 Row 2", "ITCol2 Row 2", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol3 Row 2\"", Act = () => _s.ClickEl("name", "ITCol3 Row 2", "ITCol3 Row 2", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol4 Row 2\"", Act = () => _s.ClickEl("name", "ITCol4 Row 2", "ITCol4 Row 2", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol1 Row 3\"", Act = () => _s.ClickEl("name", "ITCol1 Row 3", "ITCol1 Row 3", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol2 Row 3\"", Act = () => _s.ClickEl("name", "ITCol2 Row 3", "ITCol2 Row 3", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol3 Row 3\"", Act = () => _s.ClickEl("name", "ITCol3 Row 3", "ITCol3 Row 3", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"ITCol4 Row 3\"", Act = () => _s.ClickEl("name", "ITCol4 Row 3", "ITCol4 Row 3", -1, "dgvItems", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Cash\"", Act = () => _s.ClickEl("name", "Cash", "Cash", -1, "tpgRegister", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Pay\"", Act = () => _s.ClickEl("id", "btnOK", "Pay", -1, "frmCash", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Close\"", Act = () => _s.ClickEl("id", "btnCancel", "Close", -1, "", "") });
            flow.Add(new UiSession.FlowStep { Label = "Clicked button \"Close\"", Act = () => _s.ClickEl("id", "btnCancel", "Close", -1, "frmPicture", "") });
            int failed = _s.RunFlow(flow);
            _s.Step(failed == 0 ? "Recorded flow replayed faithfully - every step matched." : failed + " step(s) could not be replayed - the flow diverged.");
            _s.Report.SetStatus(failed == 0 ? "PASS" : "FAIL"); _s.Finish(failed == 0 ? "PASS" : "FAIL");
            Assert.That(failed, Is.EqualTo(0), failed + " recorded step(s) could not be replayed - the app flow diverged from the recording.");
        }
    }
}
