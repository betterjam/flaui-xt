# Test Catalog Schema (the contract)

The catalog is the **single source of truth** shared between the AI "detect" engine
and the Ranorex/NUnit "execute" runners. The AI writes/updates it (discovery + addition
modes); the runners and reports read it.

It lives as one YAML file per feature under `qa-ai/catalog/features/`, plus a generated
`coverage.json` rollup. YAML is hand-readable so QA can review AI output in a PR diff.

## Why a catalog (and not just generating tests directly)

**Independent-suite model:** we generate a brand-new Ranorex project under
`qa-ai/generated/` rather than extending `MyFirstExatouchTest`. The existing 135 recordings
are a **vetted reference** the AI mines for real element locators (so it never fabricates
coordinates) — not a thing we modify. After generation we **compare** the agent's catalog
against the existing suite (`scripts/compare.ps1`) to show matches, agent-found NEW cases,
and existing cases the agent didn't reproduce.

A WinForms app has no DOM; an AI cannot reliably author valid Ranorex locators for *new*
UI without the running app. The catalog separates the part the AI **can** own (what to
test, the flow, expected results, which existing building blocks to compose) from the part
that needs a record pass (net-new element locators). Each test case therefore carries a
`binding` status so the pipeline knows what is runnable now vs. what needs QA to record.

## Feature file: `qa-ai/catalog/features/<feature>.yaml`

```yaml
feature: Register                      # maps to app folder 2.0.x\Transactions (see source_paths)
description: Point-of-sale order entry, tender, and refund flows.
source_paths:                          # app code that defines this feature (grounds discovery)
  - 2.0.x/Transactions
  - 2.0.x/Services/TenderServices
flavors: [CFD, BO, DEMO, TRAINING]     # #if build flavors that alter this feature's behavior
                                       # (discovery detects these from #if blocks in source_paths)

test_cases:
  - id: REG-001                        # stable ID; never reused. Prefix = feature.
    title: Cash tender for a single taxable item
    kind: atomic                       # atomic (one behavior) | composite (chains atomics)
    priority: P1                       # P1 smoke / P2 regression / P3 edge
    type: e2e                          # e2e (Ranorex UI) | integration (NUnit) | unit
    flavors: [CFD]                     # which flavors this case is meaningful in
    # --- categorization (all four axes; see "Categories" below) -------------
    categories:
      test_kind: happy-path            # happy-path | validation | error-guard | permission | edge-case
      ui_area: frmRegister             # the form the flow lives in
      entity: Order                    # business object: Vendor | PurchaseOrder | Receiving | Item | Order ...
    # composite cases add:  chains: [PUR-001, PUR-010, PUR-020]   # atomic ids run in order
    preconditions:
      - Logged in as admin
      - At least one taxable item exists (e.g. "Apples")
    steps:                             # plain-language, runner-agnostic
      - Go to Register
      - Add item "Apples" to the order
      - Go to Pay tab
      - Pay with cash, exact amount
    expected:
      - Order total reflects item price + tax
      - Receipt tape lists "Apples"
      - Order closes to Paid status
    # --- the bridge to execution -------------------------------------------
    runner: ranorex
    composes:                          # existing reusable building blocks this case reuses
      - GoToRegister                   # -> MyFirstExatouchTest\Home\GoToRegister.rxrec
      - AddItemToOrder                 # -> Register\Register Tab\AddItemToOrder.rxrec
      - GoToPayTab
      - PayWithCash
      - VerifyItemsInRegisterTape      # -> Code Modules\Verification\...
    binding: covered                   # covered | partial | needs-recording
    rxtst: RegressionTestSuite.rxtst   # suite the runnable case lives in (when covered)
    rxtest_case: PayWithCashSingleItem # test-case node name inside the suite
    new_controls: []                   # net-new UI locators QA must record (when not covered)
    # --- traceability: which app files make up this test's flow -------------
    # Discovery fills this from the forms/managers it read while authoring the case.
    # impact.ps1 uses it: when any listed file changes in a diff, this case is flagged
    # stale and queued for the agent to regenerate. This is what powers addition mode.
    source_files:
      - 2.0.x/Purchasing/frmPO.cs
      - 2.0.x/Managers/PurchaseOrderManager.cs   # only files actually on the flow path
    # --- code evidence this test is derived FROM (the rule: tests come from code) -----
    # Each anchor MUST exist in catalog/_baseline/evidence-<Feature>.json (file:line:text or
    # a handler). validate-catalog.ps1 rejects cases citing anchors that aren't in the code.
    # impact.ps1 uses these to decide, on a diff: covered anchors -> UPDATE the test; new
    # anchors with no citing test -> NEW test needed.
    evidence:
      - { file: 2.0.x/Purchasing/frmPO.cs, line: 175, text: "Purchase order has been voided, and cannot be modified." }
    flow_hash: ""                      # set by impact.ps1 = hash of the listed source_files'
                                       # content at authoring time; a mismatch = drift to review
    source_refs:                       # ticket / PR provenance (addition mode fills this)
      tickets: []
      prs: []
    origin: discovery                  # discovery | addition
```

