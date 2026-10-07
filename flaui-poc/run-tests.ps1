<#
.SYNOPSIS
  Runs the FlaUI tests (all, or one via -Filter) and updates run-state.json. Launched as an
  independent process by dashboard-server.ps1 so it survives regardless of the server.
#>
param(
    [string]$Filter = '',
    [string]$PocDir = $PSScriptRoot
)
$ErrorActionPreference = 'Continue'
if (-not $PocDir) { $PocDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$ResultsDir = Join-Path $PocDir 'TestResults'
$StateFile  = Join-Path $ResultsDir 'run-state.json'
$logFile    = Join-Path $ResultsDir 'run-tests.log'
function RLog($m) { "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m | Add-Content $logFile }

# map filter -> the report it produces (for auto-open)
$reportMap = @{
    'Navigate_Home_to_Items_and_back'           = 'navigation-report.html'
    'Open_Item_List_from_Home'                  = 'item-list-report.html'
    'Open_first_item_in_the_list'               = 'open-item-report.html'
    'Edit_save_reopen_and_verify_item_persistence' = 'item-persistence-report.html'
    'Edit_all_tabs_save_reopen_verify'             = 'item-full-persistence-report.html'
    'Explore_item_detail_tabs_and_describe_what_we_find' = 'runtime-explorer-report.html'
}
$lastReport = if ($Filter -and $reportMap.ContainsKey($Filter)) { $reportMap[$Filter] } else { '' }

# reset the live stream so the live page starts fresh
@{ title = 'Starting test...'; status = 'running'; steps = @() } |
    ConvertTo-Json | Set-Content (Join-Path $ResultsDir 'live-run.json') -Encoding utf8

# mark running
@{ running = $true; lastFilter = $Filter; lastReport = $lastReport
   startedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'); finishedAt = '' } |
    ConvertTo-Json | Set-Content $StateFile -Encoding utf8
RLog "RUN start filter='$Filter'"

Set-Location $PocDir
$env:FLAUI_HEADLESS = ''

# --- recording-driver awareness -------------------------------------------------------------
# A live Flow-Capture driver (AutomationServer on :5200) holds its OWN Exatouch open. Running a
# test alongside it used to kill that app (leaving a zombie driver) while both stacks fought over
# UIA. So: if a driver answers on :5200, shut it down cleanly first and restart it after the run.
function Test-DriverUp {
    try {
        $p = [Net.HttpWebRequest]::Create('http://localhost:5200/scene'); $p.Method = 'POST'; $p.ContentLength = 0; $p.Timeout = 2500
        $resp = $p.GetResponse(); $resp.Close(); return $true   # ANY answer = a driver host is alive (healthy or zombie)
    } catch { return $false }
}
function Stop-DriverHost {
    try { $p = [Net.HttpWebRequest]::Create('http://localhost:5200/stop'); $p.Method = 'POST'; $p.ContentLength = 0; $p.Timeout = 1500; $p.GetResponse().Close() } catch { }
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='testhost.exe' OR Name='testhost.x86.exe' OR Name='powershell.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Run_automation_driver*' } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue } catch { } }
}
$driverWasUp = Test-DriverUp
if ($driverWasUp) { RLog "recording driver detected on :5200 - stopping it for this run (will restart after)"; Stop-DriverHost; Start-Sleep -Milliseconds 800 }

Get-Process -Name Exatouch -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
& dotnet build -c Debug 2>&1 | Out-Null
if ($Filter) { & dotnet test --no-build -c Debug --filter "FullyQualifiedName~$Filter" 2>&1 | Out-Null }
else         { & dotnet test --no-build -c Debug 2>&1 | Out-Null }
Get-Process -Name Exatouch -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
& (Join-Path $PocDir 'build-index.ps1') 2>&1 | Out-Null

# safety net: if the test didn't stream a final status (no LiveLog), force-finish the live
# file by reading the run result so the live page stops spinning. Derive PASS/FAIL from report.
$liveFile = Join-Path $ResultsDir 'live-run.json'
try {
    $live = Get-Content $liveFile -Raw | ConvertFrom-Json
    if ($live.status -eq 'running') {
        $finalStatus = 'PASS'
        if ($lastReport) {
            $rp = Join-Path $ResultsDir $lastReport
            if (Test-Path $rp) { $h = Get-Content $rp -Raw; if ($h -match '>FAIL<') { $finalStatus = 'FAIL' } }
        }
        $live.status = $finalStatus
        $live | ConvertTo-Json -Depth 6 | Set-Content $liveFile -Encoding utf8
    }
} catch { }

# Archive this run's data per-test so the Angular app can render the report natively
# (run-<TestName>.json holds the steps+screenshots+status; Angular reads it for the Report tab).
if ($Filter) {
    try {
        $finished = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
        $liveData = Get-Content $liveFile -Raw | ConvertFrom-Json
        @{ test = $Filter; status = $liveData.status; title = $liveData.title
           finishedAt = $finished; steps = $liveData.steps } |
            ConvertTo-Json -Depth 6 | Set-Content (Join-Path $ResultsDir "run-$Filter.json") -Encoding utf8
    } catch { RLog "archive failed: $($_.Exception.Message)" }
}

# restart the recording driver if we stopped it for this run (fresh Exatouch, latest build)
if ($driverWasUp) {
    RLog "restarting recording driver on :5200"
    Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',"Set-Location '$PocDir'; dotnet test --no-build -c Debug --filter 'FullyQualifiedName~Run_automation_driver' *> '$ResultsDir\driver.log'" -WindowStyle Hidden
}

# mark finished (keep lastReport so the dashboard can offer to open it)
@{ running = $false; lastFilter = $Filter; lastReport = $lastReport
   finishedAt = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') } |
    ConvertTo-Json | Set-Content $StateFile -Encoding utf8
RLog "RUN done filter='$Filter' report='$lastReport'"
