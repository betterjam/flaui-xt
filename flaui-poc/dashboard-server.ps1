<#
.SYNOPSIS
  Local test-runner dashboard for the Exatouch FlaUI suite. Serves a web UI with a "Run
  Tests" button; clicking it runs `dotnet test` in the background and the page shows live
  status (running -> pass/fail) with links to each report. No external dependencies.

.DESCRIPTION
  A tiny HttpListener-based server on http://localhost:<port>. Endpoints:
    GET  /            -> the dashboard HTML (auto-refreshes status via polling)
    GET  /api/status  -> JSON: current run state + per-test results + discovered tests
    POST /api/run     -> start a test run (all, or ?test=Name for one)
  Test runs happen in a background job; results are parsed from the .trx and reports folder.
  Stop with Ctrl+C.

.PARAMETER Port  Port to listen on (default 5099).
#>
[CmdletBinding()]
param(
    [int]$Port = 5099,
    [string]$PocDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
if (-not $PocDir) { $PocDir = Split-Path -Parent $MyInvocation.MyCommand.Path }
$ResultsDir = Join-Path $PocDir 'TestResults'
$StateFile  = Join-Path $ResultsDir 'run-state.json'
New-Item -ItemType Directory -Path $ResultsDir -Force | Out-Null

# ---- discover [Test] methods ---------------------------------------------------
function Get-Tests {
    $tests = @()
    foreach ($cs in Get-ChildItem $PocDir -Filter '*.cs' -File) {
        $raw = Get-Content $cs.FullName -Raw
        # skip whole fixtures marked [Explicit] (diagnostics, not user-facing tests)
        if ($raw -match '\[Explicit') { continue }
        $lines = Get-Content $cs.FullName
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match '^\s*\[Test\]') {
                for ($j = $i + 1; $j -lt [Math]::Min($i + 4, $lines.Count); $j++) {
                    $m = [regex]::Match($lines[$j], 'public\s+void\s+(\w+)\s*\(')
                    if ($m.Success) {
                        $name = $m.Groups[1].Value
                        $tests += [pscustomobject]@{ method = $name; pretty = ($name -replace '_',' '); file = $cs.Name }
                        break
                    }
                }
            }
        }
    }
    return $tests
}

