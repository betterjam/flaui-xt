using System;
using System.Linq;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace FlaUiPoc
{
    /// <summary>
    /// Page Object for the Exatouch Vendors screen (frmVendors).
    /// Encapsulates the REAL locators read from the WinForms code (Designer.cs):
    /// txtName, btnOK/btnDelete, etc. — the WinForms control "Name" is exposed as the
    /// AutomationId via UI Automation, which is how FlaUI finds it (no coordinates).
    ///
    /// In Ranorex this would be a recording; here it is code the AGENT can generate by
    /// reading the Designer.cs. There is no recorder: the AI writes the locator.
    /// </summary>
    public class VendorsPage
    {
        private readonly Window _main;

        public VendorsPage(Window main) => _main = main;

        // --- locators (por AutomationId == Name del control WinForms) ---
        public TextBox VendorName =>
            _main.FindFirstDescendant(cf => cf.ByAutomationId("txtName"))?.AsTextBox();

        public Button SaveButton =>
            // ucNav exposes the save action; on many forms it is btnOK with text "Save".
            _main.FindFirstDescendant(cf => cf.ByName("Save"))?.AsButton()
            ?? _main.FindFirstDescendant(cf => cf.ByAutomationId("btnOK"))?.AsButton();

        // --- actions ---
        public void SetName(string value)
        {
            var box = VendorName ?? throw new Exception("txtName not found in frmVendors (not exposed by UIA?).");
            box.Focus();
            box.Text = value;
        }

        public void Save() =>
            (SaveButton ?? throw new Exception("Save/btnOK button not found.")).Invoke();
    }
}
