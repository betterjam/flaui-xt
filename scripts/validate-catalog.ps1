<#
.SYNOPSIS
  Deterministic anti-hallucination validator for AI-generated catalog YAML. No AI.

.DESCRIPTION
  The AI can hallucinate: fabricated recording names, false "covered" claims, locator
  references that don't exist, unstable IDs. This script mechanically rejects those by
  checking each feature catalog against the parsed Ranorex inventory (_baseline) and the
  app source. Exit code 0 = clean, 1 = violations found (fail the pipeline / agent re-runs).

  Checks:
    H1  Every reference_recordings name exists in _baseline/recordings.json.
    H2  Every composes name exists as a recording OR a [UserCodeMethod] in Code Modules.
    H3  matches_existing (when not null) names a real test case in _baseline/suites.json.
    H4  binding=locators-known requires a non-empty reference_recordings list.
    H5  No fabricated locators: catalog YAML must not contain loc="x;y" or ranoreXPath.
    H6  IDs are unique and well-formed (FEATURE-NNN); none duplicated across files.
    H7  source_paths point at real folders under the app root.

.NOTES
  Uses a light YAML reader (the catalog is flat/simple by SCHEMA design). Read-only.
#>
[CmdletBinding()]
param(
    [string]$Feature,                                   # optional: validate one feature file
    [string]$CatalogDir  = (Join-Path $PSScriptRoot '..\catalog\features'),
    [string]$BaselineDir = (Join-Path $PSScriptRoot '..\catalog\_baseline'),
    [string]$AppRoot     = (Join-Path $PSScriptRoot '..\..\2.0.x'),
    [string]$ModulesDir  = (Join-Path $PSScriptRoot '..\..\MyFirstExatouchTest\MyFirstExatouchTest\Code Modules')
)

$ErrorActionPreference = 'Stop'
$violations = New-Object System.Collections.Generic.List[string]
function Fail($id, $file, $msg) { $violations.Add(("[{0}] {1}: {2}" -f $id, (Split-Path $file -Leaf), $msg)) }

# --- load ground truth ----------------------------------------------------------
if (-not (Test-Path (Join-Path $BaselineDir 'recordings.json'))) {
    Write-Error "Baseline missing. Run reconcile.ps1 first."; exit 2
}
$recNames  = (Get-Content (Join-Path $BaselineDir 'recordings.json') -Raw | ConvertFrom-Json |
              ForEach-Object { $_.name }) | Sort-Object -Unique
$suiteCases = @()
(Get-Content (Join-Path $BaselineDir 'suites.json') -Raw | ConvertFrom-Json) |
    ForEach-Object { $suiteCases += $_.testCases; $suiteCases += $_.modules }
$suiteCases = $suiteCases | Where-Object { $_ } | Sort-Object -Unique

# [UserCodeMethod] names from Code Modules
$userCodeMethods = @()
if (Test-Path $ModulesDir) {
    foreach ($cs in Get-ChildItem $ModulesDir -Recurse -Filter '*.cs' -File) {
        $txt = Get-Content $cs.FullName -Raw
        foreach ($m in [regex]::Matches($txt, 'public\s+static\s+\w+\s+(\w+)\s*\(')) {
            $userCodeMethods += $m.Groups[1].Value
        }
    }
}
$reusable = ($recNames + $userCodeMethods) | Sort-Object -Unique

# --- pick files -----------------------------------------------------------------
$files = if ($Feature) {
    @(Get-ChildItem $CatalogDir -Filter "$Feature.yaml" -File -ErrorAction SilentlyContinue)
} else {
    @(Get-ChildItem $CatalogDir -Filter '*.yaml' -File -ErrorAction SilentlyContinue)
}
if (-not $files) { Write-Host "No catalog files to validate in $CatalogDir."; exit 0 }

$allIds = @{}

