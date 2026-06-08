<#
.SYNOPSIS
  Unattended discovery loop: cover every area's tests until the ledger is complete.

.DESCRIPTION
  The "run all weekend" driver. Repeatedly:
    1. ledger.ps1 -> pick the area with the most UNCOVERED anchors
    2. refresh that area's evidence (extract-evidence / extract-managers / reconcile)
    3. invoke the AI agent (Claude Agent SDK via run-agent.ps1) with prompts/cover-area.md
    4. validate-catalog.ps1 + coverage.ps1  (deterministic gates; retry agent on red)
    5. impact.ps1 -Stamp, then git commit the area on the work branch
  Terminates when ledger.ps1 exits 0 (grandUncovered == 0 == "no new tests to do"),
  OR a guardrail trips: cost cap, per-area retry limit, or wall-clock deadline.

.SECURITY
  Reads the API key ONLY from $env:ANTHROPIC_API_KEY. Refuses to run if it is missing or
  looks like it was hardcoded. Writes only under qa-ai/. Never touches 2.0.x app code.

.PARAMETER MaxUsd        Stop once estimated spend reaches this (cost cap).
.PARAMETER DeadlineUtc   Hard wall-clock stop (e.g. "2026-06-08T10:00:00Z").
.PARAMETER MaxRetries    Per-area agent retries before skipping & logging.
.PARAMETER Branch        Git branch to commit area work onto.
.PARAMETER WhatIf        Plan only: show the area order and exit without calling the agent.
#>
[CmdletBinding()]
param(
    [double]$MaxUsd       = 50,
    [string]$DeadlineUtc  = '',
    [int]$MaxRetries      = 3,
    [string]$Branch       = 'qa-ai-discovery',
    [switch]$WhatIf,
    [string]$QaRoot       = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path,
    [string]$RepoRoot     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)
$ErrorActionPreference = 'Stop'
$scripts   = Join-Path $QaRoot 'scripts'
$logFile   = Join-Path $QaRoot 'reports\weekend-run.log'
$heartbeat = Join-Path $QaRoot 'reports\heartbeat.json'
$pidFile   = Join-Path $QaRoot 'reports\runner.pid'
function Log($m) { $line = "[{0}] {1}" -f (Get-Date -Format 'u'), $m; Write-Host $line; Add-Content $logFile $line }
function Beat($area, $round) {
    # watchdog reads this: a stale 'lastBeat' => the runner is stuck/dead and must be restarted.
    @{ lastBeat=(Get-Date).ToUniversalTime().ToString('u'); pid=$PID; area=$area; round=$round } |
        ConvertTo-Json | Set-Content $heartbeat -Encoding utf8
}
Set-Content $pidFile $PID -Encoding utf8

# --- security/auth gate ---------------------------------------------------------
# Accept EITHER an env API key (SDK path) OR an authenticated `claude` CLI (fallback).
$key = $env:ANTHROPIC_API_KEY
$haveClaude = [bool](Get-Command claude -ErrorAction SilentlyContinue)
if ([string]::IsNullOrWhiteSpace($key) -and -not $haveClaude) {
    Write-Error "No auth: set ANTHROPIC_API_KEY (env only) OR log in the 'claude' CLI."; exit 2
}
if ($key -and ($key.Length -lt 40 -or $PSBoundParameters.Values -contains $key)) {
    Write-Error "API key looks wrong or hardcoded. Use the environment variable only."; exit 2
}
if (-not $key) { Log "No env key; using authenticated 'claude' CLI for agent calls." }

$deadline = if ($DeadlineUtc) { [datetime]::Parse($DeadlineUtc).ToUniversalTime() } else { [datetime]::MaxValue }
$spent = 0.0   # running USD estimate, updated by run-agent.ps1 output

$deadlineLabel = if ([datetime]::Equals($deadline,[datetime]::MaxValue)) { 'none' } else { $DeadlineUtc }
Log "Weekend runner start. Branch=$Branch  MaxUsd=$MaxUsd  Deadline=$deadlineLabel"

# --- refresh denominators once up front ----------------------------------------
& (Join-Path $scripts 'reconcile.ps1')        | Out-Null
& (Join-Path $scripts 'map-app.ps1')          | Out-Null
& (Join-Path $scripts 'extract-managers.ps1') | Out-Null

# ensure work branch (only if repo is a git repo)
$isGit = Test-Path (Join-Path $RepoRoot '.git')
if ($isGit -and -not $WhatIf) {
    Push-Location $RepoRoot
    try { git rev-parse --verify $Branch *> $null; if ($LASTEXITCODE -ne 0) { git checkout -b $Branch | Out-Null } else { git checkout $Branch | Out-Null } }
    finally { Pop-Location }
}

