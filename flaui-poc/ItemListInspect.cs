using System.Linq;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>
    /// Inspection: navigate Home -> Items -> Item List, then dump everything on the item list
    /// screen (grid, rows, cells, text) so we can see HOW individual items are exposed to UI
    /// Automation -> which tells us how to click one open. Pure diagnostic, no real asserts.
    /// </summary>
    [TestFixture]
    [Explicit("Diagnostic only - dumps the Item List UIA tree to help author tests. Not a real test, no report.")]
    [Category("Diagnostic")]
    public class ItemListInspect
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
            Thread.Sleep(500);
        }

        [OneTimeTearDown]
        public void Teardown() { try { _app?.Kill(); } catch { } _automation?.Dispose(); }

        [Test]
        public void Inspect_item_list_screen()
        {
            // navigate Home -> Items -> Item List
            Win.FindFirstDescendant(cf => cf.ByName("Items"))?.AsButton()?.Click();
            Thread.Sleep(2000);
            Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList"))?.AsButton()?.Click();
            Thread.Sleep(2500);

            TestContext.WriteLine("===== ITEM LIST SCREEN - control inventory =====");

            // data grids
            var grids = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.DataGrid)
                                                     .Or(cf.ByControlType(ControlType.Table)));
            TestContext.WriteLine($"DataGrids/Tables: {grids.Length}");
            foreach (var g in grids)
                TestContext.WriteLine($"  GRID autoId='{g.Properties.AutomationId.ValueOrDefault}' name='{g.Properties.Name.ValueOrDefault}'");

            // rows
            var rows = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.DataItem)
                                                    .Or(cf.ByControlType(ControlType.ListItem))
                                                    .Or(cf.ByControlType(ControlType.Custom)));
            TestContext.WriteLine($"Rows/DataItems/Custom: {rows.Length}");
            foreach (var r in rows.Take(15))
                TestContext.WriteLine($"  ROW [{r.Properties.ControlType.ValueOrDefault}] name='{r.Properties.Name.ValueOrDefault}'");

            // text elements (item names usually show as Text)
            var texts = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
                           .Select(t => t.Properties.Name.ValueOrDefault)
                           .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
            TestContext.WriteLine($"Text values: {texts.Count}");
            foreach (var t in texts.Take(40)) TestContext.WriteLine($"  TEXT '{t}'");

            Assert.Pass("Inspection complete - see log.");
        }
    }
}
