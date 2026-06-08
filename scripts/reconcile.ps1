<#
.SYNOPSIS
  Deterministic Ranorex coverage reconciler. No AI. Parses the existing Ranorex
  project into a coverage baseline that grounds the discovery agent.

.DESCRIPTION
  Walks the Ranorex project and emits:
    - recordings.json : every .rxrec (name, feature folder, path, variables) -> the
                        reusable "vocabulary" the AI composes new tests from.
    - suites.json     : every .rxtst and the test modules / test cases it contains.
    - baseline.json   : app feature folders vs. Ranorex coverage, with uncovered gaps.

  These feed the discovery prompt so the AI reconciles against what already exists
  instead of regenerating the 135 recordings QA already built.

.NOTES
  Read-only. Safe to run anytime. Output goes to qa-ai\catalog\_baseline\.
#>
[CmdletBinding()]
param(
    [string]$AppRoot     = (Join-Path $PSScriptRoot '..\..\2.0.x'),
    [string]$RanorexRoot = (Join-Path $PSScriptRoot '..\..\MyFirstExatouchTest\MyFirstExatouchTest'),
    [string]$OutDir      = (Join-Path $PSScriptRoot '..\catalog\_baseline')
)

$ErrorActionPreference = 'Stop'
$AppRoot     = (Resolve-Path $AppRoot).Path
$RanorexRoot = (Resolve-Path $RanorexRoot).Path
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$OutDir = (Resolve-Path $OutDir).Path

function Get-FeatureFolder([string]$fullPath, [string]$root) {
    # First path segment under the Ranorex root = the feature grouping.
    $rel = $fullPath.Substring($root.Length).TrimStart('\','/')
    $parts = $rel -split '[\\/]'
    if ($parts.Count -gt 1) { return $parts[0] } else { return '(root)' }
}

# --- 1. Recordings: the reusable building blocks --------------------------------
Write-Host 'Parsing recordings (.rxrec)...'
$recordings = @()
foreach ($f in Get-ChildItem -Path $RanorexRoot -Recurse -Filter '*.rxrec' -File) {
    $vars = @()
    try {
        [xml]$doc = Get-Content -LiteralPath $f.FullName -Raw
        foreach ($v in $doc.SelectNodes('//variables/var')) {
            if ($v.name) { $vars += $v.name }
        }
    } catch { Write-Warning "Could not parse $($f.Name): $($_.Exception.Message)" }

    $recordings += [pscustomobject]@{
        name      = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        feature   = Get-FeatureFolder $f.FullName $RanorexRoot
        path      = $f.FullName.Substring($RanorexRoot.Length).TrimStart('\','/')
        variables = $vars
    }
}

# --- 2. Suites: what is wired up to actually run --------------------------------
Write-Host 'Parsing test suites (.rxtst)...'
$suites = @()
foreach ($f in Get-ChildItem -Path $RanorexRoot -Recurse -Filter '*.rxtst' -File) {
    $modules = @(); $cases = @()
    try {
        [xml]$doc = Get-Content -LiteralPath $f.FullName -Raw
        foreach ($m in $doc.SelectNodes('//testmodule')) { if ($m.name) { $modules += $m.name } }
        foreach ($c in $doc.SelectNodes('//testcase'))   { if ($c.name) { $cases   += $c.name } }
    } catch { Write-Warning "Could not parse $($f.Name): $($_.Exception.Message)" }

    $suites += [pscustomobject]@{
        suite       = [IO.Path]::GetFileNameWithoutExtension($f.Name)
        path        = $f.FullName.Substring($RanorexRoot.Length).TrimStart('\','/')
        moduleCount = $modules.Count
        modules     = ($modules | Select-Object -Unique)
        testCases   = ($cases | Select-Object -Unique)
    }
}

# --- 3. Baseline: app features vs. coverage -------------------------------------
Write-Host 'Mapping app feature folders vs. coverage...'
$skip = @('obj','bin','.vs','.claude','.git','Properties','packages','Resources','Graphics','Help',
          'Service References','ConfigProfiles','SharedScripts','Shared','Models','Exceptions','Controls','Library')
$appFeatures = Get-ChildItem -Path $AppRoot -Directory |
    Where-Object { $skip -notcontains $_.Name } |
    Select-Object -ExpandProperty Name

$recByFeature = $recordings | Group-Object feature | ForEach-Object { @{ $_.Name = $_.Count } }
$coveredFeatures = ($recordings | Select-Object -ExpandProperty feature -Unique)

$baseline = [pscustomobject]@{
    appRoot            = $AppRoot
    ranorexRoot        = $RanorexRoot
    appFeatureFolders  = $appFeatures
    ranorexFeatures    = $coveredFeatures
    totals = [pscustomobject]@{
        recordings = $recordings.Count
        suites     = $suites.Count
        features   = $appFeatures.Count
    }
    recordingsByFeature = ($recordings | Group-Object feature |
        Sort-Object Count -Descending |
        ForEach-Object { [pscustomobject]@{ feature = $_.Name; recordings = $_.Count } })
    note = 'Mapping app folder -> ranorex feature is fuzzy by design; the discovery agent confirms it semantically.'
}

# --- write outputs --------------------------------------------------------------
$recordings | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir 'recordings.json') -Encoding utf8
$suites     | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir 'suites.json')     -Encoding utf8
$baseline   | ConvertTo-Json -Depth 6 | Set-Content -Path (Join-Path $OutDir 'baseline.json')   -Encoding utf8

Write-Host ''
Write-Host "Recordings : $($recordings.Count)"
Write-Host "Suites     : $($suites.Count)"
Write-Host "App features: $($appFeatures.Count)"
Write-Host "Output     : $OutDir"
