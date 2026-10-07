using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using NUnit.Framework;

namespace FlaUiPoc
{
    /// <summary>
    /// RUNTIME DISCOVERY: instead of guessing the testable surface from source code, we DRIVE
    /// the running app and record what is ACTUALLY there - every tab, the real fields on each
    /// tab, their current values, checkboxes, combos. This is ground truth (static analysis
    /// can't know e.g. that txtCost lives on the Pricing tab and is hidden until you click it).
    ///
    /// Output: runtime-map.json describing exactly what we found, per screen/tab. This both
    /// drives complete tests AND can correct the static catalog.
    /// </summary>
    [TestFixture]
    [Explicit("Diagnostic explorer - run manually; hidden from the suite.")]
    public class RuntimeExplorer
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;

        private FlaUI.Core.Application _app;
        private FlaUI.UIA3.UIA3Automation _automation;
        private readonly ReportBuilder _report = new ReportBuilder();
        private LiveLog _live;
        private Window Win => _app.GetMainWindow(_automation);

        [OneTimeSetUp]
        public void Setup()
        {
            Directory.CreateDirectory(OutDir);
            _live = new LiveLog(OutDir, "Runtime discovery: explore the Item Detail screen, tab by tab");
            _automation = new FlaUI.UIA3.UIA3Automation();
            _app = FlaUI.Core.Application.Launch(ExePath);
            Thread.Sleep(8000);
            try { Win?.SetForeground(); } catch { }
            Thread.Sleep(500);
        }

        [OneTimeTearDown]
        public void Teardown()
        {
            _report.Write(OutDir, "Exatouch - Runtime UI Discovery", "runtime-explorer-report.html");
            try { _app?.Kill(); } catch { }
            _automation?.Dispose();
        }

        private void Step(string msg)
        {
            string png = null, file = null;
            try { file = $"shot_{Guid.NewGuid():N}.png"; png = Path.Combine(OutDir, file);
                  AppCapture.Save(_app, _automation, png); } catch { png = null; }
            _report.AddStep(msg, png); _live?.AddStep(msg, file); TestContext.WriteLine(msg);
        }

        // fields that look like real detail fields (have an AutomationId on the open form)
        private IEnumerable<(string id, string val, string type)> VisibleFields()
        {
            foreach (var e in Win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)))
            {
                var id = e.Properties.AutomationId.ValueOrDefault ?? "";
                if (id.StartsWith("txt")) yield return (id, Safe(() => e.AsTextBox().Text), "text");
            }
            foreach (var c in Win.FindAllDescendants(cf => cf.ByControlType(ControlType.CheckBox)))
            {
                var id = c.Properties.AutomationId.ValueOrDefault ?? "";
                if (id.StartsWith("chk")) yield return (id, Safe(() => (c.AsCheckBox().IsChecked ?? false).ToString()), "check");
            }
            foreach (var b in Win.FindAllDescendants(cf => cf.ByControlType(ControlType.ComboBox)))
            {
                var id = b.Properties.AutomationId.ValueOrDefault ?? "";
                if (id.StartsWith("cbo")) yield return (id, Safe(() => b.AsComboBox().Value ?? ""), "combo");
            }
        }
        private static string Safe(Func<string> f) { try { return f() ?? ""; } catch { return ""; } }

        [Test]
        public void Explore_item_detail_tabs_and_describe_what_we_find()
        {
            _report.NewCase("Runtime discovery of the Item Detail screen (every tab, real fields)");
            var map = new StringBuilder();
            map.AppendLine("{");
            map.AppendLine("  \"screen\": \"Item Detail (frmItems)\",");
            map.AppendLine("  \"discoveredAt\": \"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "\",");
            map.AppendLine("  \"tabs\": [");

            // open the first item
            Win.FindFirstDescendant(cf => cf.ByName("Items"))?.AsButton()?.Click(); Thread.Sleep(2000);
            Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList"))?.AsButton()?.Click(); Thread.Sleep(2500);
            var grid = Win.FindFirstDescendant(cf => cf.ByAutomationId("dgvList"));
            grid?.FindAllChildren().FirstOrDefault(r => (r.Properties.Name.ValueOrDefault ?? "").StartsWith("Row "))?.AsGridRow()?.DoubleClick();
            Thread.Sleep(2500);
            Step("Opened an item. Now walking every tab to record the real fields on each.");

            // find the tabs (real tab names from the running app)
            var tabItems = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem)).ToList();
            var tabNames = tabItems.Select(t => t.Properties.Name.ValueOrDefault ?? "")
                                   .Where(n => !string.IsNullOrWhiteSpace(n) && n != "0").Distinct().ToList();
            Step($"Found {tabNames.Count} tabs on the Item Detail: {string.Join(", ", tabNames)}");

            int totalFields = 0; bool firstTab = true;
            foreach (var tabName in tabNames)
            {
                // click the tab
                var tab = Win.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(tabName)));
                try { tab?.Click(); } catch { try { tab?.AsTabItem()?.Select(); } catch { } }
                Thread.Sleep(900);

                // record fields now visible (those belonging to this tab)
                var fields = VisibleFields().Where(f => f.id.StartsWith("txt") || f.id.StartsWith("chk") || f.id.StartsWith("cbo"))
                                            .GroupBy(f => f.id).Select(g => g.First()).ToList();
                totalFields += fields.Count;

                if (!firstTab) map.AppendLine("    ,");
                firstTab = false;
                map.AppendLine("    {");
                map.AppendLine($"      \"tab\": \"{Esc(tabName)}\",");
                map.AppendLine($"      \"fieldCount\": {fields.Count},");
                map.AppendLine("      \"fields\": [");
                for (int i = 0; i < fields.Count; i++)
                {
                    var f = fields[i];
                    map.AppendLine($"        {{ \"id\": \"{Esc(f.id)}\", \"type\": \"{f.type}\", \"value\": \"{Esc(f.val)}\" }}{(i < fields.Count - 1 ? "," : "")}");
                }
                map.AppendLine("      ]");
                map.AppendLine("    }");

                Step($"Tab '{tabName}': found {fields.Count} editable fields. Examples: " +
                     string.Join(", ", fields.Take(6).Select(f => f.id)));
            }

            map.AppendLine("  ],");
            map.AppendLine($"  \"totalFieldsAcrossTabs\": {totalFields}");
            map.AppendLine("}");

            var mapPath = Path.Combine(OutDir, "runtime-map.json");
            File.WriteAllText(mapPath, map.ToString(), Encoding.UTF8);
            Step($"Runtime discovery complete: {tabNames.Count} tabs, {totalFields} fields recorded to runtime-map.json. " +
                 "This is what the app ACTUALLY has at runtime - far more than the default tab alone.");

            Assert.That(totalFields, Is.GreaterThan(10), "Expected to discover fields across multiple tabs.");
            _report.SetStatus("PASS");
            _live?.Finish("PASS");
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    }
}