# ---- read latest report status per test (by scanning report html) --------------
function Get-Reports {
    $out = @{}
    Get-ChildItem $ResultsDir -Filter '*report*.html' -File -EA SilentlyContinue |
        Where-Object { $_.Name -ne 'index.html' } | ForEach-Object {
        $html = Get-Content $_.FullName -Raw
        $status = if ($html -match '>FAIL<') { 'FAIL' } elseif ($html -match '>PASS<') { 'PASS' } else { 'UNKNOWN' }
        $out[$_.Name] = @{ status = $status; when = $_.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'); size = [math]::Round($_.Length/1KB) }
    }
    return $out
}

function Write-State($state) { $state | ConvertTo-Json -Depth 6 | Set-Content $StateFile -Encoding utf8 }
function Read-State {
    if (Test-Path $StateFile) { try { return Get-Content $StateFile -Raw | ConvertFrom-Json } catch { } }
    return [pscustomobject]@{ running = $false; lastFilter = ''; startedAt = ''; finishedAt = '' }
}

# ---- the dashboard HTML --------------------------------------------------------
function Get-DashboardHtml {
@"
<!doctype html><html><head><meta charset='utf-8'><title>Exatouch UI Test Runner</title>
<style>
 body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#0f172a;color:#e2e8f0}
 header{background:#1e293b;padding:20px 32px;border-bottom:3px solid #38bdf8;display:flex;justify-content:space-between;align-items:center}
 h1{margin:0;font-size:20px}
 button{background:#38bdf8;color:#0f172a;border:none;padding:10px 20px;border-radius:8px;font-size:14px;font-weight:700;cursor:pointer}
 button:hover{background:#0ea5e9} button:disabled{background:#475569;color:#94a3b8;cursor:not-allowed}
 .small{background:#334155;color:#e2e8f0;padding:6px 12px;font-size:12px;font-weight:600}
 .small:hover{background:#475569}
 table{width:calc(100% - 64px);margin:24px 32px;border-collapse:collapse;background:#1e293b;border-radius:10px;overflow:hidden}
 th,td{text-align:left;padding:12px 18px;border-bottom:1px solid #334155;font-size:14px} th{background:#334155;color:#cbd5e1}
 a{color:#38bdf8;text-decoration:none} a:hover{text-decoration:underline}
 .badge{padding:2px 12px;border-radius:20px;font-size:12px;font-weight:600}
 .PASS{background:#166534;color:#bbf7d0} .FAIL{background:#991b1b;color:#fecaca}
 .RUNNING{background:#854d0e;color:#fde68a} .NONE{background:#475569;color:#cbd5e1}
 .bar{margin:16px 32px;color:#94a3b8;font-size:13px;display:flex;align-items:center}
 .dot{display:inline-block;width:9px;height:9px;border-radius:50%;margin-right:8px}
 .live{background:#4ade80} .idle{background:#64748b}
 .spin{width:16px;height:16px;border:3px solid #334155;border-top-color:#38bdf8;border-radius:50%;display:inline-block;margin-right:10px;animation:rot .8s linear infinite}
 @keyframes rot{to{transform:rotate(360deg)}}
 tr.running td{background:#27314a}
 .pulse{animation:pulse 1.2s ease-in-out infinite}
 @keyframes pulse{0%,100%{opacity:1}50%{opacity:.45}}
</style></head><body>
<header>
 <h1>Exatouch UI Test Runner <span class='small' style='font-weight:400'>FlaUI + NUnit</span></h1>
 <div><button id='runAll' onclick='runAll()'>&#9654; Run all tests</button></div>
</header>
<div class='bar'><span id='statusIcon'></span><span id='statusText'>Idle</span></div>
<table id='tbl'><tr><th>Test</th><th>Status</th><th>Last run</th><th>Report</th><th></th></tr></table>
<script>
let polling=null;
let wasRunning=false;       // to detect the running->finished transition
let pendingReport=null;     // report to auto-open when a single-test run finishes
async function load(){
 const s=await (await fetch('/api/status')).json();
 const icon=document.getElementById('statusIcon'), txt=document.getElementById('statusText');
 document.getElementById('runAll').disabled=s.running;
 if(s.running){icon.innerHTML="<span class='spin'></span>";txt.textContent='Running tests, please wait...';txt.className='pulse';}
 else{icon.innerHTML="<span class='dot idle'></span>";txt.textContent=s.finishedAt?('Last run finished '+s.finishedAt):'Idle';txt.className='';}

 // when a run just finished, auto-open the report
 if(wasRunning && !s.running){
   const rep = pendingReport || (s.lastReport||'');
   if(rep){ window.open('/report/'+rep,'_blank'); }
   pendingReport=null;
 }
 wasRunning=s.running;

 let rows="<tr><th>Test</th><th>Status</th><th>Last run</th><th>Report</th><th></th></tr>";
 for(const t of s.tests){
   const r=t.report; let st=t.status||'NONE';
   if(s.running) st='RUNNING';
   const link=r?("<a href='/report/"+r+"' target='_blank'>open report</a>"):"-";
   const when=t.when||'-';
   rows+="<tr class='"+(s.running?'running':'')+"'><td><strong>"+t.pretty+"</strong></td>"+
     "<td><span class='badge "+st+"'>"+st+"</span></td>"+
     "<td>"+when+"</td><td>"+link+"</td>"+
     "<td><button class='small' "+(s.running?'disabled':'')+" onclick=\"runOne('"+t.method+"','"+(r||'')+"')\">Run</button></td></tr>";
 }
 document.getElementById('tbl').innerHTML=rows;
 if(s.running && !polling){polling=setInterval(load,2000);}
 if(!s.running && polling){clearInterval(polling);polling=null;}
}
async function runAll(){await fetch('/api/run',{method:'POST'});window.location='/live';}
async function runOne(m,rep){await fetch('/api/run?test='+m,{method:'POST'});window.location='/live';}
load();
</script></body></html>
"@
}

# ---- background test run (independent process, survives the server) ------------
function Start-Run($filter) {
    $st = Read-State
    if ($st.running) { SLog "run ignored (already running)"; return }
    $runner = Join-Path $PocDir 'run-tests.ps1'
    $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File', $runner, '-PocDir', $PocDir)
    if ($filter) { $a += @('-Filter', $filter) }
    Start-Process -FilePath 'powershell.exe' -ArgumentList $a -WindowStyle Hidden | Out-Null
    SLog "run started filter='$filter'"
}

# ---- map tests to their report files (by convention) ---------------------------
function Build-StatusPayload {
    $tests = Get-Tests
    $reports = Get-Reports
    # convention: method -> report file. Known mappings + heuristic.
    $map = @{
        'Navigate_Home_to_Items_and_back'           = 'navigation-report.html'
        'Open_Item_List_from_Home'                  = 'item-list-report.html'
        'Open_first_item_in_the_list'               = 'open-item-report.html'
        'Edit_save_reopen_and_verify_item_persistence' = 'item-persistence-report.html'
    }
    # area per BASE test from the review agents manifest (so the sidebar can group by area)
    $areaMap = @{}
    try {
        $mani = Get-Content (Join-Path $PocDir 'review\agents.json') -Raw | ConvertFrom-Json
        foreach ($p in $mani.tests.PSObject.Properties) { $areaMap[$p.Name] = $p.Value.area }
    } catch { }
    # imported tests carry the folder they were found in (import-folders.json) - so they group under
    # that same folder in the sidebar instead of falling into 'other'. Method name == recording name.
    $importFolders = @{}
    try {
        $ifJson = Join-Path $ResultsDir 'import-folders.json'
        if (Test-Path $ifJson) {
            (Get-Content $ifJson -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $importFolders[$_.Name] = $_.Value }
        }
    } catch { }
    $ext = Get-Extends    # child -> parent links (test inheritance)
    $arr = foreach ($t in $tests) {
        $rep = $map[$t.method]
        $info = if ($rep -and $reports.ContainsKey($rep)) { $reports[$rep] } else { $null }
        $base = Get-BaseMethod $t.method          # strip _vN
        $ver  = Get-VersionOf $t.method           # 1 if no suffix
        # area inherits up the extends chain, so a child sits in the same group as its parent
        $area = Resolve-Area $base $importFolders $areaMap $ext
        $extends = if ($ext.ContainsKey($base)) { $ext[$base] } else { '' }
        # AUTHORITATIVE status: the per-test run archive (run-<Method>.json) has the real PASS/FAIL for
        # THIS exact method - including versioned ones (_v2, _v3) that the report-name $map never knew
        # about. Prefer it; fall back to the report-html scan only if there's no archive.
        $status = $(if ($info) { $info.status } else { $null })
        $when   = $(if ($info) { $info.when } else { $null })
        $runArchive = Join-Path $ResultsDir "run-$($t.method).json"
        if (Test-Path $runArchive) {
            try {
                $ra = Get-Content $runArchive -Raw | ConvertFrom-Json
                if ($ra.status) { $status = "$($ra.status)" }
                if ($ra.finishedAt) { $when = "$($ra.finishedAt)" }
            } catch { }
        }
        [pscustomobject]@{
            method = $t.method; pretty = ($base -replace '_',' ')   # pretty = base name (version shown separately)
            report = $rep; status = $status; when = $when
            base   = $base; version = $ver; area = $area; extends = $extends
        }
    }
    $st = Read-State
    # Detect ANY active run - dashboard-started (run-state) OR external (a fresh live-run.json
    # still in 'running' state, e.g. someone ran `dotnet test` directly).
    $running = [bool]$st.running
    $lf = Join-Path $ResultsDir 'live-run.json'
    if (-not $running -and (Test-Path $lf)) {
        try {
            $lv = Get-Content $lf -Raw | ConvertFrom-Json
            if ($lv.status -eq 'running' -and ((Get-Date) - (Get-Item $lf).LastWriteTime).TotalSeconds -lt 30) { $running = $true }
        } catch { }
    }
    return [pscustomobject]@{ running = $running; finishedAt = $st.finishedAt; lastReport = $st.lastReport; tests = @($arr) }
}

# ---- live streaming report page ------------------------------------------------
function Get-LiveHtml {
@"
<!doctype html><html><head><meta charset='utf-8'><title>Live Test Run</title>
<style>
 body{font-family:Segoe UI,Arial,sans-serif;margin:0;background:#0f172a;color:#e2e8f0}
 header{background:#1e293b;padding:20px 32px;border-bottom:3px solid #38bdf8}
 .back{color:#38bdf8;text-decoration:none;font-size:14px} .back:hover{text-decoration:underline}
 h1{margin:8px 0 0;font-size:20px}
 .state{margin:16px 32px;display:flex;align-items:center;font-size:15px}
 .spin{width:18px;height:18px;border:3px solid #334155;border-top-color:#38bdf8;border-radius:50%;display:inline-block;margin-right:12px;animation:rot .8s linear infinite}
 @keyframes rot{to{transform:rotate(360deg)}}
 .done{font-size:22px;margin-right:10px}
 .pass{color:#4ade80} .fail{color:#f87171}
 .step{margin:0 32px 18px;background:#1e293b;border-radius:10px;padding:16px 20px;opacity:0;transform:translateY(8px);animation:fade .4s ease forwards}
 @keyframes fade{to{opacity:1;transform:none}}
 .step .num{display:inline-block;background:#38bdf8;color:#0f172a;font-weight:700;font-size:12px;padding:2px 10px;border-radius:6px;margin-right:10px}
 .step .t{font-size:15px;line-height:1.5;margin-bottom:10px}
 .step img{max-width:520px;border:1px solid #475569;border-radius:6px;display:block}
 .banner{margin:0 32px 20px;padding:14px 20px;border-radius:10px;font-weight:600;font-size:16px;animation:fade .5s ease forwards}
 .banner.pass{background:#166534;color:#bbf7d0} .banner.fail{background:#991b1b;color:#fecaca}
</style></head><body>
<header><a href='/' class='back'>&larr; Back to all tests</a><h1 id='title'>Starting test...</h1></header>
<div class='state' id='state'><span class='spin'></span><span>Launching Exatouch and running the test...</span></div>
<div id='steps'></div>
<script>
let shown=0, finished=false;
async function poll(){
 let d; try{ d=await (await fetch('/api/live?t='+Date.now())).json(); }catch(e){ setTimeout(poll,1000); return; }
 if(d.title) document.getElementById('title').textContent=d.title;
 // render any new steps (stream)
 const box=document.getElementById('steps');
 for(let i=shown;i<d.steps.length;i++){
   const s=d.steps[i];
   const div=document.createElement('div'); div.className='step';
   div.innerHTML="<div class='t'><span class='num'>Step "+s.n+"</span>"+escapeHtml(s.text)+"</div>"+
     (s.image?("<img src='/report/"+s.image+"?t="+Date.now()+"'>"):"");
   box.appendChild(div);
 }
 shown=d.steps.length;
 // finish handling
 const st=document.getElementById('state');
 if(d.status==='PASS'||d.status==='FAIL'){
   if(!finished){
     finished=true;
     const ok=d.status==='PASS';
     st.innerHTML="<span class='done "+(ok?'pass':'fail')+"'>"+(ok?'&#10004;':'&#10008;')+"</span><span>Test "+(ok?'passed':'failed')+"</span>";
     const b=document.createElement('div'); b.className='banner '+(ok?'pass':'fail');
     b.textContent=ok?'✔ All steps completed successfully.':'✘ The test failed - see the last step above.';
     document.getElementById('steps').appendChild(b);
   }
   return; // stop polling
 }
 setTimeout(poll,1200);
}
function escapeHtml(s){return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');}
poll();
</script></body></html>
"@
}

# ---- recover a recorded action map from a generated test's .cs (for tests made before maps were saved) -
# Each generated action line ends with "// <label>"; we map the call back to an action + label so the
# Steps & Actions tab works for EVERY generated test, not just freshly-recorded ones.
function Get-RecordingFromCs($method) {
    if (-not $method) { return @() }
    $files = Get-ChildItem -Path $PocDir -Filter '*.cs' -File -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $txt = ''
        try { $txt = Get-Content $f.FullName -Raw -Encoding UTF8 } catch { continue }
        if ($txt -notmatch "void\s+$([regex]::Escape($method))\s*\(") { continue }
        $steps = @(); $n = 0
        foreach ($line in ($txt -split "`n")) {
            if ($line -match '^\s*(SetField|ClickEl|DblClick|OpenRow|SelectTab|_s\.Step)\b.*//\s*(.+?)\s*$') {
                $call = $matches[1]; $label = $matches[2].Trim()
                $action = switch ($call) {
                    'SetField'  { 'set' }   'SelectTab' { 'tab' }   'DblClick' { 'doubleclick' }
                    'OpenRow'   { 'click' } '_s.Step'   { 'picture' } default { 'click' }
                }
                $key = ''; $value = ''
                if ($call -eq 'SetField' -and $line -match 'SetField\("([^"]*)",\s*"([^"]*)"\)') { $key = $matches[1]; $value = $matches[2] }
                # for a picture, the readable description is the _s.Step("...") argument, not the comment
                if ($call -eq '_s.Step' -and $line -match '_s\.Step\("(.+?)"\)\s*;') { $label = $matches[1] }
                $n++
                $steps += [pscustomobject]@{ n = $n; action = $action; by = 'id'; key = $key; value = $value; label = $label; file = ''; keyboard = $false }
            }
        }
        return $steps
    }
    return @()
}

# ---- regenerate a recorded test's .cs from its saved MAP with the current (fixed) replay helpers -------
# Lets old tests (silent-skip replay) get the robust replay (wait-for-control + visible-name fallback +
# logged misses) without re-recording. Mirrors Recorder.Generate's output.
function EscCs($s) { if ($null -eq $s) { return '' }; return $s.Replace('\','\\').Replace('"','\"').Replace("`r",' ').Replace("`n",' ').Replace("`t",' ') }

# $ownMap = this test's OWN steps (the diff only). Inherited steps are NOT copied in - the generated
# BuildFlow REFERENCES the parent's BuildFlow (recursively), so a parent is the single source of truth and
# editing it flows to every descendant.
function Write-RecordedTestCs($ownMap, $cls, $name, $parentName = '') {
    $lines = foreach ($s in $ownMap) {
        if ("$($s.action)" -eq 'picture') { continue }   # pictures are skipped; each action self-highlights
        $by = "$($s.by)"; $key = EscCs "$($s.key)"; $val = EscCs "$($s.value)"; $label = EscCs "$($s.label)"
        # the click fallback name is the captured visible name (value); for OLDER maps that's empty, so
        # recover it from the label's quoted text ('Clicked button "Cash"' -> Cash) so dynamic ids resolve.
        $nm = "$($s.value)"
        if ([string]::IsNullOrEmpty($nm) -and "$($s.label)" -match '"([^"]+)"') { $nm = $matches[1] }
        $nm = EscCs $nm
        $idx = -1; if ($null -ne $s.index) { try { $idx = [int]$s.index } catch { $idx = -1 } }
        $parent = EscCs "$($s.parent)"; $ct = EscCs "$($s.controlType)"
        # lambdas use the passed-in session `s` (BuildFlow is static), not the instance field
        $act = switch ("$($s.action)") {
            'picture'     { "() => { s.Step(`"$label`"); return true; }" }
            'tab'         { "() => s.SelectTab(`"$key`")" }
            'set'         { "() => s.SetField(`"$key`", `"$val`")" }
            'doubleclick' { if ($by -eq 'row') { '() => s.OpenRow()' } else { "() => s.DblClickEl(`"$by`", `"$key`", `"$nm`", $idx, `"$parent`", `"$ct`")" } }
            default       { if ($by -eq 'row') { '() => s.OpenRow()' } else { "() => s.ClickEl(`"$by`", `"$key`", `"$nm`", $idx, `"$parent`", `"$ct`")" } }
        }
        "            flow.Add(new UiSession.FlowStep { Label = `"$label`", Act = $act });"
    }
    $body = ($lines -join "`n")
    $count = @($ownMap).Count
    # REFERENCE the parent (trunk): call its BuildFlow first, so its steps run before this test's own.
    $parentCall = ''
    $extLine = ''
    if ($parentName) {
        $pcls = ($parentName.Substring(0,1).ToUpper() + $parentName.Substring(1)) + 'Test'
        $parentCall = "            $pcls.BuildFlow(s, flow);   // REFERENCE the parent (trunk) - single source of truth, not a copy"
        $extLine = "    // EXTENDS $parentName - this test's flow = the parent's BuildFlow (by reference) + the steps below."
    }
@"
using NUnit.Framework;
using System.Collections.Generic;

namespace FlaUiPoc
{
    /// <summary>Auto-generated from a RECORDED user session. A THIN test: it declares its OWN steps and, if it
    /// extends a parent, REFERENCES the parent's BuildFlow (recursively). Resolution + self-healing retry live
    /// in the UiSession engine.</summary>
$extLine
    [TestFixture]
    public class $cls
    {
        private static readonly string OutDir = Paths.OutDir;
        private static readonly string ExePath = Paths.ExePath;
        private UiSession _s;

        /// <summary>Append this test's flow to <paramref name="flow"/>: the parent's steps first (by reference,
        /// recursively), then this test's own recorded steps. Shared so children can compose this test.</summary>
        public static void BuildFlow(UiSession s, List<UiSession.FlowStep> flow)
        {
$parentCall
$body
        }

        [OneTimeSetUp] public void Setup() => _s = UiSession.Launch(ExePath, OutDir, "$name", "Recorded flow: $name", autoDismiss: false);
        [OneTimeTearDown] public void Teardown() { _s?.WriteReport("Recorded - $name", "$name-report.html"); _s?.Dispose(); }

        [Test]
        public void $name()
        {
            var flow = new List<UiSession.FlowStep>();
            BuildFlow(_s, flow);   // parent (by reference) + this test's own steps
            _s.Report.NewCase("Replay of recorded session (" + flow.Count + " steps)");
            int failed = _s.RunFlow(flow);
            _s.Step(failed == 0 ? "Recorded flow replayed faithfully - every step matched." : failed + " step(s) could not be replayed - the flow diverged.");
            _s.Report.SetStatus(failed == 0 ? "PASS" : "FAIL"); _s.Finish(failed == 0 ? "PASS" : "FAIL");
            Assert.That(failed, Is.EqualTo(0), failed + " recorded step(s) could not be replayed - the app flow diverged from the recording.");
        }
    }
}
"@
}

# ---- test inheritance ("extends") --------------------------------------------------
# A test can EXTEND a parent: its own recording-<name>.json holds only the DIFF steps; the parent link
# lives in extends.json ({ child = parent }). The effective flow = parent's effective flow + the child's
# diff, flattened - so running the child replays the father then itself as ONE test. Regenerating a parent
# cascades to its children (their .cs embeds the parent's steps).
# Windows file names are case-INSENSITIVE but C# class names are case-SENSITIVE, so "register_module" and
# "Register_Module" would collide into one .cs file but two class names -> a broken cross-reference. Canonicalize
# every test name to the EXACT casing of its recording-<name>.json file, so the parent class and every child
# reference always agree.
function Canonicalize-TestName($name) {
    if ([string]::IsNullOrEmpty($name)) { return $name }
    $f = Get-ChildItem $ResultsDir -Filter "recording-$name.json" -File -EA SilentlyContinue | Select-Object -First 1
    if ($f) { return ($f.BaseName -replace '^recording-', '') }
    return $name
}
function Get-Extends {
    $p = Join-Path $ResultsDir 'extends.json'; $h = @{}
    if (Test-Path $p) { try { (Get-Content $p -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $h[(Canonicalize-TestName $_.Name)] = (Canonicalize-TestName $_.Value) } } catch { } }
    return $h
}
function Set-Extends($child, $parent) {
    $h = Get-Extends
    if ($parent) { $h[$child] = $parent } elseif ($h.ContainsKey($child)) { $h.Remove($child) }
    ($h | ConvertTo-Json) | Set-Content (Join-Path $ResultsDir 'extends.json') -Encoding UTF8
}
function Get-OwnMap($name) {
    $f = Join-Path $ResultsDir ("recording-$name.json")
    if (Test-Path $f) { try { return @(Get-Content $f -Raw -Encoding UTF8 | ConvertFrom-Json) } catch { return @() } }
    return @()
}
# the flattened parent-chain -> child flow. Each step is tagged inherited (true if it came from an ancestor)
# and from (the test it originated in), so the Steps & Actions tab can show the hierarchy. Cycle-guarded.
function Get-EffectiveMap($name, $seen = $null) {
    if ($null -eq $seen) { $seen = New-Object 'System.Collections.Generic.HashSet[string]' }
    if (-not $seen.Add($name)) { return @() }
    $ext = Get-Extends; $out = @()
    if ($ext.ContainsKey($name) -and $ext[$name]) {
        foreach ($s in (Get-EffectiveMap $ext[$name] $seen)) {
            if ($s.PSObject.Properties['inherited']) { $s.inherited = $true } else { Add-Member -InputObject $s -NotePropertyName inherited -NotePropertyValue $true -Force }
            if (-not $s.PSObject.Properties['from']) { Add-Member -InputObject $s -NotePropertyName from -NotePropertyValue $ext[$name] -Force }
            $out += $s
        }
    }
    foreach ($s in (Get-OwnMap $name)) {
        if ($s.PSObject.Properties['inherited']) { $s.inherited = $false } else { Add-Member -InputObject $s -NotePropertyName inherited -NotePropertyValue $false -Force }
        if (-not $s.PSObject.Properties['from']) { Add-Member -InputObject $s -NotePropertyName from -NotePropertyValue $name -Force }
        $out += $s
    }
    return ,$out
}
# resolve a base test's sidebar area, inheriting the parent's folder/area up the extends chain
function Resolve-Area($base, $importFolders, $areaMap, $ext, $depth = 0) {
    if ($importFolders.ContainsKey($base) -and $importFolders[$base]) { return $importFolders[$base] }
    if ($areaMap.ContainsKey($base) -and $areaMap[$base]) { return $areaMap[$base] }
    if ($depth -lt 12 -and $ext.ContainsKey($base) -and $ext[$base]) { return Resolve-Area $ext[$base] $importFolders $areaMap $ext ($depth + 1) }
    return 'other'
}
# canonical (re)generation of a recorded/extended test's .cs. Reference model: emit the test's OWN steps and
# a call to the parent's BuildFlow. Regenerate ANCESTORS FIRST so the referenced BuildFlow always exists
# (else the child .cs wouldn't compile), then cascade to children for consistency.
function Regen-RecordedTest($name, $seen = $null) {
    if ($null -eq $seen) { $seen = New-Object 'System.Collections.Generic.HashSet[string]' }
    if (-not $seen.Add($name)) { return $true }   # already handled this pass
    $ext = Get-Extends
    $parent = if ($ext.ContainsKey($name)) { $ext[$name] } else { '' }
    if ($parent) { Regen-RecordedTest $parent $seen }   # ancestor first - the reference target must exist
    $own = Get-OwnMap $name
    if (@($own).Count -eq 0 -and -not $parent) { return $false }   # nothing to generate (empty root)
    $cls = ($name.Substring(0,1).ToUpper() + $name.Substring(1)) + 'Test'
    Set-Content -Path (Join-Path $PocDir ($cls + '.cs')) -Value (Write-RecordedTestCs $own $cls $name $parent) -Encoding UTF8
    foreach ($k in $ext.Keys) { if ($ext[$k] -eq $name) { Regen-RecordedTest $k $seen } }   # keep children consistent
    return $true
}

# ---- Flow Capture driver lifecycle -------------------------------------------------
# The driver (AutomationServer) runs inside `dotnet test` -> dotnet.exe/testhost.exe, NOT a process named
# "testhost", so a plain Get-Process testhost misses it. A "zombie" driver (its Exatouch was killed but the
# host lives) still answers HTTP on :5200 with ok:false - so aliveness must check ok:true, and a real
# restart must kill the host by its COMMAND LINE.
function Stop-Driver {
    try { $p = [Net.HttpWebRequest]::Create('http://localhost:5200/stop'); $p.Method='POST'; $p.Timeout=1200; $p.GetResponse().Close() } catch { }
    Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='testhost.exe' OR Name='testhost.x86.exe'" -EA SilentlyContinue |
        Where-Object { $_.CommandLine -like '*Run_automation_driver*' -or $_.CommandLine -like '*FlaUiPoc*' } |
        ForEach-Object { try { Stop-Process -Id $_.ProcessId -Force -EA SilentlyContinue } catch { } }
    Get-Process Exatouch -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
}
function Test-DriverHealthy {
    # alive AND its app is up (a zombie answers HTTP but reports ok:false)
    try {
        $p = [Net.HttpWebRequest]::Create('http://localhost:5200/scene'); $p.Method='POST'; $p.ContentLength=0; $p.Timeout=2500
        $resp = $p.GetResponse(); $sr = New-Object IO.StreamReader($resp.GetResponseStream()); $txt = $sr.ReadToEnd(); $resp.Close()
        return ($txt -match '"ok"\s*:\s*true')
    } catch { return $false }
}
function Start-Driver {
    Start-Process powershell -ArgumentList '-NoProfile','-ExecutionPolicy','Bypass','-Command',"Set-Location '$PocDir'; dotnet test --no-build -c Debug --filter 'FullyQualifiedName~Run_automation_driver' *> '$ResultsDir\driver.log'" -WindowStyle Hidden
}

# ---- HTTP server ---------------------------------------------------------------
$logFile = Join-Path $ResultsDir 'dashboard.log'
function SLog($m) { "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $m | Add-Content $logFile }
# read the request body (POST payload) as a string
function Read-Body($req) {
    if (-not $req.HasEntityBody) { return '' }
    $sr = New-Object IO.StreamReader($req.InputStream, $req.ContentEncoding)
    try { return $sr.ReadToEnd() } finally { $sr.Close() }
}
# shared versioning helpers (Get-BaseMethod, Get-TestVersions, Get-LatestVersion)
. (Join-Path $PocDir 'review\versioning-lib.ps1')
SLog "starting on port $Port"

$listener = New-Object System.Net.HttpListener
$prefix = "http://localhost:$Port/"
$listener.Prefixes.Add($prefix)
try { $listener.Start(); SLog "listener started" }
catch { SLog "listener FAILED: $($_.Exception.Message)"; throw }
Write-Host "Dashboard running at $prefix  (Ctrl+C to stop)" -ForegroundColor Green
try { if (-not $env:DASH_NO_OPEN) { Start-Process $prefix } } catch { SLog "browser open skipped: $($_.Exception.Message)" }

# If the Angular app was built, serve it (single integrated stack: Angular UI + this API).
$ngDist = Join-Path $PocDir '..\dashboard-ng\dist\dashboard-ng\browser'
$ngDist = if (Test-Path $ngDist) { (Resolve-Path $ngDist).Path } else { '' }
if ($ngDist) { SLog "serving Angular app from $ngDist" } else { SLog "no Angular build; serving inline dashboard" }
function Ctype($p) {
    switch ([IO.Path]::GetExtension($p).ToLower()) {
        '.html' { 'text/html' } '.js' { 'text/javascript' } '.css' { 'text/css' }
        '.json' { 'application/json' } '.ico' { 'image/x-icon' } '.png' { 'image/png' }
        '.svg' { 'image/svg+xml' } '.woff2' { 'font/woff2' } default { 'application/octet-stream' }
    }
}

try {
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $req = $ctx.Request; $res = $ctx.Response
        $path = $req.Url.AbsolutePath
        try {
            # --- Angular static assets (js/css/etc) served from dist when present ---
            if ($ngDist -and $path -ne '/' -and $path -notlike '/api/*' -and $path -notlike '/report/*' -and $path -ne '/live') {
                $asset = Join-Path $ngDist ($path.TrimStart('/'))
                if (Test-Path $asset -PathType Leaf) {
                    $buf = [IO.File]::ReadAllBytes($asset)
                    $res.ContentType = Ctype $asset; $res.OutputStream.Write($buf, 0, $buf.Length); $res.OutputStream.Close(); continue
                }
            }
            if ($path -eq '/' -and $ngDist) {
                # serve Angular index.html (SPA entry)
                $buf = [IO.File]::ReadAllBytes((Join-Path $ngDist 'index.html'))
                $res.ContentType = 'text/html'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/' ) {
                $html = Get-DashboardHtml
                $buf = [Text.Encoding]::UTF8.GetBytes($html)
                $res.ContentType = 'text/html'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/live' -and -not $ngDist) {
                $html = Get-LiveHtml
                $buf = [Text.Encoding]::UTF8.GetBytes($html)
                $res.ContentType = 'text/html'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/live' -and $ngDist) {
                # Angular SPA route - serve index.html, the router handles /live
                $buf = [IO.File]::ReadAllBytes((Join-Path $ngDist 'index.html'))
                $res.ContentType = 'text/html'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/live') {
                # CONTRACT: run-state.json is the AUTHORITY for whether a test is running.
                # We merge it with the streamed steps so the frontend gets ONE consistent shape:
                #   runState: idle | running | passed | failed   (drives the animation)
                #   title, steps[]                                (the streamed detail)
                # This kills the "stale FAIL with 0 steps" bug - if nothing is running and the
                # live file is empty/old, we report 'idle' so the frontend shows no false finish.
                $rs = $null; $live = $null
                try { $rs = Get-Content (Join-Path $ResultsDir 'run-state.json') -Raw | ConvertFrom-Json } catch { }
                $lf = Join-Path $ResultsDir 'live-run.json'
                try { if (Test-Path $lf) { $live = Get-Content $lf -Raw | ConvertFrom-Json } } catch { }

                # A run is "running" if the dashboard's run-state says so OR the live stream itself
                # reports running and was updated recently. This way ANY run is detected - including
                # tests started OUTSIDE the dashboard (e.g. `dotnet test` from a terminal).
                $liveRunning = ($live -and $live.status -eq 'running')
                $liveFresh = $false
                if (Test-Path $lf) { $liveFresh = ((Get-Date) - (Get-Item $lf).LastWriteTime).TotalSeconds -lt 30 }
                $isRunning = [bool]($rs.running) -or ($liveRunning -and $liveFresh)

                $steps = @(); if ($live -and $live.steps) { $steps = $live.steps }
                $title = if ($live -and $live.title) { $live.title } else { 'Test' }

                $runState =
                    if ($isRunning) { 'running' }
                    elseif ($live -and $live.status -eq 'PASS' -and $steps.Count -gt 0) { 'passed' }
                    elseif ($live -and $live.status -eq 'FAIL' -and $steps.Count -gt 0) { 'failed' }
                    else { 'idle' }   # nothing running + no real streamed steps = idle (no false finish)

                # the saved report + which test ran (so the frontend can open the report and RETRY)
                $report = if ($rs.lastReport) { $rs.lastReport } else { '' }
                $testName = if ($rs.lastFilter) { $rs.lastFilter } else { '' }
                $isDebug = [bool]($live.debug)
                $payload = [pscustomobject]@{ runState = $runState; title = $title; steps = $steps; report = $report; test = $testName; debug = $isDebug } | ConvertTo-Json -Depth 6
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/review') {
                # PER-TEST AI review result. ?test=X returns that test's review-<X>.json + its
                # review-<X>-state.json {running,phase}. Each test has its OWN review section.
                $t = $req.QueryString['test']
                $rf = if ($t) { Join-Path $ResultsDir "review-$t.json" } else { '' }
                $sf = if ($t) { Join-Path $ResultsDir "review-$t-state.json" } else { '' }
                $running = $false; $phase = ''
                try { if ($sf -and (Test-Path $sf)) { $sv = Get-Content $sf -Raw | ConvertFrom-Json; $running = [bool]$sv.running; $phase = "$($sv.phase)" } } catch { }
                $body = if ($rf -and (Test-Path $rf)) { Get-Content $rf -Raw } else { '{"summary":"","coverageScore":0,"gaps":[],"qualityIssues":[],"topRecommendations":[]}' }
                $payload = "{""running"":$($running.ToString().ToLower()),""phase"":""$phase"",""review"":$body}"
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/review/run' -and $req.HttpMethod -eq 'POST') {
                # launch the per-test review agent for ?test=X. It runs the test first if it has no
                # archived result (?force=1 re-runs it fresh), then the AI analyzes that one test.
                # NOTE: detached `Start-Process powershell` fails (exit 255) in this env, so we use
                # Start-Job (reliable, in-process) to spawn the reviewer.
                $t = $req.QueryString['test']
                if ($t) {
                    $rev = Join-Path $PocDir 'review\run-review.ps1'
                    $frc = ($req.QueryString['force'] -eq '1')
                    Start-Job -ScriptBlock {
                        param($rev,$t,$frc,$poc)
                        $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$rev,'-Test',$t,'-PocDir',$poc)
                        if ($frc) { $a += '-Force' }
                        & powershell.exe @a 2>&1 | Out-Null
                    } -ArgumentList $rev,$t,$frc,$PocDir | Out-Null
                    SLog "per-test review started (test=$t force=$($req.QueryString['force']))"
                }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/list') {
                # all versions of a base test (for the per-test version picker) + which is latest
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                $json = '{"base":"","versions":[],"latest":0}'
                if ($base) {
                    $vers = @(Get-TestVersions -PocDir $PocDir -BaseMethod $base)
                    $latest = if ($vers.Count) { $vers[-1].version } else { 0 }
                    $arr = $vers | ForEach-Object { @{ version = $_.version; method = $_.method } }
                    $json = @{ base = $base; latest = $latest; versions = @($arr) } | ConvertTo-Json -Depth 5 -Compress
                }
                $buf = [Text.Encoding]::UTF8.GetBytes($json)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/delete' -and $req.HttpMethod -eq 'POST') {
                # manually DELETE a specific version of a test (?test=base&version=N). Removes its .cs
                # from the project + its run/review artifacts. v1 (the original) cannot be deleted.
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                $vn = 0; [void][int]::TryParse($req.QueryString['version'], [ref]$vn)
                $ok = $false; $blocked = ''
                if ($base -and $vn -gt 1) {
                    # can't delete a version that has a CHILD (a later version was built from it) -
                    # that would orphan the child. Only the latest version of a family is deletable.
                    $allv = @(Get-TestVersions -PocDir $PocDir -BaseMethod $base)
                    $maxV = if ($allv.Count) { ($allv[-1].version) } else { 0 }
                    if ($vn -lt $maxV) {
                        $blocked = "v$vn has a later version (v$maxV) built on it; delete the newer version(s) first."
                        SLog "version delete BLOCKED: $base v$vn ($blocked)"
                    }
                }
                if ($base -and $vn -gt 1 -and -not $blocked) {
                    $method = "${base}_v$vn"
                    Get-ChildItem $PocDir -Filter "*.v$vn.cs" -File | ForEach-Object {
                        if ((Get-Content $_.FullName -Raw) -match [regex]::Escape($method)) {
                            Remove-Item $_.FullName -Force -EA SilentlyContinue; $ok = $true
                        }
                    }
                    Remove-Item (Join-Path $ResultsDir "run-$method.json") -Force -EA SilentlyContinue
                    Remove-Item (Join-Path $ResultsDir "review-$method.json") -Force -EA SilentlyContinue
                    Remove-Item (Join-Path $ResultsDir "review-$method-state.json") -Force -EA SilentlyContinue
                    SLog "version deleted: $method (ok=$ok)"
                }
                $payload = @{ ok = $ok; blocked = $blocked } | ConvertTo-Json -Compress
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/test/delete' -and $req.HttpMethod -eq 'POST') {
                # Delete a whole test (family) - ONLY when no other test EXTENDS it (no children in the trunk->branch tree).
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                $ok = $false; $blocked = ''
                if ($base) {
                    $ext = Get-Extends
                    $children = @($ext.Keys | Where-Object { $ext[$_] -eq $base })
                    if ($children.Count -gt 0) {
                        $blocked = "this test has $($children.Count) branch test(s) extending it ($($children -join ', ')); delete those first."
                        SLog "test delete BLOCKED: $base has children ($($children -join ','))"
                    } else {
                        # remove every .cs that declares this test method (the base and any _vN versions live in their own files)
                        Get-ChildItem $PocDir -Filter '*.cs' -File | ForEach-Object {
                            if ((Get-Content $_.FullName -Raw) -match "public\s+void\s+$([regex]::Escape($base))(_v\d+)?\s*\(") {
                                Remove-Item $_.FullName -Force -EA SilentlyContinue; $ok = $true
                            }
                        }
                        # maps, run/review archives (base + versions)
                        Remove-Item (Join-Path $PocDir (($base.Substring(0,1).ToUpper()+$base.Substring(1))+'Test.recording.json')) -Force -EA SilentlyContinue
                        Get-ChildItem $ResultsDir -Filter "recording-$base.json" -File -EA SilentlyContinue | Remove-Item -Force -EA SilentlyContinue
                        Get-ChildItem $ResultsDir -File -EA SilentlyContinue | Where-Object { $_.Name -match "^(run|review|verdict)-$([regex]::Escape($base))(_v\d+)?(-state)?\.json$" } | Remove-Item -Force -EA SilentlyContinue
                        # drop hierarchy + folder links
                        Set-Extends $base ''
                        $iff = Join-Path $ResultsDir 'import-folders.json'
                        if (Test-Path $iff) { $h=@{}; (Get-Content $iff -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { if ($_.Name -ne $base) { $h[$_.Name]=$_.Value } }; ($h | ConvertTo-Json) | Set-Content $iff -Encoding UTF8 }
                        SLog "test deleted: $base (ok=$ok)"
                    }
                }
                $payload = @{ ok = $ok; blocked = $blocked } | ConvertTo-Json -Compress
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/verdict') {
                # the self-correcting verdict for a test family: did the latest version ACHIEVE the
                # accepted improvements? {achieved, reason, missing, attempt, maxAttempts, retrying}
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                $vf = if ($base) { Join-Path $ResultsDir "verdict-$base.json" } else { '' }
                $json = if ($vf -and (Test-Path $vf)) { Get-Content $vf -Raw } else { 'null' }
                $buf = [Text.Encoding]::UTF8.GetBytes($json)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/draft' -and $req.HttpMethod -eq 'POST') {
                # accept improvements -> agent drafts the NEXT version (staged, not yet live).
                # body = {"accepted":["...","..."]}. Launch via Start-Job (Start-Process fails here).
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                if ($base) {
                    $body = Read-Body $req
                    $accepted = @()
                    try { $accepted = @(($body | ConvertFrom-Json).accepted) } catch { }
                    # Write accepted to a FILE and pass its path (NOT the JSON inline): Start-Job's
                    # arg-marshalling mangles a "[...]" string with brackets/quotes, which made the
                    # child powershell hang. A clean file path marshals fine.
                    $accFile = Join-Path $ResultsDir "version-draft-$base-input.json"
                    $accJson = '[' + (($accepted | ForEach-Object { $_ | ConvertTo-Json -Compress }) -join ',') + ']'
                    $accJson | Set-Content $accFile -Encoding utf8
                    # clear any STALE draft (e.g. a previously-applied one) and immediately mark
                    # 'starting' so the modal shows the "agent writing" state right away (no flash of
                    # an old/empty draft on its first poll, which looked like the modal closing).
                    Remove-Item (Join-Path $ResultsDir "version-draft-$base.json") -Force -EA SilentlyContinue
                    @{ running = $true; phase = 'starting'; at = (Get-Date).ToString('u') } |
                        ConvertTo-Json | Set-Content (Join-Path $ResultsDir "version-draft-$base-state.json") -Encoding utf8
                    $drv = Join-Path $PocDir 'review\draft-version.ps1'
                    Start-Job -ScriptBlock {
                        param($drv,$t,$accFile,$poc)
                        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $drv -Test $t -AcceptedFile $accFile -PocDir $poc 2>&1 | Out-Null
                    } -ArgumentList $drv,$base,$accFile,$PocDir | Out-Null
                    SLog "version draft started (test=$base accepted=$($accepted.Count))"
                }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/draft') {
                # GET the current draft for a test: manifest + state + the DIFF (old vs staged code)
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                $running = $false; $phase = ''; $draft = $null; $oldCode = ''; $newCode = ''
                if ($base) {
                    $sf = Join-Path $ResultsDir "version-draft-$base-state.json"
                    try { if (Test-Path $sf) { $sv = Get-Content $sf -Raw | ConvertFrom-Json; $running = [bool]$sv.running; $phase = "$($sv.phase)" } } catch { }
                    $mf = Join-Path $ResultsDir "version-draft-$base.json"
                    if (Test-Path $mf) {
                        try {
                            $draft = Get-Content $mf -Raw | ConvertFrom-Json
                            if ($draft.status -eq 'staged') {
                                $stage = Join-Path $ResultsDir "staging\$($draft.file)"
                                if (Test-Path $stage) { $newCode = [string](Get-Content $stage -Raw) }
                                # the source we're diffing AGAINST = the file the draft came from (fromFile),
                                # i.e. the current latest version, read as a plain string.
                                $fromPath = Join-Path $PocDir "$($draft.fromFile)"
                                if (Test-Path $fromPath) { $oldCode = [string](Get-Content $fromPath -Raw) }
                            }
                        } catch { }
                    }
                }
                # Assemble JSON manually so the (large) code strings are escaped exactly once and the
                # object graph can't blow up. ConvertTo-Json the small bits; hand-escape the code.
                $draftJson = if ($draft) { ($draft | ConvertTo-Json -Depth 6 -Compress) } else { 'null' }
                $escOld = ($oldCode | ConvertTo-Json)   # ConvertTo-Json on a lone string = a valid JSON string literal
                $escNew = ($newCode | ConvertTo-Json)
                $payload = '{"running":' + ($running.ToString().ToLower()) +
                           ',"phase":' + ($phase | ConvertTo-Json) +
                           ',"draft":' + $draftJson +
                           ',"oldCode":' + $escOld +
                           ',"newCode":' + $escNew + '}'
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/discard' -and $req.HttpMethod -eq 'POST') {
                # throw away the staged draft (file + manifest)
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                if ($base) {
                    $mf = Join-Path $ResultsDir "version-draft-$base.json"
                    try {
                        if (Test-Path $mf) {
                            $d = Get-Content $mf -Raw | ConvertFrom-Json
                            $stage = Join-Path $ResultsDir "staging\$($d.file)"
                            if (Test-Path $stage) { Remove-Item $stage -Force }
                            Remove-Item $mf -Force
                        }
                    } catch { }
                    SLog "version draft discarded (test=$base)"
                }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/version/apply' -and $req.HttpMethod -eq 'POST') {
                # APPLY the staged draft: copy it into the test project (now live + runnable), then
                # auto-run the new version and auto-review it. Launch the chain via Start-Job.
                $t = $req.QueryString['test']; $base = if ($t) { Get-BaseMethod $t } else { '' }
                $ok = $false; $newMethod = ''
                if ($base) {
                    $mf = Join-Path $ResultsDir "version-draft-$base.json"
                    if (Test-Path $mf) {
                        try {
                            $d = Get-Content $mf -Raw | ConvertFrom-Json
                            $stage = Join-Path $ResultsDir "staging\$($d.file)"
                            $live  = Join-Path $PocDir $d.file
                            if ($d.status -eq 'staged' -and (Test-Path $stage)) {
                                Copy-Item $stage $live -Force         # <-- now part of the project
                                $newMethod = $d.newMethod
                                # PSCustomObject from ConvertFrom-Json can't take a NEW prop via '=';
                                # add appliedAt via Add-Member and set status with -Force.
                                $d.status = 'applied'
                                $d | Add-Member -NotePropertyName appliedAt -NotePropertyValue ((Get-Date).ToString('u')) -Force
                                $d | ConvertTo-Json -Depth 6 | Set-Content $mf -Encoding utf8
                                $ok = $true
                                SLog "version applied: $($d.file) -> $newMethod (v$($d.toVersion))"
                                # auto-run the new version, then VERIFY it achieved the accepted
                                # improvements. verify-version drives the self-correcting loop
                                # (discard + re-draft with feedback, up to 3 attempts) and also runs
                                # the per-test review. Reset any stale verdict so the UI shows "checking".
                                Remove-Item (Join-Path $ResultsDir "verdict-$base.json") -Force -EA SilentlyContinue
                                $runner = Join-Path $PocDir 'run-tests.ps1'
                                $ver    = Join-Path $PocDir 'review\verify-version.ps1'
                                $rev    = Join-Path $PocDir 'review\run-review.ps1'
                                Start-Job -ScriptBlock {
                                    param($runner,$ver,$rev,$m,$poc)
                                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Filter $m -PocDir $poc 2>&1 | Out-Null
                                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $ver -Method $m -PocDir $poc 2>&1 | Out-Null
                                    # review the version that ended up winning (verdict names it)
                                    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $rev -Test $m -PocDir $poc 2>&1 | Out-Null
                                } -ArgumentList $runner,$ver,$rev,$newMethod,$PocDir | Out-Null
                            }
                        } catch { SLog "apply failed: $($_.Exception.Message)" }
                    }
                }
                $payload = @{ ok = $ok; newMethod = $newMethod } | ConvertTo-Json -Compress
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/run-data') {
                # the saved run for a test (steps+screenshots+status) so Angular renders the report natively
                $t = $req.QueryString['test']
                $rf = Join-Path $ResultsDir "run-$t.json"
                $json = if ($t -and (Test-Path $rf)) { Get-Content $rf -Raw } else { '{"test":"","status":"","steps":[]}' }
                $buf = [Text.Encoding]::UTF8.GetBytes($json)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/status') {
                $json = Build-StatusPayload | ConvertTo-Json -Depth 6
                $buf = [Text.Encoding]::UTF8.GetBytes($json)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/run' -and $req.HttpMethod -eq 'POST') {
                $test = $req.QueryString['test']
                Start-Run $test | Out-Null
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/run/stop' -and $req.HttpMethod -eq 'POST') {
                # stop a running test: graceful debug-stop if a debug run is up, then kill the test host + app
                try { $p = [Net.HttpWebRequest]::Create('http://localhost:5201/stop'); $p.Method = 'POST'; $p.Timeout = 1000; $p.GetResponse().Close() } catch { }
                Get-Process testhost, Exatouch -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
                # mark the run finished so the UI clears the spinner
                try { $lf = Join-Path $ResultsDir 'live-run.json'; if (Test-Path $lf) { $j = Get-Content $lf -Raw -Encoding UTF8 | ConvertFrom-Json; $j.status = 'FAIL'; ($j | ConvertTo-Json -Depth 6) | Set-Content $lf -Encoding UTF8 } } catch { }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true,"stopped":true}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/debug/run' -and $req.HttpMethod -eq 'POST') {
                # launch a test in DEBUG mode (EXA_DEBUG=1) so the engine hosts the pause/inspect server :5201
                $t = $req.QueryString['test']
                $started = $false
                if ($t) {
                    $cmd = "`$env:EXA_DEBUG='1'; Set-Location '$PocDir'; dotnet test --no-build -c Debug --filter 'FullyQualifiedName~$t' *> '$ResultsDir\debug-run.log'"
                    Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-Command', $cmd -WindowStyle Hidden
                    $started = $true
                }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":' + ($started.ToString().ToLower()) + '}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/ranorex/list') {
                # browse the Ranorex recordings (for the Import menu)
                $rxroot = Join-Path $PocDir '..\..\MyFirstExatouchTest'
                $rxroot = if (Test-Path $rxroot) { (Resolve-Path $rxroot).Path } else { '' }
                $recs = @()
                if ($rxroot) {
                    $recs = Get-ChildItem -Path $rxroot -Recurse -Filter *.rxrec -ErrorAction SilentlyContinue | ForEach-Object {
                        $rel = $_.FullName.Substring($rxroot.Length).TrimStart('\')
                        [pscustomobject]@{ name = $_.BaseName; full = $_.FullName; folder = (Split-Path $rel -Parent) }
                    }
                }
                $payload = [pscustomobject]@{ ok = $true; root = $rxroot; recordings = @($recs) } | ConvertTo-Json -Depth 5
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json; charset=utf-8'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/ranorex/import' -and $req.HttpMethod -eq 'POST') {
                # import selected .rxrec recordings -> our maps -> runnable FlaUI tests
                $body = Read-Body $req | ConvertFrom-Json
                $files = @($body.files)
                # map each recording's basename -> its Ranorex folder (so imported tests group by folder)
                $rxroot = Join-Path $PocDir '..\..\MyFirstExatouchTest'
                $rxroot = if (Test-Path $rxroot) { (Resolve-Path $rxroot).Path } else { '' }
                $fmap = @{}
                foreach ($f in $files) {
                    $bn = [IO.Path]::GetFileNameWithoutExtension($f)
                    # folder EXACTLY as /api/ranorex/list shows it (relative to the Ranorex root) so the
                    # imported test lands under the same group the user picked it from
                    $fmap[$bn] = if ($rxroot -and $f.StartsWith($rxroot)) { Split-Path ($f.Substring($rxroot.Length).TrimStart('\')) -Parent } else { Split-Path $f -Parent | Split-Path -Leaf }
                }
                $imp = Join-Path $PocDir '..\scripts\import-ranorex.py'
                $env:IMPORT_OUT = $ResultsDir
                $out = & python $imp @files 2>&1
                $imported = @(); $folders = @{}
                foreach ($line in $out) {
                    if ("$line" -match '^IMPORTED\t([^\t]+)\t(\d+)') {
                        $name = $matches[1]
                        $mf = Join-Path $ResultsDir ("recording-$name.json")
                        if (Test-Path $mf) {
                            try {
                                $map = Get-Content $mf -Raw -Encoding UTF8 | ConvertFrom-Json
                                $cls = ($name.Substring(0,1).ToUpper() + $name.Substring(1)) + 'Test'
                                Set-Content -Path (Join-Path $PocDir ($cls + '.cs')) -Value (Write-RecordedTestCs $map $cls $name) -Encoding UTF8
                                $imported += $name
                                if ($fmap.ContainsKey($name)) { $folders[$name] = $fmap[$name] }
                            } catch { }
                        }
                    }
                }
                # merge folder assignments into import-folders.json (drives the sidebar grouping)
                $ifPath = Join-Path $ResultsDir 'import-folders.json'
                $existing = @{}
                if (Test-Path $ifPath) { try { (Get-Content $ifPath -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $existing[$_.Name] = $_.Value } } catch { } }
                foreach ($k in $folders.Keys) { $existing[$k] = $folders[$k] }
                ($existing | ConvertTo-Json) | Set-Content $ifPath -Encoding UTF8
                $payload = [pscustomobject]@{ ok = $true; imported = @($imported); count = $imported.Count } | ConvertTo-Json -Depth 4
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json; charset=utf-8'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/export/ranorex' -and $req.HttpMethod -eq 'POST') {
                # export a test (our recording map) -> Ranorex .rxrec + .rxrep (no evidence)
                $t = $req.QueryString['test']
                $mf = Join-Path $ResultsDir ("recording-$t.json")
                $exp = Join-Path $PocDir '..\scripts\export-ranorex.py'
                $outdir = Join-Path $PocDir '..\generated\ranorex-export'
                if ($t -and (Test-Path $mf)) {
                    & python $exp $mf $outdir 2>&1 | Out-Null
                    $payload = [pscustomobject]@{ ok = $true; test = $t; rxrec = "$t.rxrec"; rxrep = "$t.rxrep" } | ConvertTo-Json
                } else {
                    $payload = '{"ok":false,"error":"no recording map for this test - record or import it first"}'
                }
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json; charset=utf-8'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/ranorex/file') {
                # download an exported Ranorex file
                $name = Split-Path ($req.QueryString['name']) -Leaf
                $f = Join-Path (Join-Path $PocDir '..\generated\ranorex-export') $name
                if ($name -and (Test-Path $f)) {
                    $buf = [IO.File]::ReadAllBytes($f)
                    $res.ContentType = 'application/octet-stream'
                    $res.AddHeader('Content-Disposition', "attachment; filename=`"$name`"")
                    $res.OutputStream.Write($buf, 0, $buf.Length)
                } else { $res.StatusCode = 404 }
            }
            elseif ($path -eq '/api/recording') {
                # the captured action map for a recorded test (Steps & Actions tab). Empty if not recorded.
                $t = $req.QueryString['test']
                $f = Join-Path $ResultsDir ("recording-$t.json")
                if ($t -and (Test-Path $f)) {
                    # effective (flattened) map so the tab shows inherited parent steps + this test's own, tagged
                    $eff = Get-EffectiveMap $t
                    $j = $eff | ConvertTo-Json -Depth 6; if ($j[0] -ne '[') { $j = "[$j]" }; $body = $j
                }
                else {
                    # fall back to parsing the generated test file (older tests have no saved map)
                    $arr = Get-RecordingFromCs $t
                    if (-not $arr -or $arr.Count -eq 0) { $body = '[]' }
                    else { $j = $arr | ConvertTo-Json -Depth 5; if ($j[0] -ne '[') { $j = "[$j]" }; $body = $j }
                }
                $buf = [Text.Encoding]::UTF8.GetBytes($body)
                $res.ContentType = 'application/json; charset=utf-8'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/regenerate' -and $req.HttpMethod -eq 'POST') {
                # rebuild a recorded test's .cs from its saved map using the current (robust) replay
                $t = $req.QueryString['test']
                $mapFile = Join-Path $ResultsDir ("recording-$t.json")
                $result = '{"ok":false,"error":"no saved map for this test"}'
                if ($t -and (Test-Path $mapFile)) {
                    try {
                        # flatten the extends chain (parent steps + this test's diff) and regenerate, cascading to children
                        if (Regen-RecordedTest $t) {
                            $cls = ($t.Substring(0,1).ToUpper() + $t.Substring(1)) + 'Test'
                            $csPath = Join-Path $PocDir ($cls + '.cs')
                            $n = (Get-EffectiveMap $t | Measure-Object).Count
                            $result = '{"ok":true,"file":"' + ($csPath -replace '\\','\\') + '","steps":' + $n + '}'
                        }
                    } catch { $result = '{"ok":false,"error":"' + ($_.Exception.Message -replace '"','\"') + '"}' }
                }
                $buf = [Text.Encoding]::UTF8.GetBytes($result)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/uiversion') {
                # the current UI build id (main bundle hash) - the app polls this and auto-reloads on change
                $v = ''
                try { $idx = Get-Content (Join-Path $ngDist 'index.html') -Raw; $v = [regex]::Match($idx, 'main-[A-Za-z0-9]+\.js').Value } catch { }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"v":"' + $v + '"}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/flow') {
                # The Flow Capture studio's OWN stream (flow-capture.json) - separate from the test-run
                # channel, so a long-lived studio session never looks like a running test in the suite.
                $ff = Join-Path $ResultsDir 'flow-capture.json'
                $flow = $null
                try { if (Test-Path $ff) { $flow = Get-Content $ff -Raw -Encoding UTF8 | ConvertFrom-Json } } catch { }
                $steps = @(); if ($flow -and $flow.steps) { $steps = $flow.steps }
                $title = if ($flow -and $flow.title) { $flow.title } else { 'Flow Capture' }
                $payload = [pscustomobject]@{ runState = 'idle'; title = $title; steps = $steps; report = ''; test = '' } | ConvertTo-Json -Depth 6
                $buf = [Text.Encoding]::UTF8.GetBytes($payload)
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/creator/start' -and $req.HttpMethod -eq 'POST') {
                # Launch the driver if it isn't already serving a HEALTHY app. A zombie (host alive, app dead)
                # answers HTTP but reports ok:false - kill it and relaunch so we don't sit on a dead app.
                $alive = Test-DriverHealthy
                if (-not $alive) { Stop-Driver; Start-Sleep -Milliseconds 1200; Start-Driver }
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true,"already":' + ($alive.ToString().ToLower()) + '}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -eq '/api/creator/restart' -and $req.HttpMethod -eq 'POST') {
                # Restart cleanly: force-kill the driver HOST (dotnet/testhost by command line) + Exatouch, then
                # relaunch with the latest build. This reliably clears a zombie driver with a dead app.
                Stop-Driver
                Start-Sleep -Milliseconds 1500
                Start-Driver
                $buf = [Text.Encoding]::UTF8.GetBytes('{"ok":true,"restarted":true}')
                $res.ContentType = 'application/json'; $res.OutputStream.Write($buf, 0, $buf.Length)
            }
            elseif ($path -like '/report/*') {
                $file = Join-Path $ResultsDir (Split-Path $path -Leaf)
                if (Test-Path $file) {
                    $buf = [IO.File]::ReadAllBytes($file)
                    $res.ContentType = 'text/html'; $res.OutputStream.Write($buf, 0, $buf.Length)
                } else { $res.StatusCode = 404 }
            }
            else { $res.StatusCode = 404 }
        } catch { try { $res.StatusCode = 500 } catch { } }
        # close the response defensively: if the client already disconnected (timed-out fetch/curl),
        # OutputStream.Close() throws HttpListenerException - that must NOT crash the whole server loop.
        try { $res.OutputStream.Close() } catch { }
    }
} finally { try { $listener.Stop() } catch { } }
