<#
.SYNOPSIS
  Register the 3-hour watchdog in Windows Task Scheduler (and at startup). Run once.

.DESCRIPTION
  Creates task 'ExatouchQA-Watchdog' that runs watchdog.ps1 every 3 hours AND at logon, so
  the weekend discovery run is restarted within 3 hours of any crash/hang/reboot and continues
  to completion. Uninstall with: -Remove. The key is read from the env at run time, never stored.

.PARAMETER MaxUsd / Branch / DeadlineUtc   Passed through to the watchdog/runner.
.PARAMETER Remove   Unregister the task instead of creating it.
#>
[CmdletBinding()]
param(
    [double]$MaxUsd = 50,
    [string]$Branch = 'qa-ai-discovery',
    [string]$DeadlineUtc = '',
    [switch]$Remove,
    [string]$QaRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
)
$ErrorActionPreference = 'Stop'
$taskName = 'ExatouchQA-Watchdog'

if ($Remove) {
    schtasks /Delete /TN $taskName /F
    Write-Host "Removed scheduled task '$taskName'."
    return
}

if ([string]::IsNullOrWhiteSpace($env:ANTHROPIC_API_KEY)) {
    Write-Warning "ANTHROPIC_API_KEY is not set as a *user* env var. Set it with: setx ANTHROPIC_API_KEY ""sk-ant-...""  then reopen your terminal, so the scheduled task inherits it."
}

$wd = Join-Path $QaRoot 'scripts\watchdog.ps1'
$inner = "-NoProfile -ExecutionPolicy Bypass -File `"$wd`" -StaleHours 3 -MaxUsd $MaxUsd -Branch $Branch"
if ($DeadlineUtc) { $inner += " -DeadlineUtc $DeadlineUtc" }
$action = "powershell.exe $inner"

# every 3 hours, indefinitely; runs whether or not the user is logged on if you supply creds.
schtasks /Create /TN $taskName /TR $action /SC HOURLY /MO 3 /F /RL LIMITED | Out-Null
# also fire once at logon so a reboot resumes promptly
schtasks /Create /TN "$taskName-AtLogon" /TR $action /SC ONLOGON /F /RL LIMITED | Out-Null

Write-Host "Installed '$taskName' (every 3h) + '$taskName-AtLogon'."
Write-Host "Kick off the first run now with:  schtasks /Run /TN $taskName"
Write-Host "It will keep the weekend runner alive and auto-stop when the ledger is complete."
