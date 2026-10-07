## Area focus — Items

(Inherits the BASE rules above; this only adds Items-specific knowledge.)

The Items area is Exatouch's product catalog: the Item List grid (`dgvList`, rows exposed as
"Row 0".."Row N", names live in cells) and the Item Detail form (`frmItems`). Item Detail has
~13 tabs in runtime-map.json: Details, Pricing, Tax & GL, Promos, Inventory, Inventory Options,
Purchasing, Modifiers, Inventory Matrix, Inventory Adjustment (+ others). ~179 fields total; only
~10–16 are reachable at once because most are hidden behind their tab until clicked.

Items-specific things a reviewer must weigh:
- **Required fields / validations** that gate Save: SKU (`*`, UPC/EAN format), Cost ("Cost is
  required"), PLU/Code (unique), Price (cannot be blank). A test that edits-and-saves must prove
  it didn't silently hit one of these — Exatouch shows an `frmPopup` warning window on invalid
  values. "Save returned but the form stayed open / a popup was up" = the save was BLOCKED.
- **Money paths are high severity**: price, cost, promos, tax/GL mappings. An untested or
  falsely-passing money field is a `high` finding.
- **Tab-hidden coverage**: a field existing in runtime-map but on a tab the test never clicks is a
  real gap — name the tab and the field count.
- **Round-trip reality**: the app reformats values (9275 -> 9275.00); numeric round-trip should
  match by value, not string. Promo2/3/4 are numeric and legitimately reject strings -> 0.00.
