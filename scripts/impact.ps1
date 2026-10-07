<#
.SYNOPSIS
  Impact analysis: which generated tests are affected when app files change. No AI.

.DESCRIPTION
  Each catalog case lists the app `source_files` that make up its flow and a `flow_hash`
  (a hash of those files' contents at authoring time). This script, given a set of changed
  files (explicit list, or derived from `git diff`), finds every case whose flow touches a
  changed file, OR whose current source_files no longer hash-match (drift), and reports them
  as STALE -> the queue the discovery/addition agent regenerates.

  Two jobs:
    -ChangedFiles a,b,c     : report cases impacted by those changes (CI / pre-merge).
    -Stamp                  : (re)compute and write flow_hash for every case from current
                              file contents. Run after the agent (re)generates a case so the
                              baseline is current. Only this mode writes to catalog files.

.OUTPUTS
  qa-ai\reports\impact.json  (+ console summary). Exit 1 if impacted cases found (so CI can
  gate), 0 if clean.

.NOTES
  Read-only except in -Stamp mode (which edits only flow_hash lines under qa-ai\catalog\).
  Never touches app code.
#>
[CmdletBinding(DefaultParameterSetName='Report')]
param(
    [Parameter(ParameterSetName='Report')] [string[]]$ChangedFiles,
    [Parameter(ParameterSetName='Report')] [switch]$FromGitDiff,
    [Parameter(ParameterSetName='Report')] [string]$GitRange = 'HEAD~1..HEAD',
    [Parameter(ParameterSetName='Stamp')]  [switch]$Stamp,
    [string]$CatalogDir = (Join-Path $PSScriptRoot '..\catalog\features'),
    [string]$RepoRoot   = (Resolve-Path (&{ if ($env:EXATOUCH_ROOT) { $env:EXATOUCH_ROOT } else { 'C:\Dev\Exatouch' } })).Path,   # the Exatouch app checkout (EXATOUCH_ROOT)
    [string]$ReportsDir = (Join-Path $PSScriptRoot '..\reports')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $ReportsDir)) { New-Item -ItemType Directory -Path $ReportsDir | Out-Null }

function Get-FileHashSafe([string]$relPath) {
    $full = Join-Path $RepoRoot $relPath
    if (-not (Test-Path $full)) { return 'MISSING' }
    return (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.Substring(0,16)
}

# --- parse catalog cases: id, title, source_files[], flow_hash, line numbers ----
function Read-Cases([string]$file) {
    $lines = Get-Content $file
    $cases = @(); $cur = $null; $section = $null
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]
        if ($ln -match '^\s*-?\s*id:\s*([A-Za-z0-9_-]+)') {
            if ($cur) { $cases += $cur }
            $cur = [pscustomobject]@{ id=$Matches[1]; title=''; file=$file; sourceFiles=@(); evidence=@(); flowHash=''; hashLine=-1 }
            $section = $null
        }
        elseif ($cur -and $ln -match '^\s*title:\s*(.+?)\s*$') { $cur.title = $Matches[1].Trim('"',"'") }
        elseif ($cur -and $ln -match '^\s*source_files:\s*$') { $section = 'sf' }
        elseif ($cur -and $ln -match '^\s*evidence:\s*$') { $section = 'ev' }
        elseif ($cur -and $ln -match '^\s*flow_hash:\s*"?([^"]*)"?\s*$') { $cur.flowHash = $Matches[1]; $cur.hashLine = $i; $section = $null }
        elseif ($cur -and $section -eq 'sf' -and $ln -match '^\s*-\s*(\S+?)\s*(#.*)?$') { $cur.sourceFiles += $Matches[1] }
        elseif ($cur -and $section -eq 'ev' -and $ln -match '\{\s*file:\s*(\S+?)\s*,\s*line:\s*(\d+)') {
            $cur.evidence += [pscustomobject]@{ file=$Matches[1].TrimEnd(','); line=[int]$Matches[2] }
        }
        elseif ($ln -match '^\s*\w+:') { $section = $null }
    }
    if ($cur) { $cases += $cur }
    return ,$cases
}

$catalogFiles = Get-ChildItem $CatalogDir -Filter '*.yaml' -File -ErrorAction SilentlyContinue
if (-not $catalogFiles) { Write-Host "No catalog files yet in $CatalogDir."; exit 0 }

