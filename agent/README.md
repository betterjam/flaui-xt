# qa-ai/agent — the headless model bridge

`run-agent.ps1` invokes the model to cover one area. Two engines, in priority order:

## 1. Claude Agent SDK (preferred for unattended runs)
`cover-area.mjs` calls `@anthropic-ai/claude-agent-sdk` with a pinned model and a qa-ai-scoped
working dir, and prints `COST_USD=<n>` so the weekend runner can enforce the $50 cap precisely.

Install once:
```powershell
npm install --prefix C:\Dev\Exatouch\qa-ai\agent
```
Key is read from `$env:ANTHROPIC_API_KEY` (never an argument, never logged).

## 2. claude -p fallback (already installed here)
If the SDK isn't installed, `run-agent.ps1` falls back to the `claude` CLI headless:
`claude -p --permission-mode acceptEdits --model claude-opus-4-8 --add-dir <qa-ai>`.
This reuses your existing Claude Code auth. Note: per-call USD cost isn't parsed in this path,
so the cost cap leans on the deadline + per-area retry limits instead. Prefer the SDK if you
want hard dollar accounting.

## What the agent does each call
Reads `prompts/cover-area.md` (with `{{AREA}}` substituted), then for that area: reads the
evidence anchors + catalog + reusable recordings, authors atomic + composite test cases that
cite every uncovered anchor, and self-verifies with `validate-catalog.ps1` + `coverage.ps1`
until coverage is 100%. Writes only `qa-ai/catalog/features/<Area>.yaml`. Never touches app code.

## Smoke test (spends a small amount — rotate your key first if it was ever exposed)
```powershell
$env:ANTHROPIC_API_KEY = "sk-ant-...new..."
C:\Dev\Exatouch\qa-ai\scripts\run-agent.ps1 -Area Calendar -PromptFile C:\Dev\Exatouch\qa-ai\prompts\cover-area.md
# Calendar is the smallest area (51 anchors) -> cheapest first real run.
```
