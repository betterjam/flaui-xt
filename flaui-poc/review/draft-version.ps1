<#
.SYNOPSIS
  Draft the NEXT version of a test that incorporates accepted AI-review improvements. Produces a
  STAGED new .cs file (not yet live) so the UI can show a diff and let the user Apply or Discard.

.DESCRIPTION
  Tests are versioned by FILE COPY: OpenItemTest.cs (v1) -> OpenItemTest.v2.cs (v2) -> .v3.cs ...
  Each version is a self-contained NUnit fixture: the class and the [Test] method get a _vN suffix
  so every version is independently runnable/reviewable and they don't collide.

  Flow:
    1. Find the latest version of -Test (vN) and its source file.
    2. Ask `claude` to rewrite it as v(N+1): keep it a valid, compiling FlaUI/NUnit test, change
       ONLY what's needed to implement the accepted improvements, rename class+method+LiveLog title
       with the _v(N+1) suffix, point report/output names at the new version.
    3. Write the draft to TestResults/staging/<NewFile> (NOT into the test project yet) +
       a version-draft-<Test>.json manifest {test, fromVersion, toVersion, file, newMethod,
       accepted[], summary, status}. server's Apply step copies it live.
  State: version-draft-<Test>-state.json {running, phase, at}.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Test,            # base test method (any version; we find latest)
    [string]$AcceptedJson,                          # JSON array of accepted suggestion strings (inline)
    [string]$AcceptedFile,                          # OR a file containing that JSON array (preferred -
                                                    #   avoids passing brackets/quotes as a CLI arg, which
                                                    #   Start-Job arg-marshalling mangles)
    [string]$FeedbackFile,                          # JSON array of lessons from PRIOR failed attempts
                                                    #   (the AI learns from what went wrong last time)
    [string]$PocDir,
    [string]$Model = 'claude-opus-4-8'
)
$ErrorActionPreference = 'Stop'
$reviewDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path $MyInvocation.MyCommand.Path -Parent }
if (-not $PocDir) { $PocDir = Split-Path $reviewDir -Parent }
$results = Join-Path $PocDir 'TestResults'
$staging = Join-Path $results 'staging'
New-Item -ItemType Directory -Force -Path $staging | Out-Null

. (Join-Path $reviewDir 'versioning-lib.ps1')   # shared: Get-TestVersions, Get-BaseMethod, etc.

$base    = Get-BaseMethod $Test                  # strip any _vN -> base method name
$state   = Join-Path $results "version-draft-$base-state.json"
$draftOut= Join-Path $results "version-draft-$base.json"
function Mark($phase) { @{ running = ($phase -ne 'idle'); phase = $phase; at = (Get-Date).ToString('u') } |
    ConvertTo-Json | Set-Content $state -Encoding utf8 }
function DLog($m) { "[{0}] draft {1}: {2}" -f (Get-Date -Format 'HH:mm:ss'), $base, $m |
    Add-Content (Join-Path $results 'version-draft.log') }

Mark 'drafting'; DLog "start (accepted=$AcceptedJson)"

# --- locate latest version + its source file --------------------------------
$latest = Get-LatestVersion -PocDir $PocDir -BaseMethod $base    # {version, method, file}
$srcFile = $latest.file
$curVer  = [int]$latest.version
$newVer  = $curVer + 1
$newMethod = "${base}_v$newVer"
$newFileName = ([IO.Path]::GetFileNameWithoutExtension($latest.baseFile)) + ".v$newVer.cs"
DLog "latest v$curVer in $([IO.Path]::GetFileName($srcFile)) -> drafting v$newVer ($newMethod)"

# accepted suggestions: prefer the file (clean path arg) over inline JSON
$acceptedRaw = if ($AcceptedFile -and (Test-Path $AcceptedFile)) { Get-Content $AcceptedFile -Raw } else { $AcceptedJson }
$accepted = @()
try { $accepted = @($acceptedRaw | ConvertFrom-Json) } catch { DLog "accepted parse failed: $($_.Exception.Message)" }
DLog "accepted=$($accepted.Count) item(s)"
$src = Get-Content $srcFile -Raw

