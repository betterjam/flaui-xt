using System;
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
    /// Diagnostic: open an item, focus a text field so Exatouch's on-screen keyboard appears, then dump
    /// every top-level window and every button (id/name/class/rect) so we can identify the keyboard's
    /// collapse control and dismiss it reliably. Writes keyboard-probe.txt. Explicit so it only runs
    /// when asked.
    /// </summary>
    [TestFixture]
    [Explicit("Diagnostic - dumps the on-screen keyboard's UIA controls.")]
    public class KeyboardProbe
    {
        private static readonly string OutDir = Paths.OutDir;
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
        public void Dump_keyboard_controls()
        {
            var sb = new StringBuilder();
            // open first item
            Win.FindFirstDescendant(cf => cf.ByName("Items"))?.AsButton()?.Click(); Thread.Sleep(2000);
            Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList"))?.AsButton()?.Click(); Thread.Sleep(2500);
            var grid = Win.FindFirstDescendant(cf => cf.ByAutomationId("dgvList"));
            grid?.FindAllChildren().FirstOrDefault(r => (r.Properties.Name.ValueOrDefault ?? "").StartsWith("Row "))?.AsGridRow()?.DoubleClick();
            Thread.Sleep(2500);

            // focus a text field to summon the keyboard
            var box = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                         .Select(e => e.AsTextBox())
                         .FirstOrDefault(t => { try { return t.IsEnabled && !t.IsReadOnly; } catch { return false; } });
            sb.AppendLine($"focused a textbox: id='{box?.Properties.AutomationId.ValueOrDefault}'");
            try { box?.Focus(); } catch { }
            Thread.Sleep(1500);

            // 1) all top-level windows of the process
            sb.AppendLine("\n===== TOP-LEVEL WINDOWS =====");
            foreach (var w in _app.GetAllTopLevelWindows(_automation))
            {
                var r = w.BoundingRectangle;
                sb.AppendLine($"WIN title='{w.Title}' autoId='{w.Properties.AutomationId.ValueOrDefault}' " +
                              $"class='{w.ClassName}' rect=({r.X},{r.Y},{r.Width}x{r.Height})");
            }

            // 2) every Button anywhere under the desktop for this process - to find the keyboard keys
            int pid = _app.ProcessId;
            var desktop = _automation.GetDesktop();
            var buttons = desktop.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                                 .Where(b => { try { return b.Properties.ProcessId.ValueOrDefault == pid; } catch { return false; } })
                                 .ToList();
            sb.AppendLine($"\n===== BUTTONS (process {pid}): {buttons.Count} =====");
            foreach (var b in buttons)
            {
                try
                {
                    var r = b.BoundingRectangle;
                    sb.AppendLine($"BTN id='{b.Properties.AutomationId.ValueOrDefault}' name='{b.Properties.Name.ValueOrDefault}' " +
                                  $"class='{b.ClassName}' rect=({r.X},{r.Y},{r.Width}x{r.Height})");
                }
                catch { }
            }

            // 3) anything whose name looks like a keyboard key, plus its parent chain
            sb.AppendLine("\n===== KEYBOARD-KEY MATCHES (name in {CAPS, Clear, Next/Continue, @gmail.com, Backspace}) =====");
            foreach (var el in desktop.FindAllDescendants())
            {
                try
                {
                    if (el.Properties.ProcessId.ValueOrDefault != pid) continue;
                    var nm = el.Properties.Name.ValueOrDefault ?? "";
                    if (nm == "CAPS" || nm == "Clear" || nm == "Next/Continue" || nm == "@gmail.com" || nm == "Backspace")
                    {
                        var p = el.Parent;
                        sb.AppendLine($"KEY '{nm}' [{el.Properties.ControlType.ValueOrDefault}] parent: " +
                                      $"name='{p?.Properties.Name.ValueOrDefault}' id='{p?.Properties.AutomationId.ValueOrDefault}' class='{p?.ClassName}'");
                    }
                }
                catch { }
            }

            File.WriteAllText(Path.Combine(OutDir, "keyboard-probe.txt"), sb.ToString());
            TestContext.WriteLine(sb.ToString());
            Assert.Pass("probe complete");
        }

        [Test]
        public void Find_keyboard_hide_button()
        {
            // open the first item
            Win.FindFirstDescendant(cf => cf.ByName("Items"))?.AsButton()?.Click(); Thread.Sleep(2000);
            Win.FindFirstDescendant(cf => cf.ByAutomationId("btnItemList"))?.AsButton()?.Click(); Thread.Sleep(2500);
            var grid = Win.FindFirstDescendant(cf => cf.ByAutomationId("dgvList"));
            grid?.FindAllChildren().FirstOrDefault(r => (r.Properties.Name.ValueOrDefault ?? "").StartsWith("Row "))?.AsGridRow()?.DoubleClick();
            Thread.Sleep(2500);

            var first = Win.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit))
                           .Select(e => e.AsTextBox())
                           .FirstOrDefault(t => { try { return t.IsEnabled && !t.IsReadOnly; } catch { return false; } });

            void ShowKeyboard() { try { first?.Focus(); } catch { } Thread.Sleep(900); }
            void Shot(string name) { try { using (var c = FlaUI.Core.Capturing.Capture.Element(Win)) c.ToFile(Path.Combine(OutDir, name)); } catch { } }

            var r = Win.BoundingRectangle;
            // baseline: keyboard up
            ShowKeyboard();
            Shot("kbh_00_up.png");

            // candidate Hide-glyph points (window-relative). After each click we screenshot, then
            // re-show the keyboard for the next candidate. Whichever shot has NO keyboard = the button.
            var cands = new (int x, int y, string tag)[]
            {
                ( 65, 720, "a_65_720"),
                ( 40, 720, "b_40_720"),
                ( 90, 720, "c_90_720"),
                ( 65, 700, "d_65_700"),
                ( 40, 740, "e_40_740"),
                ( 30, 745, "f_30_745"),
            };
            int i = 1;
            foreach (var c in cands)
            {
                ShowKeyboard();
                try { FlaUI.Core.Input.Mouse.Click(new System.Drawing.Point((int)r.Left + c.x, (int)r.Top + c.y)); } catch { }
                Thread.Sleep(700);
                Shot($"kbh_{i:00}_{c.tag}.png");
                i++;
            }
            Assert.Pass("hide-button probe complete - inspect kbh_*.png");
        }
    }
}
