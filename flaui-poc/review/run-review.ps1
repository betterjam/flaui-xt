<#
.SYNOPSIS
  Per-test AI review with agent INHERITANCE (base -> area -> per-test). Review ONE test: ensure it
  has real run evidence (run it if it hasn't, or re-run if -Force), then have the AI agent analyze
  THAT test's source + evidence + the runtime map and suggest fixes. Writes review-<Test>.json.

.DESCRIPTION
  The prompt is COMPOSED, never repeated (DRY):
    BASE        review-prompt-base.md        general QA rules + JSON output contract (written once)
    + AREA      areas/<area>.md              only that area's specifics (Items: tabs/validations...)
    + PER-TEST  "review THIS test" + its source + its run evidence
  agents.json maps test -> area (area=null => base only; add granularity only when needed).

  Flow:
    1. No archived run-<Test>.json -> run that test now (real evidence). -Force re-runs it fresh.
    2. Feed the composed prompt + runtime map + this test's source + this test's run to `claude`.
    3. Write TestResults/review-<Test>.json. UI state = review-<Test>-state.json {running,phase,at}.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Test,
    [switch]$Force,
    [string]$PocDir,
    [string]$Model  = 'claude-opus-4-8'
)
$ErrorActionPreference = 'Stop'
$reviewDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path $MyInvocation.MyCommand.Path -Parent }
if (-not $PocDir) { $PocDir = Split-Path $reviewDir -Parent }
$results   = Join-Path $PocDir 'TestResults'
$out       = Join-Path $results "review-$Test.json"
$state     = Join-Path $results "review-$Test-state.json"
function Mark($phase) {
    @{ running = ($phase -ne 'idle'); phase = $phase; at = (Get-Date).ToString('u') } |
        ConvertTo-Json | Set-Content $state -Encoding utf8
}
function RLog($m) { "[{0}] {1}: {2}" -f (Get-Date -Format 'HH:mm:ss'), $Test, $m |
    Add-Content (Join-Path $results 'review-run.log') }

Mark 'starting'; RLog "review started (Force=$Force)"

# --- resolve the agent inheritance chain from the manifest ------------------
$manifest = Get-Content (Join-Path $reviewDir 'agents.json') -Raw | ConvertFrom-Json
$baseFile = Join-Path $reviewDir $manifest.base
$cfg      = $manifest.tests.$Test
$area     = if ($cfg) { $cfg.area } else { $null }
$chain    = if ([string]::IsNullOrEmpty($area)) { "base -> test:$Test" } else { "base -> area:$area -> test:$Test" }
RLog "agent chain: $chain"

# --- ensure evidence: run this test if it has no archived result (or -Force) ---
$archive = Join-Path $results "run-$Test.json"
$needsRun = $Force -or -not (Test-Path $archive)
if ($needsRun) {
    Mark "running: $Test"; RLog "running test (force=$Force, hadResult=$([bool](Test-Path $archive)))"
    $runner = Join-Path $PocDir 'run-tests.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $runner -Filter $Test -PocDir $PocDir 2>&1 | Out-Null
} else {
    RLog "reuse existing result"
}

# --- COMPOSE the prompt: base + area + per-test focus + data (no repetition) ---
Mark 'analyzing'; RLog "assembling evidence for the agent"
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine((Get-Content $baseFile -Raw))                                 # BASE (general, once)
if ($area) {                                                                       # AREA (only if defined)
    $areaFile = Join-Path $reviewDir "areas\$area.md"
    if (Test-Path $areaFile) { [void]$sb.AppendLine((Get-Content $areaFile -Raw)) }
}
# PER-TEST focus (granular instructions added only here)
[void]$sb.AppendLine("`n## Review focus - THIS test only")
[void]$sb.AppendLine("Review ONLY the test named **$Test**. Judge its coverage of its area (per the")
[void]$sb.AppendLine("runtime map) and whether it is honest. Set `"test`" to `"$Test`" in your JSON output.")

[void]$sb.AppendLine("`n===== DATA: runtime-map.json (what the app actually has) =====")
$rtMap = Join-Path $results 'runtime-map.json'
if (Test-Path $rtMap) { [void]$sb.AppendLine((Get-Content $rtMap -Raw)) } else { [void]$sb.AppendLine("(no runtime map)") }

[void]$sb.AppendLine("`n===== DATA: source for this test =====")
foreach ($cs in Get-ChildItem $PocDir -Filter '*.cs' -File) {
    $raw = Get-Content $cs.FullName -Raw
    if ($raw -notmatch [regex]::Escape($Test)) { continue }
    [void]$sb.AppendLine("`n----- FILE: $($cs.Name) -----"); [void]$sb.AppendLine($raw)
}

[void]$sb.AppendLine("`n===== DATA: this test's ACTUAL run result (the evidence) =====")
if (Test-Path $archive) { [void]$sb.AppendLine((Get-Content $archive -Raw)) }
else { [void]$sb.AppendLine("(no run result for this test)") }

$prompt = $sb.ToString()
RLog ("invoking claude (prompt {0} KB)" -f [math]::Round($prompt.Length/1KB))
$raw = $prompt | & claude -p --output-format json --model $Model 2>&1
$txt = $null
try { $txt = ($raw | ConvertFrom-Json).result } catch { $txt = ($raw -join "`n") }
$m = [regex]::Match($txt, '(?s)\{.*\}'); if ($m.Success) { $txt = $m.Value }

try {
    $parsed = $txt | ConvertFrom-Json
    $txt | Set-Content $out -Encoding utf8
    RLog "review OK (score=$($parsed.coverageScore) gaps=$($parsed.gaps.Count) quality=$($parsed.qualityIssues.Count))"
} catch {
    @{ test=$Test; summary='The review agent did not return valid JSON.'; coverageScore=0; gaps=@(); qualityIssues=@(); topRecommendations=@('Re-run the review.') } |
        ConvertTo-Json -Depth 6 | Set-Content $out -Encoding utf8
    $txt | Set-Content (Join-Path $results "review-$Test-raw.txt") -Encoding utf8
    RLog "agent reply not valid JSON - wrote fallback"
}
Mark 'idle'; RLog "review done"
