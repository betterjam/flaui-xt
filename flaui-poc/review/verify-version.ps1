<#
.SYNOPSIS
  Judge whether an APPLIED test version actually ACHIEVED the accepted improvements - and, if not,
  drive the self-correcting loop (discard + re-draft with the failure reason as FEEDBACK, so the AI
  learns from what went wrong) up to a max number of attempts.

.DESCRIPTION
  Called after a new version was applied + auto-run. Steps:
    1. Read the accepted improvements (what we wanted) + this version's run evidence (run-<Method>.json).
    2. Ask `claude` for a strict-JSON VERDICT: { achieved: bool, reason, missing[] }.
    3. Write TestResults/verdict-<Method>.json (the UI banner reads this).
    4. If NOT achieved AND attempt < MaxAttempts:
         - append the failure reason to the family's feedback log (so the next draft sees it),
         - DISCARD this version (delete its .cs + drop it from the family),
         - re-draft the next attempt from the PREVIOUS good version, passing the accumulated feedback,
         - apply + run that attempt, then recurse (verify again).
       The AI thus learns from each miss and tries again until it achieves the goal or runs out of tries.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Method,   # the applied versioned method (e.g. Foo_v2)
    [int]$Attempt = 1,
    [int]$MaxAttempts = 3,
    [string]$PocDir,
    [string]$Model = 'claude-opus-4-8'
)
$ErrorActionPreference = 'Stop'
$reviewDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path $MyInvocation.MyCommand.Path -Parent }
if (-not $PocDir) { $PocDir = Split-Path $reviewDir -Parent }
$results = Join-Path $PocDir 'TestResults'
. (Join-Path $reviewDir 'versioning-lib.ps1')

$base   = Get-BaseMethod $Method
$verNum = [int](&{ $m = [regex]::Match($Method, '_v(\d+)$'); if ($m.Success) { $m.Groups[1].Value } else { 1 } })
$verdictFile = Join-Path $results "verdict-$base.json"
$feedbackFile = Join-Path $results "version-feedback-$base.json"
function VLog($m) { "[{0}] verify {1} (attempt {2}/{3}): {4}" -f (Get-Date -Format 'HH:mm:ss'), $Method, $Attempt, $MaxAttempts, $m |
    Add-Content (Join-Path $results 'verify.log') }

VLog "start"

# what we wanted: the accepted improvements that produced this version
$accepted = @()
$accFile = Join-Path $results "version-draft-$base-input.json"
if (Test-Path $accFile) { try { $accepted = @(Get-Content $accFile -Raw | ConvertFrom-Json) } catch { } }

# the evidence: this version's run + its source
$runFile = Join-Path $results "run-$Method.json"
$run = if (Test-Path $runFile) { Get-Content $runFile -Raw } else { '(no run evidence)' }
# the AUTHORITATIVE overall run outcome. A version whose run FAILED can NEVER be "achieved",
# no matter what a mid-run "gate" streamed - the test as a whole did not pass.
$runStatus = ''
if (Test-Path $runFile) { try { $runStatus = (Get-Content $runFile -Raw | ConvertFrom-Json).status } catch { } }
$runFailed = ($runStatus -eq 'FAIL')
VLog "overall run status = '$runStatus' (failed=$runFailed)"
$srcRaw = ''
foreach ($cs in Get-ChildItem $PocDir -Filter '*.cs' -File) {
    $r = Get-Content $cs.FullName -Raw
    if ($r -match [regex]::Escape($Method)) { $srcRaw = $r; break }
}

# ============================================================================
# DETERMINISTIC GATE (decides achieved - NO AI). The test's own evidence is the
# source of truth: a test that did not run to completion / did not end PASS cannot
# be "achieved", whatever an AI might infer. Facts decide; the AI only explains.
# ============================================================================
$liveObj = $null
try { if (Test-Path $runFile) { $liveObj = Get-Content $runFile -Raw | ConvertFrom-Json } } catch { }
$steps = @(); if ($liveObj -and $liveObj.steps) { $steps = @($liveObj.steps) }

