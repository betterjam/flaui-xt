<#
.SYNOPSIS
  Bridge: invoke the Claude Agent SDK to cover one area, headless. Reads key from env only.

.DESCRIPTION
  Substitutes {{AREA}} into prompts/cover-area.md and runs the discovery agent via the
  Claude Agent SDK (Node). The agent reads evidence/catalog, writes only
  qa-ai/catalog/features/<Area>.yaml, and self-verifies. This script prints the agent's
  summary line plus "COST_USD=<n>" which weekend-runner.ps1 accumulates against the cost cap.

  Requires: Node.js + @anthropic-ai/claude-agent-sdk installed in qa-ai/agent/.
  The Node program (agent/cover-area.mjs) is the actual SDK call; this PS wrapper handles
  arg/prompt prep and the env-only key rule so nothing secret is ever on a command line.

.NOTES
  Pinned model: claude-opus-4-8. Tools restricted to Read/Grep/Glob/Bash/Write within qa-ai.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Area,
    [Parameter(Mandatory)] [string]$PromptFile,
    [string]$QaRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$Model  = 'claude-opus-4-8'
)
$ErrorActionPreference = 'Stop'

# Auth: either an env API key (SDK path) OR an authenticated `claude` CLI (fallback path).
$haveKey    = -not [string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY)
$haveClaude = [bool](Get-Command claude -ErrorAction SilentlyContinue)
if (-not $haveKey -and -not $haveClaude) {
    Write-Error "No auth available: set ANTHROPIC_API_KEY (env only) OR log in the 'claude' CLI."; exit 2
}

$node = (Get-Command node -ErrorAction SilentlyContinue)
$agentJs = Join-Path $QaRoot 'agent\cover-area.mjs'
$sdkInstalled = Test-Path (Join-Path $QaRoot 'agent\node_modules\@anthropic-ai\claude-agent-sdk')
# Use the SDK only when we have an env key AND the SDK is installed; otherwise use the claude CLI.
$useSdk = $haveKey -and $node -and (Test-Path $agentJs) -and $sdkInstalled
if (-not $useSdk) {
    # Fallback path: Claude Code headless (-p) using the logged-in claude CLI.
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if ($claude) {
        $prompt = (Get-Content $PromptFile -Raw).Replace('{{AREA}}', $Area)
        Write-Host "Using Claude Code headless fallback for area '$Area'."
        # --output-format json gives us a result object incl. total_cost_usd so the cap is enforced.
        $raw = $prompt | & claude -p --output-format json --permission-mode acceptEdits --model $Model --add-dir $QaRoot 2>&1
        $cost = 0
        try {
            $obj = $raw | ConvertFrom-Json
            if ($obj.result) { Write-Host $obj.result }
            if ($obj.total_cost_usd) { $cost = [double]$obj.total_cost_usd }
        } catch {
            # not JSON (e.g. an error) -> surface it; cost stays 0, deadline/retries still bound the run
            Write-Host $raw
        }
        Write-Host ("COST_USD={0}" -f $cost)
        return
    }
    Write-Error "Neither agent/cover-area.mjs (SDK) nor 'claude' CLI found. Install one. See agent/README.md."
    exit 3
}

# Pass area + prompt path as args; key stays in the inherited env, never on the command line.
& node $agentJs --area $Area --prompt $PromptFile --model $Model --qa-root $QaRoot
