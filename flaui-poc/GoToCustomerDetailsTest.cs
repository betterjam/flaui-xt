using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>Auto-generated from a RECORDED user session (regenerated from its saved map). A THIN test:
    /// it just declares the steps; resolution + self-healing retry live in the UiSession engine.</summary>
    [TestFixture]
    public class GoToCustomerDetailsTest
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;

        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, "GoToCustomerDetails", "Recorded flow: GoToCustomerDetails", autoDismiss: false);
        [OneTimeTearDown] public void Teardown() { _s?.WriteReport("Recorded - GoToCustomerDetails", "GoToCustomerDetails-report.html"); _s?.Dispose(); }

        [Test]
        public void GoToCustomerDetails()
        {
            _s.Report.NewCase("Replay of recorded session (1 steps)");
            var flow = new System.Collections.Generic.List<UiSession.FlowStep>();
            flow.Add(new UiSession.FlowStep { Label = "Clicked text \"Customer Details\"", Act = () => _s.ClickEl("id", "lblSection", "Customer Details", -1, "pnlSession", "Edit") });
            int failed = _s.RunFlow(flow);
            _s.Step(failed == 0 ? "Recorded flow replayed faithfully - every step matched." : failed + " step(s) could not be replayed - the flow diverged.");
            _s.Report.SetStatus(failed == 0 ? "PASS" : "FAIL"); _s.Finish(failed == 0 ? "PASS" : "FAIL");
            Assert.That(failed, Is.EqualTo(0), failed + " recorded step(s) could not be replayed - the app flow diverged from the recording.");
        }
    }
}
