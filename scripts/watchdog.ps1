<#
.SYNOPSIS
  Watchdog: ensure the weekend runner is alive; restart it if dead/stalled. Run every 3h.

.DESCRIPTION
  Designed to be invoked by Windows Task Scheduler on a 3-hour trigger. Each run it:
    1. If the ledger is already complete -> nothing to do (and unregisters nothing; harmless).
    2. Else checks reports/heartbeat.json:
         - missing, OR lastBeat older than -StaleHours, OR the pid is not running
           => the runner crashed/hung/rebooted: (re)launch weekend-runner.ps1 detached.
         - fresh beat + live pid => runner healthy; do nothing.
  Because weekend-runner.ps1 is resumable (preserves catalog ids, re-derives uncovered from
  the ledger), a restart simply continues where it left off. Idempotent and safe to run often.

.PARAMETER StaleHours   How old the heartbeat may be before we consider the runner dead (3).
#>
[CmdletBinding()]
param(
    [double]$StaleHours = 3,
    [double]$MaxUsd     = 50,
    [string]$Branch     = 'qa-ai-discovery',
    [string]$DeadlineUtc = '',
    [string]$QaRoot     = ''
)
$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty when Task Scheduler launches us via -File; resolve robustly.
$scriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($scriptDir)) { $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
if ([string]::IsNullOrWhiteSpace($scriptDir)) { $scriptDir = 'C:\Dev\exatouch-qa\scripts' }
if ([string]::IsNullOrWhiteSpace($QaRoot))    { $QaRoot   = (Resolve-Path (Join-Path $scriptDir '..')).Path }
$scripts   = Join-Path $QaRoot 'scripts'
$heartbeat = Join-Path $QaRoot 'reports\heartbeat.json'
$logFile   = Join-Path $QaRoot 'reports\watchdog.log'
function WLog($m) { $l = "[{0}] {1}" -f (Get-Date -Format 'u'), $m; Write-Host $l; Add-Content $logFile $l }

$haveClaude = [bool](Get-Command claude -ErrorAction SilentlyContinue)
if ([string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY) -and -not $haveClaude) { WLog "No auth (no env key, no claude CLI). Exiting."; exit 2 }

# 1. already complete? then stop the whole schedule cleanly.
& (Join-Path $scripts 'ledger.ps1') *> $null
if ($LASTEXITCODE -eq 0) {
    WLog "Ledger complete - all tests covered. Disabling watchdog schedule."
    schtasks /Change /TN 'ExatouchQA-Watchdog' /DISABLE *> $null
    exit 0
}

# 2. is the runner alive?
$alive = $false
if (Test-Path $heartbeat) {
    try {
        $hb = Get-Content $heartbeat -Raw | ConvertFrom-Json
        $age = ((Get-Date).ToUniversalTime() - [datetime]::Parse($hb.lastBeat).ToUniversalTime()).TotalHours
        $procUp = $false
        if ($hb.pid) { $procUp = [bool](Get-Process -Id $hb.pid -ErrorAction SilentlyContinue) }
        if ($age -lt $StaleHours -and $procUp) { $alive = $true; WLog "Runner healthy (pid $($hb.pid), beat ${age}h ago, area $($hb.area))." }
        else { WLog "Runner STALE: beat ${age}h ago, pid up=$procUp. Restarting." }
    } catch { WLog "Heartbeat unreadable: $($_.Exception.Message). Restarting." }
} else { WLog "No heartbeat file. Starting runner." }

if ($alive) { exit 0 }

# 3. (re)launch the runner detached so it survives this watchdog process exiting.
$runner = Join-Path $scripts 'weekend-runner.ps1'
$args = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $runner, '-MaxUsd', $MaxUsd, '-Branch', $Branch)
if ($DeadlineUtc) { $args += @('-DeadlineUtc', $DeadlineUtc) }
Start-Process -FilePath 'powershell.exe' -ArgumentList $args -WindowStyle Hidden
WLog "Launched weekend-runner.ps1 (MaxUsd=$MaxUsd, branch=$Branch)."
