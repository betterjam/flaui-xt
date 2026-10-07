<#
.SYNOPSIS
  Directory strategy: explode a flat Area catalog into a per-test folder tree. No AI.

.DESCRIPTION
  The agent writes one flat catalog/features/<Area>.yaml (simple for it to author + verify).
  This script materializes the browsable on-disk strategy:

      catalog/<Area>/<form>/<TestId>.yaml      (one file per test)

  - <form> comes from the case's ui_area (e.g. frmVendors); falls back to '_general'.
  - one YAML file per test case, named by its stable id (PUR-002.yaml).
  - the flat features/<Area>.yaml is preserved as the source of truth the agent + the
    deterministic gates (validate/coverage/impact) operate on; the tree is the human view.

  Idempotent: rewrites the <Area> subtree from the current flat file each run, so it always
  matches. Safe to run repeatedly (the runner calls it after each area completes).

.OUTPUTS
  catalog/<Area>/<form>/<id>.yaml files.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Area,
    [string]$QaRoot = '',
    [string]$ScriptDirOverride = ''
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($ScriptDirOverride) { $ScriptDirOverride } elseif ($PSScriptRoot) { $PSScriptRoot } else { 'C:\Dev\exatouch-qa\scripts' }
if (-not $QaRoot) { $QaRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path }

$flat = Join-Path $QaRoot ("catalog\features\{0}.yaml" -f $Area)
if (-not (Test-Path $flat)) { Write-Error "No flat catalog for '$Area' at $flat"; exit 2 }
$areaDir = Join-Path $QaRoot ("catalog\{0}" -f $Area)

# wipe and rebuild the area subtree so it always mirrors the flat file exactly
if (Test-Path $areaDir) { Remove-Item $areaDir -Recurse -Force }
New-Item -ItemType Directory -Path $areaDir | Out-Null

# split the flat YAML into per-case blocks. A case starts at a line matching '  - id:'.
$lines = Get-Content $flat
$header = @()       # feature:/description:/source_paths:/flavors: preamble (before test_cases:)
$inCases = $false
$blocks = @()       # each: @{ id; uiArea; text[] }
$cur = $null

foreach ($ln in $lines) {
    if (-not $inCases) {
        if ($ln -match '^\s*test_cases:\s*$') { $inCases = $true; continue }
        $header += $ln; continue
    }
    if ($ln -match '^\s*-\s*id:\s*([A-Za-z0-9_-]+)') {
        if ($cur) { $blocks += $cur }
        $cur = @{ id = $Matches[1]; uiArea = '_general'; text = @($ln) }
    } elseif ($cur) {
        $cur.text += $ln
        if ($ln -match '^\s*ui_area:\s*([\w]+)') { $cur.uiArea = $Matches[1] }
    }
}
if ($cur) { $blocks += $cur }

$count = 0
foreach ($b in $blocks) {
    $formDir = Join-Path $areaDir $b.uiArea
    if (-not (Test-Path $formDir)) { New-Item -ItemType Directory -Path $formDir | Out-Null }
    $out = Join-Path $formDir ("{0}.yaml" -f $b.id)
    # each per-test file carries the area header for context + the single case (re-indented to top level)
    $body = New-Object System.Collections.Generic.List[string]
    $body.Add("# Generated from catalog/features/$Area.yaml by split.ps1 - do not hand-edit; edit the flat file.")
    $body.Add("feature: $Area")
    $body.Add("test_case:")
    foreach ($t in $b.text) { $body.Add($t) }
    Set-Content -Path $out -Value $body -Encoding utf8
    $count++
}

$forms = ($blocks | ForEach-Object { $_.uiArea } | Sort-Object -Unique).Count
Write-Host "Split '$Area' -> $count test files across $forms form folder(s) under catalog\$Area\"
