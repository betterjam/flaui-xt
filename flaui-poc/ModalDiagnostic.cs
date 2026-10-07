using System;
using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>
    /// Diagnostic: trigger a validation modal (blank Cost on an item) and dump EVERYTHING -
    /// top-level windows AND all descendants - so we can see exactly how the modal appears to
    /// UI Automation. The earlier detector missed it; this tells us why (it's likely a child
    /// window of frmMain, not a separate top-level window).
    /// </summary>
    [TestFixture]
    [Explicit("Diagnostic - dumps the modal's UIA representation.")]
    public class ModalDiagnostic
    {
        private static readonly string ExePath = Paths.ExePath;
        private FlaUI.Core.Application _app;
        private FlaUI.UIA3.UIA3Automation _automation;
        private Window Win => _app.GetMainWindow(_automation);

        [OneTimeSetUp]
        public void Setup()
        {
            _automation = new FlaUI.UIA3.UIA3Automation();
            _app = FlaUI.Core.Application.Launch(ExePath);
            Thread.Sleep(8000);
            try { Win?.SetForeground(); } catch { }
        }
        [OneTimeTearDown]
        public void Teardown() { try { _app?.Kill(); } catch { } _automation?.Dispose(); }

        [Test]
        public void Dump_modal_representation()
        {
            // navigate to an item and open it
            Win.FindFirstDescendant(cf => cf.ByName("Items"))?.AsButton()?.Click(); Thread.Sleep(2000);
            Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList"))?.AsButton()?.Click(); Thread.Sleep(2500);
            var grid = Win.FindFirstDescendant(cf => cf.ByAutomationId("dgvList"));
            grid?.FindAllChildren().FirstOrDefault(r => (r.Properties.Name.ValueOrDefault ?? "").StartsWith("Row "))?.AsGridRow()?.DoubleClick();
            Thread.Sleep(2500);

            // FIRST: what fields/tabs are actually visible right after opening the item?
            TestContext.WriteLine("===== fields visible right after opening item =====");
            var edits = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
            TestContext.WriteLine($"Edit fields visible: {edits.Length}");
            foreach (var e in edits.Take(25))
                TestContext.WriteLine($"  EDIT autoId='{e.Properties.AutomationId.ValueOrDefault}' text='{e.AsTextBox()?.Text}'");
            var tabs = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)
                                                    .Or(cf.ByControlType(ControlType.Tab)));
            TestContext.WriteLine($"\nTabs found: {tabs.Length}");
            foreach (var t in tabs.Take(15)) TestContext.WriteLine($"  TAB '{t.Properties.Name.ValueOrDefault}'");

            // clear Cost and save -> should raise "Cost is required"
            var cost = Win.FindFirstDescendant(cf => cf.ByAutomationId("txtCost"))?.AsTextBox();
            TestContext.WriteLine($"\nCost field found: {cost != null}, value BEFORE clear: '{cost?.Text}'");
            if (cost != null) { cost.Focus(); cost.Text = ""; Thread.Sleep(300); TestContext.WriteLine($"Cost value AFTER clear: '{cost.Text}'"); }
            var save = Win.FindFirstDescendant(cf => cf.ByName("Save"))?.AsButton()
                       ?? Win.FindFirstDescendant(cf => cf.ByAutomationId("btnOK"))?.AsButton();
            save?.Click();
            Thread.Sleep(800);   // shorter delay - catch the modal before it could close

            TestContext.WriteLine("===== AFTER SAVE - looking for the modal (0.8s) =====");

            // 1) top-level windows
            var tops = _app.GetAllTopLevelWindows(_automation);
            TestContext.WriteLine($"Top-level windows: {tops.Length}");
            foreach (var w in tops)
                TestContext.WriteLine($"  TOP: title='{w.Title}' autoId='{w.Properties.AutomationId.ValueOrDefault}' type={w.Properties.ControlType.ValueOrDefault}");

            // 2) any Window-type descendants of the main window (modal as child)
            var main = _app.GetMainWindow(_automation);
            var childWindows = main.FindAllDescendants(cf => cf.ByControlType(ControlType.Window));
            TestContext.WriteLine($"\nChild Window elements under main: {childWindows.Length}");
            foreach (var w in childWindows)
                TestContext.WriteLine($"  CHILD-WIN: name='{w.Properties.Name.ValueOrDefault}' autoId='{w.Properties.AutomationId.ValueOrDefault}'");

            // 3) anything containing the message text 'Cost is required' or with btnOK
            TestContext.WriteLine("\nElements with btnOK / Close / message text:");
            foreach (var el in main.FindAllDescendants())
            {
                var id = el.Properties.AutomationId.ValueOrDefault ?? "";
                var nm = el.Properties.Name.ValueOrDefault ?? "";
                if (id == "btnOK" || nm == "Close" || nm.Contains("required") || nm.Contains("Cost") || id.Contains("Popup") || nm.Contains("Message"))
                    TestContext.WriteLine($"  MATCH [{el.Properties.ControlType.ValueOrDefault}] autoId='{id}' name='{nm}'");
            }

            // 4) also dump the whole desktop's windows for this process
            TestContext.WriteLine("\n(If the modal isn't above, it may be a separate desktop window.)");
            Assert.Pass("Diagnostic complete - see log.");
        }
    }
}
