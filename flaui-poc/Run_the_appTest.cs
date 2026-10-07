using NUnit.Framework;
using System.Collections.Generic;

namespace FlaUiPoc
{
    /// <summary>Auto-generated from a RECORDED user session. A THIN test: it declares its OWN steps and, if it
    /// extends a parent, REFERENCES the parent's BuildFlow (recursively). Resolution + self-healing retry live
    /// in the UiSession engine.</summary>

    [TestFixture]
    public class Run_the_appTest
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;

        /// <summary>Append this test's flow to <paramref name="flow"/>: the parent's steps first (by reference,
        /// recursively), then this test's own recorded steps. Shared so children can compose this test.</summary>
        public static void BuildFlow(UiSession s, List<UiSession.FlowStep> flow)
        {

            flow.Add(new UiSession.FlowStep { Label = "Launch Exatouch and go to the Home menu", Act = () => s.ClickEl("id", "btnHome", "Home", -1, "", "Button") });
        }

        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, "Run_the_app", "Recorded flow: Run_the_app", autoDismiss: false);
        [OneTimeTearDown] public void Teardown() { _s?.WriteReport("Recorded - Run_the_app", "Run_the_app-report.html"); _s?.Dispose(); }

        [Test]
        public void Run_the_app()
        {
            var flow = new List<UiSession.FlowStep>();
            BuildFlow(_s, flow);   // parent (by reference) + this test's own steps
            _s.Report.NewCase("Replay of recorded session (" + flow.Count + " steps)");
            int failed = _s.RunFlow(flow);
            _s.Step(failed == 0 ? "Recorded flow replayed faithfully - every step matched." : failed + " step(s) could not be replayed - the flow diverged.");
            _s.Report.SetStatus(failed == 0 ? "PASS" : "FAIL"); _s.Finish(failed == 0 ? "PASS" : "FAIL");
            Assert.That(failed, Is.EqualTo(0), failed + " recorded step(s) could not be replayed - the app flow diverged from the recording.");
        }
    }
}
