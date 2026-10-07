<#
.SYNOPSIS
  Build index.html — a dashboard listing every FlaUI test and its latest report. No deps.

.DESCRIPTION
  Scans the FlaUI POC for [Test] methods (the tests that exist) and the TestResults folder
  for generated reports, then writes TestResults/index.html: a landing page showing each
  test, its status from the latest run, and a link to open its detailed report. Run this
  after a test run (or any time) to refresh the index. Self-contained HTML.
#>
[CmdletBinding()]
param(
    [string]$PocDir     = $PSScriptRoot,
    [string]$ResultsDir = (Join-Path $PSScriptRoot 'TestResults')
)
$ErrorActionPreference = 'Stop'
if (-not $PocDir) { $PocDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if (-not $ResultsDir) { $ResultsDir = Join-Path $PocDir 'TestResults' }
if (-not (Test-Path $ResultsDir)) { New-Item -ItemType Directory -Path $ResultsDir | Out-Null }

# --- discover every [Test] method in the POC (the tests that exist) -------------
$tests = @()
foreach ($cs in Get-ChildItem $PocDir -Filter '*.cs' -File) {
    $lines = Get-Content $cs.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^\s*\[Test\]') {
            # next non-attribute line with a method name
            for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Count); $j++) {
                $m = [regex]::Match($lines[$j], 'public\s+void\s+(\w+)\s*\(')
                if ($m.Success) {
                    $tests += [pscustomobject]@{
                        Method = $m.Groups[1].Value
                        Pretty = ($m.Groups[1].Value -replace '_', ' ')
                        File   = $cs.Name
                    }
                    break
                }
            }
        }
    }
}

# --- find the latest report + its status ----------------------------------------
$reports = Get-ChildItem $ResultsDir -Filter '*report*.html' -File -EA SilentlyContinue |
           Where-Object { $_.Name -ne 'index.html' } | Sort-Object LastWriteTime -Descending
$reportInfo = @()
foreach ($r in $reports) {
    $html = Get-Content $r.FullName -Raw
    # ensure the report's "Back to all tests" link points at the dashboard root '/'
    $changed = $false
    if ($html -notmatch "class='back'") {
        $backLink = "<a href='/' class='back' style='display:inline-block;margin:8px 32px;color:#38bdf8;text-decoration:none;font-size:14px'>&larr; Back to all tests</a>"
        $html = $html -replace '(<body>)', "`$1$backLink"; $changed = $true
    } elseif ($html -match "href='index\.html' class='back'") {
        # fix older reports that linked to the static index.html
        $html = $html -replace "href='index\.html' class='back'", "href='/' class='back'"; $changed = $true
    }
    if ($changed) { [IO.File]::WriteAllText($r.FullName, $html, [Text.Encoding]::UTF8) }
    # the badge text itself (>FAIL< / >PASS<) is unambiguous; CSS class names appear in <style> too
    $status = if ($html -match '>FAIL<') { 'FAIL' } elseif ($html -match '>PASS<') { 'PASS' } else { 'UNKNOWN' }
    $shots = ([regex]::Matches($html, 'data:image/png;base64')).Count
    $reportInfo += [pscustomobject]@{
        Name = $r.Name; Status = $status; Shots = $shots
        When = $r.LastWriteTime.ToString('yyyy-MM-dd HH:mm'); Size = [math]::Round($r.Length/1KB)
    }
}

# --- compose index.html ---------------------------------------------------------
$now = (Get-Date).ToString('yyyy-MM-dd HH:mm')
$total = $tests.Count
$pass = @($reportInfo | Where-Object { $_.Status -eq 'PASS' }).Count
$fail = @($reportInfo | Where-Object { $_.Status -eq 'FAIL' }).Count
if (-not $pass) { $pass = 0 }; if (-not $fail) { $fail = 0 }

$sb = New-Object System.Text.StringBuilder
[void]$sb.Append(@"
<!doctype html><html><head><meta charset='utf-8'><title>Exatouch UI Tests - Index</title>
<style>
 body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#0f172a;color:#e2e8f0}
 header{background:#1e293b;padding:24px 32px;border-bottom:3px solid #38bdf8}
 h1{margin:0;font-size:22px} .sub{color:#94a3b8;font-size:13px;margin-top:4px}
 .cards{display:flex;gap:16px;padding:24px 32px}
 .card{background:#1e293b;border-radius:10px;padding:16px 24px;min-width:90px;text-align:center}
 .card .n{font-size:28px;font-weight:700} .pass{color:#4ade80} .fail{color:#f87171}
 table{width:calc(100% - 64px);margin:0 32px 32px;border-collapse:collapse;background:#1e293b;border-radius:10px;overflow:hidden}
 th,td{text-align:left;padding:12px 18px;border-bottom:1px solid #334155;font-size:14px}
 th{background:#334155;color:#cbd5e1} tr:last-child td{border-bottom:none}
 a{color:#38bdf8;text-decoration:none} a:hover{text-decoration:underline}
 .badge{padding:2px 10px;border-radius:20px;font-size:12px}
 .b-pass{background:#166534;color:#bbf7d0} .b-fail{background:#991b1b;color:#fecaca} .b-unk{background:#475569;color:#e2e8f0}
 .file{color:#64748b;font-size:12px}
</style></head><body>
<header><h1>Exatouch UI Tests &mdash; Index</h1>
<div class='sub'>FlaUI (open source) + NUnit &middot; generated $now</div></header>
<div class='cards'>
 <div class='card'><div class='n'>$total</div>Tests</div>
 <div class='card'><div class='n pass'>$pass</div>Reports passed</div>
 <div class='card'><div class='n fail'>$fail</div>Reports failed</div>
</div>
<table><tr><th>Test</th><th>Source file</th></tr>
"@)

foreach ($t in ($tests | Sort-Object File, Method)) {
    [void]$sb.Append("<tr><td><strong>$($t.Pretty)</strong></td><td class='file'>$($t.File)</td></tr>")
}
[void]$sb.Append("</table>")

if ($reportInfo.Count) {
    [void]$sb.Append("<table><tr><th>Report</th><th>Status</th><th>Screenshots</th><th>Generated</th><th>Size</th></tr>")
    foreach ($r in $reportInfo) {
        $cls = switch ($r.Status) { 'PASS' {'b-pass'} 'FAIL' {'b-fail'} default {'b-unk'} }
        [void]$sb.Append("<tr><td><a href='$($r.Name)'>$($r.Name)</a></td>" +
            "<td><span class='badge $cls'>$($r.Status)</span></td>" +
            "<td>$($r.Shots)</td><td>$($r.When)</td><td>$($r.Size) KB</td></tr>")
    }
    [void]$sb.Append("</table>")
} else {
    [void]$sb.Append("<p style='margin:0 32px;color:#94a3b8'>No reports generated yet. Run a test to create one.</p>")
}
[void]$sb.Append("</body></html>")

$indexPath = Join-Path $ResultsDir 'index.html'
[IO.File]::WriteAllText($indexPath, $sb.ToString(), [Text.Encoding]::UTF8)

Write-Host "Index built: $indexPath" -ForegroundColor Green
Write-Host "  Tests found : $total"
Write-Host "  Reports     : $($reportInfo.Count)  (pass $pass / fail $fail)"