## Phase 1 goal: exhaustive, categorized, flow-organized discovery

The first job of discovery is to derive **every possible test the existing code supports** —
no invented behavior, nothing the code doesn't do. Concretely:

### Exhaustiveness — one test per code anchor
`extract-evidence.ps1` lists every code anchor for the feature: each `Lib.Warning/Success/
Error/Dialog` message and each `Click/Save` handler. **Every anchor must be covered by at
least one atomic test case that cites it in `evidence`.** The anchor count is the coverage
target; `validate-catalog.ps1` reports any uncited anchor as a hole. cases >= anchors.

### Categorization — all four axes, on every case (`categories:` + `priority`)
- `test_kind`: derived from the anchor — `Lib.Success` -> happy-path; `Lib.Warning` guarding
  input -> validation; `Lib.Warning` blocking an action (e.g. "voided, cannot be modified")
  -> error-guard; a `*.Access`/security check -> permission; boundary/limit -> edge-case.
- `ui_area`: the `frm*` the flow lives in.
- `entity`: the business object acted on (Vendor / PurchaseOrder / Receiving / Item / ...).
- `priority`: P1 core/money path, P2 regression, P3 edge.

### Flow organization — atomic + composite (`kind:`)
- `atomic`    — one behavior / one anchor (the bulk; what makes coverage exhaustive).
- `composite` — an end-to-end flow that **chains atomic ids** via `chains: [...]` (e.g.
  create vendor -> create PO -> receive stock). Composites add no new anchors; they assert
  the realistic ordering. This mirrors the existing Ranorex recording (atomic) + suite
  (composite) model, so generation maps cleanly onto `qa-ai/generated/`.

## `binding` values — readiness of the NEW generated case

Coverage here is about the NEW suite under `qa-ai/generated/`, grounded by the existing one:

- `locators-known`   — every control this case touches has a real locator we can lift from an
                       existing recording (named in `reference_recordings`); the generated
                       case is fully authorable now.
- `partial`          — some controls have known locators, some are new; AI generated a stub
                       with the knowns and flagged the rest in `new_controls`.
- `needs-recording`  — touches UI with no locator anywhere in the existing 135 recordings;
                       AI authored the plan, QA records the `new_controls` once.

Two extra fields support the independent-suite + comparison model:

- `reference_recordings: []` — existing `.rxrec` names whose locators/flows the AI reused as
                       ground truth (must exist in `_baseline/recordings.json`).
- `matches_existing: <name|null>` — the existing test case this new case corresponds to, if
                       any. `null` = the agent considers this a NET-NEW case the old suite
                       lacks. `compare.ps1` rolls these up into the comparison report.

## `coverage.json` (generated rollup, by reconcile.ps1)

```json
{
  "generated": "<stamp injected by caller, not the script>",
  "totals": { "features": 26, "cases": 0, "covered": 0, "needs_recording": 0 },
  "existing_recordings": 135,
  "existing_suites": 7,
  "by_feature": {
    "Register": { "cases": 12, "covered": 10, "gap": 2 }
  },
  "uncovered_features": ["Purchasing", "Reports", "Calendar", "Staff"]
}
```

## IDs and stability rules

- IDs are immutable. Renaming a title keeps the ID. Deleting a case tombstones it
  (`status: removed`) rather than freeing the ID — keeps report history coherent.
- Addition mode MUST reconcile against existing cases (by composed-steps similarity and
  title) before creating a new ID, to avoid duplicating the 135 recordings QA already built.
