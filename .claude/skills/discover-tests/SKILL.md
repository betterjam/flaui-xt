---
name: discover-tests
description: >
  Discover the E2E tests that SHOULD exist for an Exatouch feature and emit them into the
  versioned test catalog, reconciled against existing Ranorex recordings. Use when the user
  asks to find/enumerate/audit tests for a feature, build a test plan, or grow E2E coverage
  for this WinForms POS. The AI decides WHAT to test; it never runs tests or invents Ranorex
  locators. Outputs are deterministic and reviewable in a PR diff.
argument-hint: <FeatureName>   e.g. Purchasing, Reports, Register
---

# discover-tests

Discovery mode for the Exatouch E2E test pipeline. One feature per invocation.
All paths below are relative to the `qa-ai/` root (this skill lives in `qa-ai/.claude/`).

## Boundary: never touch core application code (absolute)

READ `2.0.x/` and `MyFirstExatouchTest/` freely; WRITE only under `qa-ai/`. This skill and
its agent must never create/edit/move/delete any file under `2.0.x/`, the sibling entity/
repo/XTPE2 projects, or `MyFirstExatouchTest/`. The test system cannot be allowed to modify
the product it tests. If something seems to need an app-code change, stop and report it.

## Determinism contract (non-negotiable)

The output must be **stable and repeatable** — running this twice on an unchanged feature
must produce the same catalog (same IDs, same cases). To guarantee that:
- Test-case IDs are immutable; update existing entries in place, never renumber or reorder.
- Do not stamp timestamps, random values, or run-specific data into catalog files.
- The coverage baseline comes only from the deterministic `reconcile.ps1` (no AI), so the
  AI always reasons over the same ground truth.
- Never fabricate Ranorex `loc="x;y"` coordinates or RanoreXPaths. Unknown locators are
  declared under `new_controls` for a QA record pass — that keeps generated tests honest
  and the executable tests deterministic.

## Steps

1. **Refresh the baseline** (deterministic grounding):
   ```
   qa-ai\scripts\reconcile.ps1
   ```
   This writes `qa-ai/catalog/_baseline/{recordings,suites,baseline}.json`. If it errors,
   stop and report — do not guess coverage.

2. **Read the contract and grounding**, in order:
   - `qa-ai/catalog/SCHEMA.md`
   - `qa-ai/catalog/_baseline/baseline.json`, `recordings.json`, `suites.json`
   - `qa-ai/prompts/discovery.md`  ← the full discovery method; follow it exactly.

3. **Run discovery for the named feature** per `discovery.md`: map feature→source folder,
   enumerate flows, detect `#if` flavor branches, reconcile against existing recordings,
   prioritize, and write `qa-ai/catalog/features/<feature>.yaml` per SCHEMA.md.

4. **Report** to the user: case counts by priority, covered vs. needs-recording, and the
   top 3 highest-value coverage gaps.

## Hand-off to execution (the "create deterministic tests" half)

This skill stops at the catalog. The downstream, deterministic execution path is:
- `binding: covered` cases → already runnable via the **Ranorex Test Suite Runner CLI**.
- `binding: needs-recording` cases → QA records the listed `new_controls` once, then the
  case becomes `covered`.
- A future runner wrapper selects catalog cases by id/priority/flavor and invokes Ranorex
  (JUnit XML out). Pure logic flows may instead become NUnit tests in `Exatouch.UnitTests`.

Prefer composing the project's existing recordings and `[UserCodeMethod]`s
(`SelectOrderType`, `AddItemToOrder`, `PayWithCash`, `VerifyItemsInRegisterTape`, …) so
generated tests are stable and reuse vetted building blocks.

## For large/complex features

Delegate the crawl to the `qa-test-discovery` subagent so the feature's source and the
135-recording inventory stay out of the main context window. The subagent returns the
written file path + the gap summary.
