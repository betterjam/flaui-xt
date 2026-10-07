<#
.SYNOPSIS
  Master app map: every feature area -> its forms -> anchor counts. No AI.

.DESCRIPTION
  The "cover all options" backlog. Walks the app, groups forms by top-level feature folder,
  runs the same anchor detection as extract-evidence.ps1 (message/action/toggle/permission/
  calc) per form, and rolls up totals per area. This is the scoreboard for whole-app
  coverage: it shows how many testable anchors exist in each area and how big the discovery
  job is, so nothing (admin, management, taxes, reports, ...) is silently skipped.

.OUTPUTS
  qa-ai\catalog\_baseline\app-map.json  + console table sorted by anchor count.
#>
[CmdletBinding()]
param(
    [string]$AppRoot = (Resolve-Path (Join-Path (&{ if ($env:EXATOUCH_ROOT) { $env:EXATOUCH_ROOT } else { 'C:\Dev\Exatouch' } }) '2.0.x')).Path,
    [string]$OutDir  = (Join-Path $PSScriptRoot '..\catalog\_baseline')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$repoRoot = Split-Path $AppRoot -Parent

# feature areas = top-level folders that hold UI forms (skip pure infra)
$skip = @('obj','bin','.vs','.claude','.git','Properties','packages','Resources','Graphics',
          'Help','Service References','ConfigProfiles','SharedScripts','Shared','Models',
          'Exceptions','Controls','Library','Data')

function Count-Anchors([string]$file) {
    $c = @{ message=0; action=0; toggle=0; permission=0; calc=0 }
    foreach ($ln in (Get-Content $file)) {
        if ($ln -match 'Lib\.(Warning|Success|Error|Dialog)\s*\(\s*(?:[^,"]*,\s*)?[\$@]*"[^"]+"') { $c.message++; continue }
        if ($ln -match '(?:void|public)\s+(?:btn|mnu|lnk|tab|dgv|grd|tsm|tsb)\w*(?:Click|DoubleClick|CellClick|CellContentClick)\s*\(' -or $ln -match '\w*OnSave\s*\(') { $c.action++; continue }
        if ($ln -match '(?:void|public)\s+(?:chk|cbo|rdo|opt)\w*(?:CheckedChanged|SelectedIndexChanged|Changed)\s*\(') { $c.toggle++; continue }
        if ($ln -match '\w+\.Access\s*=\s*Global\.StaffSecurity\.') { $c.permission++; continue }
        if ($ln -match '\b(Tax|Total|Discount|Cost|Markup|Price|Subtotal|Gratuity|Tip)\b\s*=' -and $ln -notmatch '//' -and $ln -match '[*/+\-]') { $c.calc++ }
    }
    return $c
}

$areas = @()
foreach ($dir in (Get-ChildItem $AppRoot -Directory | Where-Object { $skip -notcontains $_.Name })) {
    $forms = Get-ChildItem $dir.FullName -Recurse -Filter 'frm*.cs' -File |
             Where-Object { $_.Name -notlike '*.Designer.cs' -and $_.Name -notlike '*.designer.cs' }
    if (-not $forms) { continue }
    $tot = @{ message=0; action=0; toggle=0; permission=0; calc=0 }
    $formList = @()
    foreach ($f in $forms) {
        $c = Count-Anchors $f.FullName
        foreach ($k in $tot.Keys.Clone()) { $tot[$k] += $c[$k] }
        $sum = ($c.Values | Measure-Object -Sum).Sum
        $formList += [pscustomobject]@{ form=$f.BaseName; anchors=$sum; byType=$c }
    }
    $areaTotal = ($tot.Values | Measure-Object -Sum).Sum
    $areas += [pscustomobject]@{
        area = $dir.Name
        forms = $forms.Count
        anchorTotal = $areaTotal
        byType = $tot
        formsDetail = ($formList | Sort-Object anchors -Descending)
    }
}
$areas = $areas | Sort-Object anchorTotal -Descending

$map = [pscustomobject]@{
    totals = [pscustomobject]@{
        areas = $areas.Count
        forms = ($areas | Measure-Object -Property forms -Sum).Sum
        anchors = ($areas | Measure-Object -Property anchorTotal -Sum).Sum
    }
    areas = $areas
}
$mapFile = Join-Path $OutDir 'app-map.json'
$map | ConvertTo-Json -Depth 8 | Set-Content $mapFile -Encoding utf8

Write-Host ""
Write-Host "APP MAP - whole-app discovery backlog" -ForegroundColor Cyan
Write-Host ("{0,-14} {1,6} {2,8}   msg/act/tog/perm/calc" -f 'AREA','FORMS','ANCHORS')
Write-Host ("-" * 64)
foreach ($a in $areas) {
    Write-Host ("{0,-14} {1,6} {2,8}   {3}/{4}/{5}/{6}/{7}" -f `
        $a.area, $a.forms, $a.anchorTotal, $a.byType.message, $a.byType.action, $a.byType.toggle, $a.byType.permission, $a.byType.calc)
}
Write-Host ("-" * 64)
Write-Host ("{0,-14} {1,6} {2,8}" -f 'TOTAL', $map.totals.forms, $map.totals.anchors)
Write-Host ""
$mapPath = Join-Path $OutDir 'app-map.json'
Write-Host "Map: $mapPath"
