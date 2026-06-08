# Exatouch Test Catalog - Coverage Manual

_Auto-generated index of every area we cover with tests. Do not edit by hand; run scripts/index.ps1._

## Overall

**4.8% complete** - 126 / 2600 testable code anchors covered. Complete: **NO**.

| Area | Layer | Covered | Total | % | Catalog |
|------|-------|--------:|------:|--:|:-------:|
| Calendar | e2e | 51 | 51 | 100% | yes |
| Purchasing | e2e | 44 | 111 | 39.6% | yes |
| Transactions | e2e | 31 | 738 | 4.2% | yes |
| Staff | e2e | 0 | 88 | 0% | - |
| SysForms | e2e | 0 | 91 | 0% | - |
| Items | e2e | 0 | 346 | 0% | - |
| Customers | e2e | 0 | 95 | 0% | - |
| Utility | e2e | 0 | 56 | 0% | - |
| Integrations | e2e | 0 | 235 | 0% | - |
| Reports | e2e | 0 | 249 | 0% | - |
| Main | e2e | 0 | 104 | 0% | - |
| Managers | unit | 0 | 93 | 0% | - |
| Settings | e2e | 0 | 343 | 0% | - |

## Tests by area

### Calendar  (51 tests: 48 atomic, 3 composite)

**(uncategorized)** (51)