# --- compose the rewrite prompt ---------------------------------------------
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine(@"
You are a senior SDET. Produce a NEW VERSION (v$newVer) of the C# NUnit + FlaUI test below. The new
version is the ORIGINAL test PLUS the accepted improvements - it is an EXTENSION, not a rewrite.
Output ONLY the complete new .cs file content - no prose, no markdown fences.

#1 RULE - PRESERVE THE WHOLE ORIGINAL FLOW:
- Keep EVERY existing step the original performs, in the SAME ORDER, doing the SAME things. The new
  version must still do everything v$curVer did (open the app, navigate, edit each field/tab, save,
  reopen, verify - whatever the original did) and THEN add the improvements. Do NOT shorten, skip,
  collapse, or "jump straight to the new behaviour". A reader diffing v$curVer -> v$newVer should see
  only ADDED lines for the improvements, with the original body intact.
- The accepted improvements are ADDED checks/steps layered onto that full flow, not a replacement for it.

OTHER HARD RULES (must compile and run side-by-side with the original):
- Rename the test class to end with _v$newVer (e.g. Foo -> Foo_v$newVer). Keep the same namespace.
- Rename the [Test] method to EXACTLY: $newMethod
- If a LiveLog title / report file / output file name encodes the test name, suffix it with v$newVer
  so this version writes its OWN report and does not overwrite v$curVer's.
- Keep every using/import, the [TestFixture], setup/teardown, AND every existing helper method intact.
- Do NOT weaken or remove any existing assertion. Only add/strengthen.
- NEVER commit a PASS status early. Do NOT call SetStatus("PASS") / _live.Finish("PASS") until AFTER
  every assertion and check has run and held. A "gate" that streams PASS and is then followed by more
  assertions is a BUG (premature PASS): if a later assertion throws, the test ends FAIL having already
  reported PASS. The single PASS commit must be the LAST thing the test does, once everything verified.
- Stay within FlaUI's real API as used in the original (real-mouse .Click(), AutomationId lookups,
  the LiveLog _live.AddStep(...) streaming pattern, screenshot capture). Do not invent app controls
  that aren't referenced in the original or obviously analogous.
- If the file uses a FrameGuard / Step(...) capture helper, reuse it the same way for any new steps so
  new screenshots are de-duplicated and shown in context.

ACCEPTED IMPROVEMENTS TO ADD (on top of the full original flow):
"@)
$i = 1; foreach ($a in $accepted) { [void]$sb.AppendLine("  $i. $a"); $i++ }

# feedback from prior failed attempts: the AI must learn from these and NOT repeat them
$feedback = @()
if ($FeedbackFile -and (Test-Path $FeedbackFile)) { try { $feedback = @(Get-Content $FeedbackFile -Raw | ConvertFrom-Json) } catch { } }
if ($feedback.Count) {
    [void]$sb.AppendLine("`nLESSONS FROM PRIOR FAILED ATTEMPTS (you MUST address these - do not repeat the same mistake):")
    $k = 1; foreach ($fb in $feedback) { [void]$sb.AppendLine("  - $fb"); $k++ }
    DLog "including $($feedback.Count) feedback lesson(s) from prior attempts"
}

[void]$sb.AppendLine("`n===== ORIGINAL (v$curVer) FILE: $([IO.Path]::GetFileName($srcFile)) =====")
[void]$sb.AppendLine($src)

Mark 'generating'; DLog "invoking claude (prompt $([math]::Round($sb.Length/1KB)) KB)"
$raw = $sb.ToString() | & claude -p --output-format json --model $Model 2>&1
$code = $null
try { $code = ($raw | ConvertFrom-Json).result } catch { $code = ($raw -join "`n") }
# strip accidental markdown fences if any
$code = [regex]::Replace($code, '(?s)^.*?```(?:csharp|cs)?\s*', '')
$code = [regex]::Replace($code, '(?s)```\s*$', '')
if ($code -notmatch 'class\s+\w+_v' + $newVer) {
    # model didn't follow the rename - fail loudly rather than write a colliding file
    Mark 'idle'; DLog "ERROR: draft missing _v$newVer class rename"
    @{ test=$base; status='error'; error="The agent did not produce a valid v$newVer class."; fromVersion=$curVer; toVersion=$newVer } |
        ConvertTo-Json | Set-Content $draftOut -Encoding utf8
    return
}

$stageFile = Join-Path $staging $newFileName
$code | Set-Content $stageFile -Encoding utf8
DLog "staged $newFileName ($([math]::Round((Get-Item $stageFile).Length/1KB)) KB)"

@{
    test        = $base
    fromVersion = $curVer
    toVersion   = $newVer
    fromFile    = [IO.Path]::GetFileName($srcFile)
    file        = $newFileName          # staged file name (under staging/)
    newMethod   = $newMethod
    accepted    = $accepted
    status      = 'staged'              # staged -> (Apply) -> applied
    at          = (Get-Date).ToString('u')
} | ConvertTo-Json -Depth 6 | Set-Content $draftOut -Encoding utf8

Mark 'idle'; DLog "draft ready (v$curVer -> v$newVer)"
