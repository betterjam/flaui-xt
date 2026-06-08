# qa-ai — AI-driven E2E test discovery & authoring for Exatouch

Two modes, one engine, one shared catalog. The AI **decides what to test**; Ranorex/NUnit
**run the tests**. The two are decoupled by a versioned catalog (the contract).

```
2.0.x (app)  ─┐
              ├─► reconcile.ps1 ─► _baseline/*.json ─► [Discovery Agent] ─► catalog/features/*.yaml
Ranorex tests ┘   (deterministic)                       (AI brain)            (the contract)
                                                                                    │
                                                                  Ranorex Test Suite Runner CLI / NUnit
                                                                  (execute cases the catalog selects)
```

## Layout
- `catalog/SCHEMA.md` — the contract. Feature→cases→(maps to existing recordings).
- `catalog/_baseline/` — generated coverage baseline (run reconcile.ps1; not hand-edited).
- `catalog/features/*.yaml` — the catalog the AI writes (reviewed in PRs).
- `scripts/reconcile.ps1` — deterministic Ranorex parser. No AI. Run first.
- `prompts/discovery.md` — the discovery-agent instructions.

## Run discovery for one feature
```powershell
# 1. Refresh the coverage baseline (read-only, deterministic)
qa-ai\scripts\reconcile.ps1

# 2. Run the discovery agent (Claude Code) pointed at prompts\discovery.md,
#    naming one feature, e.g. "Purchasing" (an uncovered area).
#    It writes qa-ai\catalog\features\purchasing.yaml and reports the top gaps.
```

## The seam (important)
The AI cannot author valid Ranorex element locators for *new* UI without the running app.
So each catalog case carries a `binding`: `covered` (runnable now), `partial`, or
`needs-recording` (AI wrote the plan + listed new controls; QA records locators once).
This is by design — it's where the "detect" tool hands off to the "execute" tool.

## Status
- [x] Catalog schema (contract)
- [x] Deterministic reconciler (135 recordings / 7 suites parsed)
- [x] Discovery agent prompt
- [ ] First feature catalog (pick an uncovered area: Purchasing / Reports / Calendar / Staff)
- [ ] Runner wrapper: select catalog cases -> Ranorex Test Suite Runner CLI -> JUnit XML
- [ ] Addition mode (ticket + PR diff -> append cases)
