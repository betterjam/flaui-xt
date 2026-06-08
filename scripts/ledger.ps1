<#
.SYNOPSIS
  Completeness ledger: coverage of every area against the app-map denominator. No AI.

.DESCRIPTION
  The single scoreboard for "are the tests complete?" For each feature area it compares the
  testable anchors that EXIST (from app-map.json + evidence-Managers.json) against the anchors
  COVERED by the catalog (cases citing them). "Complete" == 0 uncovered app-wide. This is what
  the pipeline drives to zero and what proves the job is genuinely done, area by area.

  Per area: total anchors, covered, uncovered, coverage%, and whether a catalog exists yet.

.OUTPUTS
  qa-ai\reports\ledger.json + console table. Exit 1 while anything is uncovered.
#>
[CmdletBinding()]
param(
    [string]$BaselineDir = (Join-Path $PSScriptRoot '..\catalog\_baseline'),
    [string]$CatalogDir  = (Join-Path $PSScriptRoot '..\catalog\features'),
    [string]$ReportsDir  = (Join-Path $PSScriptRoot '..\reports')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir | Out-Null }

# anchors cited by a given catalog file (file:line keys)
function Get-CitedKeys([string]$catalogFile) {
    $cited = @{}
    if (-not (Test-Path $catalogFile)) { return $cited }
    $sect = $null
    foreach ($ln in Get-Content $catalogFile) {
        if ($ln -match '^\s*evidence:\s*$') { $sect='ev' }
        elseif ($sect -eq 'ev' -and $ln -match '\{\s*file:\s*(\S+?)\s*,\s*line:\s*(\d+)') {
            $cited[("{0}:{1}" -f $Matches[1].TrimEnd(',').Replace('/','\'), $Matches[2])] = $true
        }
        elseif ($ln -match '^\s*\w+:') { $sect=$null }
    }
    return $cited
}

$rows = @()

# --- form-based areas from the app map -----------------------------------------
$mapFile = Join-Path $BaselineDir 'app-map.json'
if (-not (Test-Path $mapFile)) { Write-Error "Run map-app.ps1 first."; exit 2 }
$map = Get-Content $mapFile -Raw | ConvertFrom-Json

foreach ($a in $map.areas) {
    $total = $a.anchorTotal
    # covered = anchors cited by this area's catalog, IF an evidence file exists to give line keys.
    $catalog = Join-Path $CatalogDir ("{0}.yaml" -f $a.area)
    $evFile  = Join-Path $BaselineDir ("evidence-{0}.json" -f $a.area)
    $covered = 0
    if ((Test-Path $catalog) -and (Test-Path $evFile)) {
        $cited = Get-CitedKeys $catalog
        $ev = Get-Content $evFile -Raw | ConvertFrom-Json
        foreach ($frm in $ev.forms) { foreach ($an in $frm.anchors) {
            if ($cited.ContainsKey(("{0}:{1}" -f $frm.file.Replace('/','\'), $an.line))) { $covered++ }
        } }
    }
    $rows += [pscustomobject]@{
        area=$a.area; layer='e2e'; total=$total; covered=$covered
        uncovered=($total-$covered); pct=$(if($total){[math]::Round(100.0*$covered/$total,1)}else{100})
        catalog=(Test-Path $catalog)
    }
}

# --- managers layer (unit) -----------------------------------------------------
$mgrEv = Join-Path $BaselineDir 'evidence-Managers.json'
if (Test-Path $mgrEv) {
    $m = Get-Content $mgrEv -Raw | ConvertFrom-Json
    $catalog = Join-Path $CatalogDir 'Managers.yaml'
    $covered = 0
    if (Test-Path $catalog) {
        $cited = Get-CitedKeys $catalog
        foreach ($mgr in $m.managers) { foreach ($meth in $mgr.methods) {
            if ($cited.ContainsKey(("{0}:{1}" -f $mgr.file.Replace('/','\'), $meth.line))) { $covered++ }
        } }
    }
    $rows += [pscustomobject]@{
        area='Managers'; layer='unit'; total=$m.anchorTotal; covered=$covered
        uncovered=($m.anchorTotal-$covered); pct=$(if($m.anchorTotal){[math]::Round(100.0*$covered/$m.anchorTotal,1)}else{100})
        catalog=(Test-Path $catalog)
    }
}

$grandTotal = ($rows | Measure-Object total -Sum).Sum
$grandCov   = ($rows | Measure-Object covered -Sum).Sum
$grandPct   = if ($grandTotal) { [math]::Round(100.0*$grandCov/$grandTotal,1) } else { 0 }

$ledger = [pscustomobject]@{
    grandTotal=$grandTotal; grandCovered=$grandCov; grandUncovered=($grandTotal-$grandCov); grandPct=$grandPct
    complete=($grandTotal -eq $grandCov)
    areas=($rows | Sort-Object uncovered -Descending)
}
$ledger | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ReportsDir 'ledger.json') -Encoding utf8

Write-Host ""
Write-Host "COMPLETENESS LEDGER" -ForegroundColor Cyan
Write-Host ("{0,-14} {1,5} {2,7} {3,8} {4,9} {5,7}  {6}" -f 'AREA','LAYER','TOTAL','COVERED','UNCOVERED','PCT','CATALOG')
Write-Host ("-"*70)
foreach ($r in ($rows | Sort-Object uncovered -Descending)) {
    Write-Host ("{0,-14} {1,5} {2,7} {3,8} {4,9} {5,6}%  {6}" -f `
        $r.area,$r.layer,$r.total,$r.covered,$r.uncovered,$r.pct,$(if($r.catalog){'yes'}else{'-'})) `
        -ForegroundColor $(if($r.uncovered -eq 0){'Green'}elseif($r.covered -gt 0){'Yellow'}else{'Gray'})
}
Write-Host ("-"*70)
Write-Host ("{0,-14} {1,5} {2,7} {3,8} {4,9} {5,6}%" -f 'TOTAL','',$grandTotal,$grandCov,($grandTotal-$grandCov),$grandPct) -ForegroundColor Cyan
Write-Host ""
Write-Host ("Complete: {0}  ({1}/{2} anchors, {3}% )" -f $(if($ledger.complete){'YES'}else{'NO'}),$grandCov,$grandTotal,$grandPct)
Write-Host "Report: $(Join-Path $ReportsDir 'ledger.json')"
if (-not $ledger.complete) { exit 1 } else { exit 0 }