# --- WhatIf: print the planned area order ONCE and exit (no agent, no loop) ------
if ($WhatIf) {
    & (Join-Path $scripts 'ledger.ps1') *> $null
    $l = Get-Content (Join-Path $QaRoot 'reports\ledger.json') -Raw | ConvertFrom-Json
    Log "PLAN (WhatIf) - areas the runner would cover, worst-first:"
    $i = 0
    foreach ($a in ($l.areas | Where-Object { $_.uncovered -gt 0 } | Sort-Object uncovered -Descending)) {
        $i++; Log ("  {0}. {1,-14} {2,4} uncovered  ({3}% now)" -f $i, $a.area, $a.uncovered, $a.pct)
    }
    Log ("Total uncovered: {0}/{1} anchors. Cost cap `$$MaxUsd. Runner stops when this hits 0." -f $l.grandUncovered, $l.grandTotal)
    Remove-Item $heartbeat,$pidFile -ErrorAction SilentlyContinue
    return
}

# --- main loop -----------------------------------------------------------------
$round = 0
while ($true) {
    $round++
    Beat '(loop)' $round   # heartbeat so the 3-hour watchdog knows we're alive

    # completion check = the ledger. exit 0 means nothing uncovered = DONE.
    & (Join-Path $scripts 'ledger.ps1') *> $null
    if ($LASTEXITCODE -eq 0) { Log "LEDGER COMPLETE - every anchor covered. Done."; Remove-Item $heartbeat,$pidFile -ErrorAction SilentlyContinue; break }

    # guardrails
    if ([datetime]::UtcNow -ge $deadline) { Log "Deadline reached. Stopping with partial coverage."; break }
    if ($spent -ge $MaxUsd)               { Log "Cost cap (`$$MaxUsd) reached (est `$$([math]::Round($spent,2))). Stopping."; break }

    # pick the most-uncovered area from the ledger report
    $ledger = Get-Content (Join-Path $QaRoot 'reports\ledger.json') -Raw | ConvertFrom-Json
    $target = $ledger.areas | Where-Object { $_.uncovered -gt 0 } | Sort-Object uncovered -Descending | Select-Object -First 1
    if (-not $target) { Log "No uncovered areas left."; break }
    $area = $target.area
    Beat $area $round
    Log "Round $round -> area '$area' ($($target.uncovered) uncovered, $($target.pct)%)"

    # refresh this area's evidence
    if ($area -eq 'Managers') { & (Join-Path $scripts 'extract-managers.ps1') | Out-Null }
    else { & (Join-Path $scripts 'extract-evidence.ps1') -Feature $area | Out-Null }

    # agent attempts with deterministic gates
    $ok = $false
    for ($try = 1; $try -le $MaxRetries; $try++) {
        Log "  agent attempt $try/$MaxRetries on '$area'"
        # run-agent.ps1 calls the Claude Agent SDK with prompts/cover-area.md ({{AREA}} subbed),
        # prints a line 'COST_USD=<n>' which we accumulate. (Built separately.)
        $out = & (Join-Path $scripts 'run-agent.ps1') -Area $area -PromptFile (Join-Path $QaRoot 'prompts\cover-area.md') 2>&1
        $out | ForEach-Object { Log "    $_" }
        $cost = ($out | Select-String 'COST_USD=([\d.]+)' | Select-Object -Last 1)
        if ($cost) { $spent += [double]$cost.Matches[0].Groups[1].Value }

        & (Join-Path $scripts 'validate-catalog.ps1') -Feature $area *> $null
        $valOk = ($LASTEXITCODE -eq 0)
        & (Join-Path $scripts 'coverage.ps1') -Feature $area *> $null
        $covOk = ($LASTEXITCODE -eq 0)
        if ($valOk -and $covOk) { $ok = $true; break }
        Log "  gates: validate=$valOk coverage=$covOk (est spend `$$([math]::Round($spent,2)))"
        if ($spent -ge $MaxUsd) { break }
    }

    if (-not $ok) { Log "  '$area' not complete after $MaxRetries tries - logging and moving on."; }

    # stamp + commit whatever progress was made
    & (Join-Path $scripts 'impact.ps1') -Stamp *> $null
    if ($isGit) {
        Push-Location $RepoRoot
        try {
            git add (Join-Path $QaRoot 'catalog') (Join-Path $QaRoot 'reports') *> $null
            git commit -m "discovery: $area coverage pass (round $round)" *> $null
        } finally { Pop-Location }
    }
    Log "  committed '$area'. est spend `$$([math]::Round($spent,2))"
}

# final ledger snapshot
& (Join-Path $scripts 'ledger.ps1') 2>&1 | ForEach-Object { Log $_ }
Log "Weekend runner finished. Total est spend `$$([math]::Round($spent,2))."
