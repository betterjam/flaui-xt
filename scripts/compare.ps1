<#
.SYNOPSIS
  Compare the AI agent's generated catalog against the EXISTING Ranorex suite. No AI.

.DESCRIPTION
  Answers "how does the agent's work stack up against what we already have?" for a feature:
    - MATCHED   : agent cases whose matches_existing names a real existing test case.
    - NEW       : agent cases with matches_existing=null (coverage the old suite lacks)
                  -> the value the agent adds.
    - MISSED    : existing test cases for this feature that NO agent case maps to
                  -> things the agent didn't reproduce (review: gap or intentional drop).
  Writes qa-ai\reports\compare-<feature>.json and prints a summary table.

.NOTES
  Read-only. Pair with validate-catalog.ps1: validate proves the agent didn't hallucinate;
  compare proves what the agent added/missed vs. reality.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Feature,
    [string]$CatalogDir  = (Join-Path $PSScriptRoot '..\catalog\features'),
    [string]$BaselineDir = (Join-Path $PSScriptRoot '..\catalog\_baseline'),
    [string]$ReportsDir  = (Join-Path $PSScriptRoot '..\reports'),
    # existing recordings whose feature-folder maps to this catalog feature.
    # Mapping is fuzzy; pass -RanorexFeatures to override (e.g. Register,Home for Transactions).
    [string[]]$RanorexFeatures
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir | Out-Null }

$catalogFile = Join-Path $CatalogDir "$Feature.yaml"
if (-not (Test-Path $catalogFile)) { Write-Error "No catalog for '$Feature' at $catalogFile"; exit 2 }

# --- agent cases: id + title + matches_existing (light YAML scan) ----------------
$agentCases = @()
$cur = $null
foreach ($ln in Get-Content $catalogFile) {
    if ($ln -match '^\s*-?\s*id:\s*([A-Za-z0-9_-]+)') {
        if ($cur) { $agentCases += $cur }
        $cur = [pscustomobject]@{ id = $Matches[1]; title = ''; matches = $null }
    }
    elseif ($cur -and $ln -match '^\s*title:\s*(.+?)\s*$') { $cur.title = $Matches[1].Trim('"',"'") }
    elseif ($cur -and $ln -match '^\s*matches_existing:\s*(.+?)\s*$') {
        $v = $Matches[1].Trim('"',"'"); $cur.matches = $(if ($v -eq 'null' -or -not $v) { $null } else { $v })
    }
}
if ($cur) { $agentCases += $cur }

# --- existing cases for this feature --------------------------------------------
$recs = Get-Content (Join-Path $BaselineDir 'recordings.json') -Raw | ConvertFrom-Json
if (-not $RanorexFeatures) { $RanorexFeatures = @($Feature) }   # default: same name
$existing = $recs | Where-Object { $RanorexFeatures -contains $_.feature } |
            ForEach-Object { $_.name } | Sort-Object -Unique

# --- classify -------------------------------------------------------------------
$matchedNames = $agentCases | Where-Object { $_.matches } | ForEach-Object { $_.matches }
$matched = $agentCases | Where-Object { $_.matches }
$new     = $agentCases | Where-Object { -not $_.matches }
$missed  = $existing   | Where-Object { $matchedNames -notcontains $_ }

$report = [pscustomobject]@{
    feature          = $Feature
    ranorexFeatures  = $RanorexFeatures
    agentCaseCount   = $agentCases.Count
    existingCount    = $existing.Count
    matched          = $matched | ForEach-Object { @{ id = $_.id; title = $_.title; existing = $_.matches } }
    new              = $new     | ForEach-Object { @{ id = $_.id; title = $_.title } }
    missed           = $missed
    summary          = @{ matched = $matched.Count; new = $new.Count; missed = $missed.Count }
}
$report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ReportsDir "compare-$Feature.json") -Encoding utf8

# --- print ----------------------------------------------------------------------
Write-Host ""
Write-Host "Comparison: $Feature  (existing Ranorex features: $($RanorexFeatures -join ', '))" -ForegroundColor Cyan
Write-Host ("  Agent cases : {0}" -f $agentCases.Count)
Write-Host ("  Existing    : {0}" -f $existing.Count)
Write-Host ("  MATCHED     : {0}  (agent reproduced existing coverage)" -f $matched.Count) -ForegroundColor Green
Write-Host ("  NEW         : {0}  (coverage the old suite lacked - agent's value-add)" -f $new.Count) -ForegroundColor Yellow
Write-Host ("  MISSED      : {0}  (existing cases the agent did not map - review)" -f $missed.Count) -ForegroundColor Magenta
if ($new.Count)    { Write-Host "  New cases:";    $new    | ForEach-Object { Write-Host "    + $($_.id) $($_.title)" } }
if ($missed.Count) { Write-Host "  Missed existing:"; $missed | ForEach-Object { Write-Host "    - $_" } }
Write-Host ""
Write-Host "Report: $(Join-Path $ReportsDir "compare-$Feature.json")"