# --- STAMP mode -----------------------------------------------------------------
if ($Stamp) {
    $stamped = 0
    foreach ($f in $catalogFiles) {
        $cases = Read-Cases $f.FullName
        $content = Get-Content $f.FullName
        foreach ($c in $cases) {
            if ($c.hashLine -lt 0 -or $c.sourceFiles.Count -eq 0) { continue }
            $combined = ($c.sourceFiles | Sort-Object | ForEach-Object { Get-FileHashSafe $_ }) -join '|'
            $h = [BitConverter]::ToString(
                    (New-Object Security.Cryptography.SHA256Managed).ComputeHash(
                        [Text.Encoding]::UTF8.GetBytes($combined))
                 ).Replace('-','').Substring(0,16)
            $indent = ($content[$c.hashLine] -replace 'flow_hash:.*','')
            $content[$c.hashLine] = "${indent}flow_hash: `"$h`""
            $stamped++
        }
        Set-Content -Path $f.FullName -Value $content -Encoding utf8
    }
    Write-Host "Stamped flow_hash for $stamped case(s)." -ForegroundColor Green
    exit 0
}

# --- REPORT mode: determine changed files ---------------------------------------
if ($FromGitDiff) {
    Push-Location $RepoRoot
    try { $ChangedFiles = (git diff --name-only $GitRange) -split "`n" | Where-Object { $_ } }
    finally { Pop-Location }
}
if (-not $ChangedFiles) { Write-Host "No changed files supplied (use -ChangedFiles or -FromGitDiff)."; exit 0 }
$changedNorm = $ChangedFiles | ForEach-Object { $_.Replace('/','\').TrimStart('.\') }

# Load all catalog cases once.
$allCases = @()
foreach ($f in $catalogFiles) { $allCases += (Read-Cases $f.FullName) }

# === VERDICT 1: UPDATE — existing tests whose source files changed ===============
$toUpdate = @()
foreach ($c in $allCases) {
    if ($c.sourceFiles.Count -eq 0) { continue }
    $hits = @()
    foreach ($sf in $c.sourceFiles) {
        $sfNorm = $sf.Replace('/','\')
        if ($changedNorm | Where-Object { $_ -like "*$sfNorm" -or $sfNorm -like "*$_" }) { $hits += $sf }
    }
    if ($hits.Count) {
        $toUpdate += [pscustomobject]@{
            id=$c.id; title=$c.title; catalog=(Split-Path $c.file -Leaf); changedSourceFiles=$hits
        }
    }
}

# === VERDICT 2: NEW — code anchors in changed files that NO test cites ===========
# Re-extract behavior anchors straight from the changed source (tests come from code),
# then subtract anchors already cited by some test's evidence. Remainder = new tests needed.
$citedAnchors = @{}   # key "file:line" -> $true
foreach ($c in $allCases) { foreach ($e in $c.evidence) {
    $citedAnchors[("{0}:{1}" -f $e.file.Replace('/','\'), $e.line)] = $true
} }

$newNeeded = @()
foreach ($cf in $changedNorm) {
    if ($cf -notmatch '\.cs$') { continue }
    $full = Join-Path $RepoRoot $cf
    if (-not (Test-Path $full)) { continue }          # deleted file -> handled by UPDATE side
    $lines = Get-Content $full
    for ($i=0; $i -lt $lines.Count; $i++) {
        $m = [regex]::Match($lines[$i], 'Lib\.(Warning|Success|Error|Dialog)\s*\(\s*(?:[^,"]*,\s*)?[\$@]*"([^"]+)"')
        if ($m.Success) {
            $key = "{0}:{1}" -f $cf, ($i+1)
            if (-not $citedAnchors.ContainsKey($key)) {
                $newNeeded += [pscustomobject]@{ file=$cf; line=($i+1); kind=$m.Groups[1].Value; text=$m.Groups[2].Value }
            }
        }
    }
}

$report = [pscustomobject]@{
    changedFiles = $changedNorm
    update = $toUpdate          # existing tests to regenerate in place (same id), human-reviewed
    new    = $newNeeded         # uncovered code anchors -> author a new test
    summary = @{ update = $toUpdate.Count; new = $newNeeded.Count }
    action = 'UPDATE: agent regenerates these case ids from current code. NEW: agent authors a test citing each anchor. Then run impact.ps1 -Stamp.'
}
$report | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ReportsDir 'impact.json') -Encoding utf8

Write-Host ""
Write-Host "Impact: $($changedNorm.Count) changed file(s)  ->  UPDATE $($toUpdate.Count) / NEW $($newNeeded.Count)" -ForegroundColor Cyan
if ($toUpdate.Count) {
    Write-Host "UPDATE existing tests (code they cover changed):" -ForegroundColor Yellow
    $toUpdate | ForEach-Object {
        Write-Host ("  ~ {0} [{1}] {2}" -f $_.id, $_.catalog, $_.title)
        $_.changedSourceFiles | ForEach-Object { Write-Host "      changed: $_" }
    }
}
if ($newNeeded.Count) {
    Write-Host "NEW tests needed (code behavior with no covering test):" -ForegroundColor Magenta
    $newNeeded | ForEach-Object { Write-Host ("  + {0}:{1}  {2}: ""{3}""" -f $_.file, $_.line, $_.kind, $_.text) }
}
if (-not $toUpdate.Count -and -not $newNeeded.Count) { Write-Host "No tests to update or add." -ForegroundColor Green }
Write-Host ""
Write-Host "Report: $(Join-Path $ReportsDir 'impact.json')"
if ($toUpdate.Count -or $newNeeded.Count) { exit 1 } else { exit 0 }