$detFail = $null   # if set, the version deterministically FAILED for this reason

# 1) overall run outcome must be PASS
if ($runStatus -ne 'PASS') { $detFail = "the run did not pass (overall status = '$runStatus')." }

# 2) the test must run to COMPLETION. We infer completeness from the streamed steps:
#    a finished test's last step reads like a verification/summary; an aborted one stops mid-flow.
#    Deterministic signals of an incomplete/aborted run in the step text:
if (-not $detFail) {
    $stepText = ($steps | ForEach-Object { "$($_.text)" }) -join "`n"
    if ($steps.Count -eq 0) { $detFail = "no steps were recorded - the test did not run." }
    elseif ($stepText -match '(?im)\b(exception|threw|error|stuck|interrupted|did not change|no new screen|timeout|aborted)\b') {
        $hit = [regex]::Match($stepText, '(?im).*\b(exception|threw|error|stuck|interrupted|did not change|no new screen|timeout|aborted)\b.*').Value.Trim()
        $detFail = "the run did not complete cleanly - a step reports a failure/abort: `"$hit`"."
    }
}

# 3) premature-PASS defect: PASS was streamed but the run still ended FAIL, or PASS is not the LAST
#    thing that happened (steps continued after a PASS was emitted).
if (-not $detFail) {
    # (only meaningful when status==PASS, handled above; this catches FAIL-after-PASS explicitly)
}
if ($runStatus -eq 'FAIL' -and ($steps | Where-Object { "$($_.text)" -match '(?i)\bPASS\b' })) {
    $detFail = "premature PASS: the test streamed PASS before a later step failed - the run ended FAIL. Move the PASS commit to AFTER all assertions."
}

$deterministicAchieved = ($null -eq $detFail)
$detMsg = if ($detFail) { "reason=$detFail" } else { '' }
VLog "DETERMINISTIC gate: achieved=$deterministicAchieved $detMsg"

# --- AI ONLY EXPLAINS (cannot override the machine decision) -----------------
# We hand the AI the machine verdict and ask it to (a) write a human reason and (b) when NOT achieved,
# turn the failure into concrete next-attempt instructions. It may NOT flip a deterministic fail to pass.
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine(@"
You are a senior QA lead writing up a test-version verdict. The PASS/FAIL decision is ALREADY MADE
deterministically from the run evidence - you must NOT change it, only explain it and (if it failed)
say exactly how to fix it next time.

MACHINE VERDICT (authoritative, do not contradict): achieved = $deterministicAchieved
Overall run status: $runStatus.  Steps recorded: $($steps.Count).
$(if ($detFail) { "Deterministic failure reason: $detFail" } else { "The run passed and ran to completion." })

Output STRICT JSON only:
{
  "added": "one short sentence: what THIS version ADDED on top of v$($verNum-1) (the improvement made), phrased as a capability, e.g. 'Now walks all 10 tabs and verifies each field, not just the Details tab.'",
  "reason": "one or two sentences explaining the verdict (did it work?), consistent with the machine verdict above",
  "missing": [ "if not achieved: each concrete fix for the next attempt; if achieved: [] " ]
}

THE IMPROVEMENTS WE WANTED (this is what was ADDED vs the previous version):
"@)
$i = 1; foreach ($a in $accepted) { [void]$sb.AppendLine("  $i. $a"); $i++ }
[void]$sb.AppendLine("`n===== THE NEW VERSION'S SOURCE ($Method) =====`n$srcRaw")
[void]$sb.AppendLine("`n===== ITS ACTUAL RUN EVIDENCE (run-$Method.json) =====`n$run")

VLog "invoking claude for explanation (prompt $([math]::Round($sb.Length/1KB)) KB)"
$raw = $sb.ToString() | & claude -p --output-format json --model $Model 2>&1
$txt = $null
try { $txt = ($raw | ConvertFrom-Json).result } catch { $txt = ($raw -join "`n") }
$m = [regex]::Match($txt, '(?s)\{.*\}'); if ($m.Success) { $txt = $m.Value }
$expl = $null
try { $expl = $txt | ConvertFrom-Json } catch { }

# build reason/missing - prefer the AI's prose, but the DECISION is the deterministic one.
$reason = if ($detFail) { "Not achieved: $detFail" } else { 'The run passed and ran to completion.' }
if ($expl -and $expl.reason) { $reason = "$($expl.reason)" }
$missing = @()
if (-not $deterministicAchieved) {
    if ($expl -and $expl.missing) { $missing = @($expl.missing) }
    if (-not $missing.Count) { $missing = @($detFail) }   # always carry at least the machine reason forward
}

# "what was ADDED vs the previous version": prefer the AI's one-liner, fall back to the raw accepted
# improvements (deterministic - exactly what the user accepted). Clean the {value,Count} unwrap.
$addedSummary = if ($expl -and $expl.added) { "$($expl.added)" } else { '' }
$acceptedClean = @()
foreach ($a in $accepted) {
    if ($a -is [string]) { $acceptedClean += $a }
    elseif ($a.value) { $acceptedClean += "$($a.value)" }
    else { $acceptedClean += "$a" }
}
if (-not $addedSummary -and $acceptedClean.Count) { $addedSummary = ($acceptedClean -join ' ') }

# stamp the verdict - achieved is the DETERMINISTIC value, never the AI's opinion
$out = [ordered]@{
    method        = $Method
    base          = $base
    achieved      = $deterministicAchieved          # <-- machine-decided
    deterministic = $true
    runStatus     = "$runStatus"
    steps         = $steps.Count
    failReason    = "$detFail"                       # the machine reason (empty if achieved)
    confidence    = $(if ($deterministicAchieved) { 100 } else { 100 })   # deterministic = certain
    fromVersion   = $verNum - 1
    added         = $addedSummary                    # what this version added on top of the previous
    acceptedItems = @($acceptedClean)                # the literal accepted improvement(s)
    reason        = $reason
    missing       = @($missing)
    attempt       = $Attempt
    maxAttempts   = $MaxAttempts
    retrying      = $false
    at            = (Get-Date).ToString('u')
}
VLog ("verdict (deterministic) achieved={0} status={1} steps={2} missing={3}" -f $out.achieved, $runStatus, $steps.Count, $out.missing.Count)

# --- self-correcting loop ---------------------------------------------------
if (-not $out.achieved -and $Attempt -lt $MaxAttempts) {
    $out.retrying = $true
    ($out | ConvertTo-Json -Depth 6) | Set-Content $verdictFile -Encoding utf8
    VLog "NOT achieved -> auto-discard + re-draft (learning from the miss)"

    # accumulate feedback so the next draft learns from every failed attempt
    $feedback = @()
    if (Test-Path $feedbackFile) { try { $feedback = @(Get-Content $feedbackFile -Raw | ConvertFrom-Json) } catch { } }
    $feedback += "Attempt $Attempt produced '$Method' but it did NOT achieve the goal. Reason: $($out.reason). Still missing: $(($out.missing) -join '; '). In the next version, specifically fix this."
    ($feedback | ConvertTo-Json) | Set-Content $feedbackFile -Encoding utf8

    # DISCARD the broken version COMPLETELY - we never keep a failed version. Remove its .cs from the
    # project AND its run/verdict artifacts, so Get-LatestVersion drops back to the previous GOOD
    # version. The re-draft then RE-CREATES THE SAME version slot (not a new abandoned number) from
    # (previous good version + the accumulated error feedback).
    $verNum = [int]([regex]::Match($Method, '_v(\d+)$').Groups[1].Value)
    Get-ChildItem $PocDir -Filter "*.v$verNum.cs" -File | ForEach-Object {
        if ((Get-Content $_.FullName -Raw) -match [regex]::Escape($Method)) {
            VLog "removing broken version file $($_.Name)"
            Remove-Item $_.FullName -Force -EA SilentlyContinue
        }
    }
    # drop the broken version's run artifacts so nothing lingers in the suite
    Remove-Item (Join-Path $results "run-$Method.json") -Force -EA SilentlyContinue
    Remove-Item (Join-Path $results "review-$Method.json") -Force -EA SilentlyContinue
    Remove-Item (Join-Path $results "review-$Method-state.json") -Force -EA SilentlyContinue

    # re-draft the SAME version slot from the previous good version, with accepted + feedback
    $drv = Join-Path $reviewDir 'draft-version.ps1'
    & powershell -NoProfile -ExecutionPolicy Bypass -File $drv -Test $base -AcceptedFile $accFile -FeedbackFile $feedbackFile -PocDir $PocDir 2>&1 | Out-Null

    # find the freshly-staged draft, apply it, run it
    $manifest = Join-Path $results "version-draft-$base.json"
    if (Test-Path $manifest) {
        $d = Get-Content $manifest -Raw | ConvertFrom-Json
        $stage = Join-Path $results "staging\$($d.file)"
        $live  = Join-Path $PocDir $d.file
        if ($d.status -eq 'staged' -and (Test-Path $stage)) {
            Copy-Item $stage $live -Force
            $d | Add-Member -NotePropertyName status -NotePropertyValue 'applied' -Force
            $d | ConvertTo-Json -Depth 6 | Set-Content $manifest -Encoding utf8
            $newMethod = $d.newMethod
            VLog "re-applied as $newMethod -> run + recurse verify"
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PocDir 'run-tests.ps1') -Filter $newMethod -PocDir $PocDir 2>&1 | Out-Null
            & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $reviewDir 'verify-version.ps1') -Method $newMethod -Attempt ($Attempt+1) -MaxAttempts $MaxAttempts -PocDir $PocDir 2>&1 | Out-Null
            return   # the recursive call writes the final verdict
        }
    }
    VLog "re-draft did not produce a staged version; stopping the loop"
    $out.retrying = $false
}