- `CAL-035` Day view right scroll button shifts the staff columns right  _( P3 / frmDay / Appointment / needs-recording )_
- `CAL-034` Day view Previous button steps the day back by one  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-033` Day view page-right button pages the staff columns right  _( P3 / frmDay / Appointment / needs-recording )_
- `CAL-038` Moving an appointment past the end of the day warns "Check start and end times.  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-037` Clicking an off-hours slot in the day view is blocked  _( P1 / frmDay / Appointment / needs-recording )_
- `CAL-036` Clicking an empty in-hours slot in the day view opens the create-appointment panel  _( P1 / frmDay / Appointment / needs-recording )_
- `CAL-029` Day view left scroll button shifts the staff columns left  _( P3 / frmDay / Appointment / needs-recording )_
- `CAL-028` Filter button opens the day-view filter dialog and applies the selection  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-027` Cancel Move on the day view aborts a pending appointment move  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-032` Day view Next button advances the day by one  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-031` Legend button opens the day-view colour legend dialog  _( P3 / frmDay / Appointment / needs-recording )_
- `CAL-030` Day view left-page button pages the staff columns left  _( P3 / frmDay / Appointment / needs-recording )_
- `CAL-039` Moving an appointment onto off-hours slots is blocked (day view move)  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-048` Check Out with no appointment row selected warns "Selection error.  _( P2 / frmSelect / Appointment / needs-recording )_
- `CAL-047` Check-out select dialog Check Out confirms the highlighted appointments  _( P2 / frmSelect / Appointment / needs-recording )_
- `CAL-046` Check-out select dialog Cancel dismisses without selecting appointments  _( P3 / frmSelect / Appointment / needs-recording )_
- `CAL-051` Day-view move validation walk (end-of-day / off-hours / double-book guards)  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-050` Appointment-time validation walk (no service / start time / off-hours / double-book)  _( P2 / frmCreate / Appointment / needs-recording )_
- `CAL-049` End-to-end - book an appointment then check it out to the register  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-042` Filter dialog Clear Filter removes the active staff/service filter  _( P2 / frmFilter / Appointment / needs-recording )_
- `CAL-041` Filter dialog Cancel discards filter changes  _( P3 / frmFilter / Appointment / needs-recording )_
- `CAL-040` Moving an appointment onto a fully double-booked slot is blocked (day view move)  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-045` Check-out select dialog "Check Out All" selects every appointment  _( P2 / frmSelect / Appointment / needs-recording )_
- `CAL-044` Legend dialog Close button dismisses the legend  _( P3 / frmLegend / Appointment / needs-recording )_
- `CAL-043` Filter dialog Show applies the selected staff and services  _( P2 / frmFilter / Appointment / needs-recording )_
- `CAL-026` Close button on the day view returns to the month calendar  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-009` Adding a service that overruns the day's last slot warns "Time unavailable.  _( P2 / frmCreate / Appointment / needs-recording )_
- `CAL-008` Conflict dialog Print confirms and prints the conflict slip  _( P3 / frmConflict / Appointment / needs-recording )_
- `CAL-007` Conflict dialog Cancel dismisses without printing  _( P3 / frmConflict / Appointment / needs-recording )_
- `CAL-012` Month button on the create panel returns to the month calendar  _( P3 / frmCreate / Appointment / needs-recording )_
- `CAL-011` Adding a service onto an already double-booked slot is blocked (create panel)  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-010` Selecting a time outside the staff member's scheduled hours is blocked (create panel)  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-003` Month calendar next button advances one month  _( P3 / frmCalendar / Appointment / needs-recording )_
- `CAL-002` Month calendar back-year button steps the calendar back one year  _( P3 / frmCalendar / Appointment / needs-recording )_
- `CAL-001` Cancel a pending appointment move from the month calendar  _( P2 / frmCalendar / Appointment / needs-recording )_
- `CAL-006` Month calendar forward-year button advances the calendar one year  _( P3 / frmCalendar / Appointment / needs-recording )_
- `CAL-005` Month calendar Today button jumps to the current date  _( P2 / frmCalendar / Appointment / needs-recording )_
- `CAL-004` Month calendar previous button steps back one month  _( P3 / frmCalendar / Appointment / needs-recording )_
- `CAL-013` Cancel an existing appointment from the create panel reports "Cancelled.  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-022` Clear Services resets all selected services on the create panel  _( P3 / frmCreate / Appointment / needs-recording )_
- `CAL-021` Saving a valid appointment reports "Saved successfully.  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-020` Saving an appointment with no services warns "Service not added.  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-025` Day view warns when no staff/services match the current filter and reverts  _( P2 / frmDay / Appointment / needs-recording )_
- `CAL-024` Clicking a service before choosing a start time warns "Choose start time.  _( P2 / frmCreate / Appointment / needs-recording )_
- `CAL-023` Selecting an existing appointment row loads it into the create panel  _( P2 / frmCreate / Appointment / needs-recording )_
- `CAL-016` Customer History opens the customer's service-only info form from the create panel  _( P3 / frmCreate / Appointment / needs-recording )_
- `CAL-015` Check Out adds the appointment's services to an open order on the register  _( P1 / frmCreate / Appointment / needs-recording )_
- `CAL-014` Close button on the create panel dismisses when there are no unsaved changes  _( P2 / frmCreate / Appointment / needs-recording )_
- `CAL-019` Move Appt. saves and switches the panel into appointment-move mode  _( P2 / frmCreate / Appointment / needs-recording )_
- `CAL-018` Double-click look-up reassigns the appointment to a different customer  _( P3 / frmCreate / Appointment / needs-recording )_
- `CAL-017` Delete Line removes the selected service from the appointment  _( P2 / frmCreate / Appointment / needs-recording )_

### Purchasing  (47 tests: 44 atomic, 3 composite)

**(uncategorized)** (47)

- `PUR-032` Clearing the item-detail description on a PO line warns and undoes  _( P3 / frmPO / Item / needs-recording )_
- `PUR-031` Editing a PO line qty below order minimum warns and resets to minimum  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-033` Setting order-min above reorder qty on a PO line warns and undoes  _( P3 / frmPO / Item / needs-recording )_
- `PUR-035` Setting a PO-line retail price below the defined minimum/SRP warns  _( P2 / frmPO / Item / needs-recording )_
- `PUR-034` Typing a PO number that parses to zero warns "Invalid PO ID  _( P3 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-030` Voiding a PO reverses stock and reports "Voided successfully  _( P1 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-026` Sending PO via POSCloud blocked when no valid sender email is configured  _( P3 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-025` Clicking OK on a modified PO saves and reports "Updated successfully  _( P1 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-027` Emailing a PO via POSCloud reports "Emailed successfully  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-029` Editing item details from a PO line saves and reports "Updated Item successfully  _( P2 / frmPO / Item / needs-recording )_
- `PUR-028` Emailing a PO via SMTP (non-POSCloud) reports "Emailed successfully  _( P3 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-036` Setting reorder qty below order-min on a PO line warns and undoes  _( P3 / frmPO / Item / needs-recording )_
- `PUR-044` Receiving-proc dialog warns on invalid/future received date  _( P2 / frmReceivingProc / Receiving / needs-recording )_
- `PUR-043` Receiving grid warns when qty x cost exceeds the maximum allowed amount  _( P3 / frmReceiving / Receiving / needs-recording )_
- `PUR-045` End-to-end - create vendor, build PO, receive stock  _( P1 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-047` Vendor validation walk (name / contact / email / LCB guards)  _( P2 / frmVendors / Vendor / needs-recording )_
- `PUR-046` Purchase-order validation walk (empty / blank desc / received-date guards)  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-042` Saving a receiving session via OK persists session and reports "Saved successfully  _( P2 / frmReceiving / Receiving / needs-recording )_
- `PUR-038` Qty/Cost dialog warns when qty x cost exceeds the maximum allowed amount  _( P3 / frmQtyCost / Item / needs-recording )_
- `PUR-037` Qty/Cost dialog warns when entered qty is below order minimum  _( P2 / frmQtyCost / Item / needs-recording )_
- `PUR-039` Adding a discontinued item to a receiving session warns  _( P2 / frmReceiving / Receiving / needs-recording )_
- `PUR-041` Finishing a receiving session with invalid UPCs warns no labels printed  _( P3 / frmReceiving / Receiving / needs-recording )_
- `PUR-040` Finishing a receiving session posts stock and reports "Saved successfully  _( P1 / frmReceiving / Receiving / needs-recording )_
- `PUR-024` Saving received PO items with invalid UPCs warns no labels printed  _( P3 / frmPO / Receiving / needs-recording )_
- `PUR-008` Vendor history shows warning when vendor has no POs  _( P3 / frmVendors / Vendor / needs-recording )_
- `PUR-007` Delete vendor confirms and reports success  _( P2 / frmVendors / Vendor / needs-recording )_
- `PUR-009` Loading a non-existent PO id warns "Purchase order not found  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-011` Adding an item whose master record was removed warns (initial lookup)  _( P3 / frmPO / Item / needs-recording )_
- `PUR-010` Adding an item to a voided PO is blocked  _( P1 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-006` Vendor save blocked when FEIN missing for Non-PA manufacturer LCB type  _( P3 / frmVendors / Vendor / needs-recording )_
- `PUR-002` Vendor save blocked when name is blank  _( P2 / frmVendors / Vendor / needs-recording )_
- `PUR-001` Save a new vendor with required contact info succeeds  _( P1 / frmVendors / Vendor / needs-recording )_
- `PUR-003` Vendor save blocked when no phone, fax or email provided  _( P2 / frmVendors / Vendor / needs-recording )_
- `PUR-005` Vendor save blocked when no LCB vendor type selected (LCB enabled)  _( P3 / frmVendors / Vendor / needs-recording )_
- `PUR-004` Vendor save blocked on invalid email address  _( P2 / frmVendors / Vendor / needs-recording )_
- `PUR-012` Adding an item whose detail row resolves to nothing warns (detail lookup)  _( P3 / frmPO / Item / needs-recording )_
- `PUR-020` Saving a PO with a line missing description warns  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-019` Saving a PO with an empty detail grid warns "Nothing to save  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-021` Saving a PO with non-whole qty for a non-SBF item warns invalid number format  _( P2 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-023` Saving a PO with received qty but invalid/future received date warns  _( P2 / frmPO / Receiving / needs-recording )_
- `PUR-022` Saving a PO with received qty but blank reference warns  _( P2 / frmPO / Receiving / needs-recording )_
- `PUR-018` Clicking OK on a PO with no changes warns "Nothing changed  _( P3 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-014` Adding an item in an inactive subcategory is blocked  _( P3 / frmPO / Item / needs-recording )_
- `PUR-013` Adding an item in an inactive category is blocked  _( P3 / frmPO / Item / needs-recording )_
- `PUR-015` Adding a discontinued item to a PO warns but still allows add  _( P2 / frmPO / Item / needs-recording )_
- `PUR-017` Completing a PO marks it received successfully  _( P1 / frmPO / PurchaseOrder / needs-recording )_
- `PUR-016` Auto-generate PO warns when nothing is below reorder level  _( P2 / frmPO / PurchaseOrder / needs-recording )_

### Transactions  (57 tests: 55 atomic, 2 composite)

**(uncategorized)** (57)

- `TXN-0039` frmBank: Warning - Nothing changed. (224)  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0038` frmBank: Save action (btnOKClick)  _( P1 / frmBank / Order / needs-recording )_
- `TXN-0037` frmBank: Reset action (btnResetClick)  _( P3 / frmBank / Order / needs-recording )_
- `TXN-0040` frmBank: Warning - Selection error.  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0043` frmBank: Success - Saved successfully. (306)  _( P1 / frmBank / Order / needs-recording )_
- `TXN-0042` frmBank: Warning - Nothing changed. (244)  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0041` frmBank: Warning - Please add a note before saving.  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0032` frmBank: Close action (btnCancelClick)  _( P3 / frmBank / Order / needs-recording )_
- `TXN-0031` frmRefund: Detail grid cell click (dgvDetailsCellClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0030` frmRefund: toggle chkReCreate (Recreate Order) changes behavior  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0033` frmBank: Duplicate Bank action (btnDuplicateClick)  _( P3 / frmBank / Order / needs-recording )_
- `TXN-0036` frmBank: Print action (btnPrintClick)  _( P3 / frmBank / Order / needs-recording )_
- `TXN-0035` frmBank: Open Drawer action (btnOpenDrawerClick)  _( P3 / frmBank / Order / needs-recording )_
- `TXN-0034` frmBank: Success - Saved successfully. (187)  _( P1 / frmBank / Order / needs-recording )_
- `TXN-0053` frmBankDuplicate: OK action (btnOkClick)  _( P3 / frmBankDuplicate / Order / needs-recording )_
- `TXN-0052` frmBankDuplicate: Cancel action (btnCancelClick)  _( P3 / frmBankDuplicate / Order / needs-recording )_
- `TXN-0051` frmBankClose: Warning - Improper amount for currency  _( P2 / frmBankClose / Order / needs-recording )_
- `TXN-0054` frmBankSwap: Close action (btnCancelClick)  _( P3 / frmBankSwap / Order / needs-recording )_
- `TXN-C002` End-to-end: refund guard rails block an ineligible order  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-C001` End-to-end: find a refundable order and complete a refund  _( P1 / frmRefund / Order / needs-recording )_
- `TXN-0055` frmBankSwap: Swap action (btnOKClick)  _( P2 / frmBankSwap / Order / needs-recording )_
- `TXN-0046` frmBank: toggle cbo (cboSelectedIndexChanged) changes behavior  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0045` frmBank: Success - Voided successfully.  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0044` frmBank: Void Bank action (btnVoidBankClick)  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0047` frmBank: Warning - Improper amount for currency  _( P2 / frmBank / Order / needs-recording )_
- `TXN-0050` frmBankClose: Close action (btnCancelClick)  _( P3 / frmBankClose / Order / needs-recording )_
- `TXN-0049` frmBankClose: Success - Successful.  _( P1 / frmBankClose / Order / needs-recording )_
- `TXN-0048` frmBankClose: Close Shift action (btnOKClick)  _( P2 / frmBankClose / Order / needs-recording )_
- `TXN-0029` frmRefund: toggle chkTip (chkTipCheckedChanged) changes behavior  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0010` frmRefund: Clear action (btnClearClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0009` frmRefund: Void button requires Register.VoidOrder right  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0008` frmRefund: Refund button requires Register.Refund right  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0011` frmRefund: Close action (btnCancelClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0014` frmRefund: Open Ended Refund action (btnOpenClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0013` frmRefund: Item Search action (btnItemSearchClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0012` frmRefund: Find Order action (btnFindOrderClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0003` frmRefund: Warning - Order balance $0.00.  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0002` frmRefund: Warning - Order is marked as  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0001` frmRefund: Warning - Failed to load order  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0004` frmRefund: Warning - Order past (refund window)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0007` frmRefund: Warning - Credit card payment(s) not completed.  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0006` frmRefund: Warning - Gift certificate cannot be refunded  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0005` frmRefund: Warning - order has no payments attached  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0024` frmRefund: Success - Transaction successful.  _( P1 / frmRefund / Order / needs-recording )_
- `TXN-0023` frmRefund: Warning - Offline mode cannot process debit or EBT (2287)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0022` frmRefund: Warning - Refund to Gift Card was cancelled. (2159)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0025` frmRefund: Warning - Gift Card (2629)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0028` frmRefund: toggle chk (chkCheckedChanged) changes behavior  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0027` frmRefund: Tips action (btnTipClick)  _( P3 / frmRefund / Order / needs-recording )_
- `TXN-0026` frmRefund: Warning - Gift Card (2675)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0017` frmRefund: Success - Refund completed.  _( P1 / frmRefund / Order / needs-recording )_
- `TXN-0016` frmRefund: Warning - Error occurred processing.  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0015` frmRefund: Warning - Offline mode cannot process debit or EBT (1686)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0018` frmRefund: Refund action (btnRefundClick)  _( P1 / frmRefund / Order / needs-recording )_
- `TXN-0021` frmRefund: Warning - Refund to Gift Card was cancelled. (2136)  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0020` frmRefund: Warning - eGift disabled as a Refund Option.  _( P2 / frmRefund / Order / needs-recording )_
- `TXN-0019` frmRefund: Warning - Gift Certification disabled as a Refund Option.  _( P2 / frmRefund / Order / needs-recording )_


