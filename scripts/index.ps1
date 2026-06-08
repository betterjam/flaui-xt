<#
.SYNOPSIS
  Generate INDEX.md — a human-readable manual of areas, coverage, and tests. No AI.

.DESCRIPTION
  The browsable "what tests do we have" document. Rolls up the ledger + every catalog YAML
  into one Markdown file: overall coverage, a per-area table, and per-area test listings
  grouped by category (test_kind / ui_area / entity) with id + title + priority + binding.
  Regenerated after each area completes so it always reflects current state.

.OUTPUTS
  qa-ai\INDEX.md
#>
[CmdletBinding()]
param(
    [string]$QaRoot = '',
    [string]$ScriptDirOverride = ''
)
$ErrorActionPreference = 'Stop'
$scriptDir = if ($ScriptDirOverride) { $ScriptDirOverride } elseif ($PSScriptRoot) { $PSScriptRoot } else { 'C:\Dev\Exatouch\qa-ai\scripts' }
if (-not $QaRoot) { $QaRoot = (Resolve-Path (Join-Path $scriptDir '..')).Path }
$catalogDir = Join-Path $QaRoot 'catalog\features'
$ledgerFile = Join-Path $QaRoot 'reports\ledger.json'
$outFile    = Join-Path $QaRoot 'INDEX.md'

# light per-case YAML reader (flat shape per SCHEMA)
function Read-Cases([string]$file) {
    $cases=@(); $c=$null
    foreach ($ln in Get-Content $file) {
        if ($ln -match '^\s*-?\s*id:\s*([A-Za-z0-9_-]+)') { if($c){$cases+=$c}; $c=[ordered]@{id=$Matches[1];title='';kind='';priority='';test_kind='';ui_area='';entity='';binding=''} }
        elseif ($c) {
            if ($ln -match '^\s*title:\s*(.+?)\s*$')      { $c.title=$Matches[1].Trim('"',"'") }
            elseif ($ln -match '^\s*kind:\s*(\w+)')        { $c.kind=$Matches[1] }
            elseif ($ln -match '^\s*priority:\s*(\w+)')    { $c.priority=$Matches[1] }
            elseif ($ln -match '^\s*test_kind:\s*([\w-]+)'){ $c.test_kind=$Matches[1] }
            elseif ($ln -match '^\s*ui_area:\s*([\w]+)')   { $c.ui_area=$Matches[1] }
            elseif ($ln -match '^\s*entity:\s*([\w]+)')    { $c.entity=$Matches[1] }
            elseif ($ln -match '^\s*binding:\s*([\w-]+)')  { $c.binding=$Matches[1] }
        }
    }
    if ($c){$cases+=$c}
    return ,$cases
}

$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine("# Exatouch Test Catalog - Coverage Manual")
$null = $sb.AppendLine("")
$null = $sb.AppendLine("_Auto-generated index of every area we cover with tests. Do not edit by hand; run scripts/index.ps1._")
$null = $sb.AppendLine("")

# --- overall + per-area table from the ledger ----------------------------------
if (Test-Path $ledgerFile) {
    $l = Get-Content $ledgerFile -Raw | ConvertFrom-Json
    $null = $sb.AppendLine("## Overall")
    $null = $sb.AppendLine("")
    $doneFlag = if($l.complete){'YES'}else{'NO'}
    $null = $sb.AppendLine("**$($l.grandPct)% complete** - $($l.grandCovered) / $($l.grandTotal) testable code anchors covered. Complete: **$doneFlag**.")
    $null = $sb.AppendLine("")
    $null = $sb.AppendLine("| Area | Layer | Covered | Total | % | Catalog |")
    $null = $sb.AppendLine("|------|-------|--------:|------:|--:|:-------:|")
    foreach ($a in ($l.areas | Sort-Object pct -Descending)) {
        $cat = if($a.catalog){'yes'}else{'-'}
        $null = $sb.AppendLine("| $($a.area) | $($a.layer) | $($a.covered) | $($a.total) | $($a.pct)% | $cat |")
    }
    $null = $sb.AppendLine("")
}

# --- per-area test listings -----------------------------------------------------
$null = $sb.AppendLine("## Tests by area")
$null = $sb.AppendLine("")
$files = Get-ChildItem $catalogDir -Filter '*.yaml' -File -EA SilentlyContinue | Sort-Object Name
if (-not $files) { $null = $sb.AppendLine("_No catalog files yet._") }
foreach ($f in $files) {
    $cases = Read-Cases $f.FullName
    $area = [IO.Path]::GetFileNameWithoutExtension($f.Name)
    $atomic = ($cases | Where-Object { $_.kind -eq 'atomic' }).Count
    $comp   = ($cases | Where-Object { $_.kind -eq 'composite' }).Count
    $null = $sb.AppendLine("### $area  ($($cases.Count) tests: $atomic atomic, $comp composite)")
    $null = $sb.AppendLine("")
    # group by test_kind for readability
    foreach ($grp in ($cases | Group-Object test_kind | Sort-Object Name)) {
        $kindName = if ($grp.Name) { $grp.Name } else { '(uncategorized)' }
        $null = $sb.AppendLine("**$kindName** ($($grp.Count))")
        $null = $sb.AppendLine("")
        foreach ($c in ($grp.Group | Sort-Object id)) {
            $tags = (@($c.priority, $c.ui_area, $c.entity, $c.binding) | Where-Object { $_ }) -join ' / '
            $null = $sb.AppendLine("- ``$($c.id)`` $($c.title)  _( $tags )_")
        }
        $null = $sb.AppendLine("")
    }
}

Set-Content -Path $outFile -Value $sb.ToString() -Encoding utf8
Write-Host "Wrote $outFile  ($($files.Count) area catalog(s))"
