<#
.SYNOPSIS
  Measure exhaustiveness: are all code anchors covered by a test? No AI.

.DESCRIPTION
  Phase-1 goal is "every possible test the existing code supports." This script cross-
  references the deterministic code anchors (evidence-<Feature>.json from extract-evidence.ps1)
  against the evidence each catalog case cites. It reports:
    - COVERED   anchors (some test cites file:line)
    - UNCOVERED anchors (a real code behavior with no test) -> the discovery backlog
    - a coverage % per feature, and atomic/composite counts.

  This is the scoreboard for exhaustive discovery: drive UNCOVERED to zero.

.OUTPUTS
  qa-ai\reports\coverage-<Feature>.json + console. Exit 1 if any anchor uncovered.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Feature,
    [string]$CatalogDir  = (Join-Path $PSScriptRoot '..\catalog\features'),
    [string]$BaselineDir = (Join-Path $PSScriptRoot '..\catalog\_baseline'),
    [string]$ReportsDir  = (Join-Path $PSScriptRoot '..\reports')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir | Out-Null }

$evFile = Join-Path $BaselineDir "evidence-$Feature.json"
if (-not (Test-Path $evFile)) { Write-Error "No evidence file. Run extract-evidence.ps1 -Feature $Feature first."; exit 2 }
$evidence = Get-Content $evFile -Raw | ConvertFrom-Json

$catalogFile = Join-Path $CatalogDir "$Feature.yaml"
if (-not (Test-Path $catalogFile)) { Write-Error "No catalog for '$Feature'."; exit 2 }

# --- collect anchors cited by the catalog ---------------------------------------
$cited = @{}; $atomic = 0; $composite = 0; $caseCount = 0
$lines = Get-Content $catalogFile; $sect = $null
foreach ($ln in $lines) {
    if ($ln -match '^\s*-?\s*id:\s*([A-Za-z0-9_-]+)') { $caseCount++; $sect=$null }
    elseif ($ln -match '^\s*kind:\s*(\w+)') { if ($Matches[1] -eq 'atomic') {$atomic++} elseif ($Matches[1] -eq 'composite') {$composite++} }
    elseif ($ln -match '^\s*evidence:\s*$') { $sect='ev' }
    elseif ($sect -eq 'ev' -and $ln -match '\{\s*file:\s*(\S+?)\s*,\s*line:\s*(\d+)') {
        $cited[("{0}:{1}" -f $Matches[1].TrimEnd(',').Replace('/','\'), $Matches[2])] = $true
    }
    elseif ($ln -match '^\s*\w+:') { $sect=$null }
}

# --- flatten typed form anchors (message/action/toggle/permission/calc) ---------
# Every anchor is a testable behavior; coverage = anchors cited by some test's evidence.
$allAnchors = @()
foreach ($frm in $evidence.forms) {
    foreach ($a in $frm.anchors) {
        $allAnchors += [pscustomobject]@{
            file = $frm.file; line = $a.line; type = $a.type
            text = $(if ($a.text) { $a.text } elseif ($a.label) { $a.label } elseif ($a.handler) { $a.handler } elseif ($a.right) { "perm:$($a.right)" } else { $a.expr })
        }
    }
}

$covered = @(); $uncovered = @()
foreach ($m in $allAnchors) {
    $key = "{0}:{1}" -f $m.file.Replace('/','\'), $m.line
    if ($cited.ContainsKey($key)) { $covered += $m } else { $uncovered += $m }
}
$total = $allAnchors.Count
$pct = if ($total) { [math]::Round(100.0 * $covered.Count / $total, 1) } else { 100 }

$report = [pscustomobject]@{
    feature = $Feature
    anchorsTotal = $total
    covered = $covered.Count
    uncovered = $uncovered.Count
    coveragePct = $pct
    caseCount = $caseCount; atomic = $atomic; composite = $composite
    uncoveredAnchors = $uncovered
}
$report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ReportsDir "coverage-$Feature.json") -Encoding utf8

$uncByType = $uncovered | Group-Object type | ForEach-Object { "{0} {1}" -f $_.Count, $_.Name }
Write-Host ""
Write-Host "Exhaustiveness: $Feature" -ForegroundColor Cyan
Write-Host ("  code anchors (all types) : {0}" -f $total)
Write-Host ("  covered                  : {0}" -f $covered.Count) -ForegroundColor Green
Write-Host ("  UNCOVERED                : {0}   [{1}]" -f $uncovered.Count, ($uncByType -join ', ')) -ForegroundColor $(if($uncovered.Count){'Red'}else{'Green'})
Write-Host ("  coverage                 : {0}%" -f $pct)
Write-Host ("  cases: {0}  (atomic {1} / composite {2})" -f $caseCount, $atomic, $composite)
if ($uncovered.Count) {
    Write-Host "  Uncovered code behaviors (need a test) - first 40:" -ForegroundColor Red
    $uncovered | Select-Object -First 40 | ForEach-Object { Write-Host ("    - {0}:{1}  [{2}] {3}" -f $_.file, $_.line, $_.type, $_.text) }
}
Write-Host ""
Write-Host "Report: $(Join-Path $ReportsDir "coverage-$Feature.json")"
if ($uncovered.Count) { exit 1 } else { Write-Host "EXHAUSTIVE: every code anchor has a test." -ForegroundColor Green; exit 0 }