# If we get here NOT achieved, the loop is over (attempts exhausted or re-draft failed). RULE: never
# keep a broken version. Roll the family BACK to the last good version - delete this failed version's
# .cs + artifacts so the family sits at the previous good version, and record that we rolled back.
if (-not $out.achieved -and -not $out.retrying) {
    $verNum = [int]([regex]::Match($Method, '_v(\d+)$').Groups[1].Value)
    if ($verNum -gt 1) {
        VLog "attempts exhausted - rolling back: deleting broken $Method (family returns to v$($verNum-1))"
        Get-ChildItem $PocDir -Filter "*.v$verNum.cs" -File | ForEach-Object {
            if ((Get-Content $_.FullName -Raw) -match [regex]::Escape($Method)) { Remove-Item $_.FullName -Force -EA SilentlyContinue }
        }
        Remove-Item (Join-Path $results "run-$Method.json") -Force -EA SilentlyContinue
        Remove-Item (Join-Path $results "review-$Method.json") -Force -EA SilentlyContinue
        $rolledBackTo = $verNum - 1
    }
    $out.rolledBack = $true
    $out | Add-Member -NotePropertyName rolledBackToVersion -NotePropertyValue $rolledBackTo -Force -EA SilentlyContinue
    $out.reason = "Could not achieve the improvement after $($out.attempt) attempts. Rolled back to the last working version (v$rolledBackTo) - no broken version is kept. $($out.reason)"
}

# achieved, or out of attempts -> write the final verdict
($out | ConvertTo-Json -Depth 6) | Set-Content $verdictFile -Encoding utf8
VLog "done (achieved=$($out.achieved) retrying=$($out.retrying) rolledBack=$($out.rolledBack))"