foreach ($file in $files) {
    $raw   = Get-Content $file.FullName -Raw
    $lines = Get-Content $file.FullName

    # H5: fabricated locators anywhere in the file
    if ($raw -match 'loc\s*=\s*"\d+;\d+"' -or $raw -match 'ranoreXPath' -or $raw -match 'searchpropertyid') {
        Fail 'H5' $file.FullName 'contains a fabricated Ranorex locator (loc="x;y"/RanoreXPath/searchpropertyid). Locators must come from a record pass, not the AI.'
    }

    # crude per-field scan (SCHEMA keeps YAML flat: "    key: value" and "      - item")
    $currentId = $null; $currentBinding = $null; $refCountForCase = @{}
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]

        if ($ln -match '^\s*-?\s*id:\s*([A-Za-z0-9_-]+)') {
            $currentId = ($Matches[1] -replace '\s+#.*$','').Trim()
            # H6: ID format + uniqueness
            if ($currentId -notmatch '^[A-Z][A-Z0-9]*-\d{3,}$') { Fail 'H6' $file.FullName "ID '$currentId' is malformed (want FEATURE-NNN)." }
            if ($allIds.ContainsKey($currentId)) { Fail 'H6' $file.FullName "duplicate ID '$currentId' (also in $($allIds[$currentId]))." }
            else { $allIds[$currentId] = $file.Name }
            $refCountForCase[$currentId] = 0
        }
        if ($ln -match '^\s*binding:\s*([a-z-]+)') { $currentBinding = $Matches[1]
            if ($currentBinding -eq 'locators-known' -and $currentId) {
                # checked after we count refs; remember binding
                $refCountForCase["$currentId::binding"] = 'locators-known'
            }
        }

        # list items under reference_recordings / composes / matches_existing
        if ($ln -match '^\s*reference_recordings:\s*(.*)$') { $section = 'ref' }
        elseif ($ln -match '^\s*composes:\s*(.*)$')         { $section = 'comp' }
        elseif ($ln -match '^\s*matches_existing:\s*(.+?)\s*$') {
            $val = ($Matches[1] -replace '\s+#.*$','').Trim().Trim('"',"'")   # strip trailing comment
            if ($val -and $val -ne 'null') {
                # H3
                if ($suiteCases -notcontains $val) { Fail 'H3' $file.FullName "case '$currentId' matches_existing '$val' which is not in any existing suite/recording." }
            }
            $section = $null
        }
        elseif ($ln -match '^\s*\w+:') { $section = $null }

        if ($ln -match '^\s*-\s*([A-Za-z0-9_]+)\s*$' -and $section) {
            $item = $Matches[1]
            if ($section -eq 'ref') {
                if ($recNames -notcontains $item) { Fail 'H1' $file.FullName "case '$currentId' reference_recordings '$item' does not exist in recordings.json." }
                if ($currentId) { $refCountForCase[$currentId]++ }
            }
            elseif ($section -eq 'comp') {
                if ($reusable -notcontains $item) { Fail 'H2' $file.FullName "case '$currentId' composes '$item' which is neither a recording nor a [UserCodeMethod]." }
            }
        }

        # H7: source_paths exist
        if ($ln -match '^\s*-\s*(2\.0\.x[\\/][^\s]+)\s*$') {
            $p = Join-Path (Split-Path $AppRoot -Parent) $Matches[1]
            if (-not (Test-Path $p)) { Fail 'H7' $file.FullName "source_path '$($Matches[1])' does not exist." }
        }
    }

    # H4: locators-known must have >=1 reference recording
    foreach ($k in ($refCountForCase.Keys | Where-Object { $_ -notlike '*::binding' })) {
        if ($refCountForCase["$k::binding"] -eq 'locators-known' -and $refCountForCase[$k] -lt 1) {
            Fail 'H4' $file.FullName "case '$k' is binding=locators-known but lists no reference_recordings."
        }
    }
}

# --- report ---------------------------------------------------------------------
if ($violations.Count -eq 0) {
    Write-Host "VALIDATION PASSED - $($files.Count) file(s), no hallucinations detected." -ForegroundColor Green
    exit 0
} else {
    Write-Host "VALIDATION FAILED - $($violations.Count) violation(s):" -ForegroundColor Red
    $violations | ForEach-Object { Write-Host "  $_" }
    exit 1
}
