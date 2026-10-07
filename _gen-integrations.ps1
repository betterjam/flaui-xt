<#
  Temp generator: emit atomic catalog cases for every Integrations anchor not yet
  cited in catalog/features/Integrations.yaml, preserving existing IDs. Writes the
  full regenerated YAML. Not part of the pipeline; deleted after use.
#>
[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

$evFile      = Join-Path $Root 'catalog\_baseline\evidence-Integrations.json'
$catalogFile = Join-Path $Root 'catalog\features\Integrations.yaml'
$ev = Get-Content $evFile -Raw | ConvertFrom-Json

# --- read existing catalog: cited file:line keys + max id + the existing text -----
$existingLines = Get-Content $catalogFile
$cited = @{}; $maxId = 0; $sect = $null
foreach ($ln in $existingLines) {
    if ($ln -match '^\s*-?\s*id:\s*INT-(\d+)') { $n=[int]$Matches[1]; if($n -gt $maxId){$maxId=$n}; $sect=$null }
    elseif ($ln -match '^\s*evidence:\s*$') { $sect='ev' }
    elseif ($sect -eq 'ev' -and $ln -match '\{\s*file:\s*(\S+?)\s*,\s*line:\s*(\d+)') {
        $cited[("{0}:{1}" -f $Matches[1].TrimEnd(',').Replace('\','/'), $Matches[2])] = $true
    }
    elseif ($ln -match '^\s*\w+:') { $sect=$null }
}
Write-Host "Existing cases up to INT-$($maxId.ToString('0000')); $($cited.Count) anchors already cited."

# --- helpers ----------------------------------------------------------------------
function YDq([string]$s) {
    # double-quoted YAML scalar: escape backslash, quote, and turn real CR/LF into \r \n
    if ($null -eq $s) { return '""' }
    $t = $s.Replace('\','\\').Replace('"','\"')
    $t = $t -replace "`r","\r" -replace "`n","\n" -replace "`t","\t"
    return '"' + $t + '"'
}
# evidence JSON stores newlines as the literal two chars backslash-n etc.; normalize the
# label display the same way the existing file does (\r\n -> space, for titles only).
function LabelDisplay([string]$s) {
    if (-not $s) { return $null }
    return ($s -replace '\\r\\n',' ' -replace '\\n',' ' -replace '\s+',' ').Trim()
}

$fp = "2.0.x/Integrations"  # forward-slash form used in the YAML
$out = New-Object System.Collections.Generic.List[string]
$id = $maxId

function NextId { $script:id++; return ("INT-{0:0000}" -f $script:id) }

$lastFormKey = $null
foreach ($frm in $ev.forms) {
    $form = $frm.form
    $file = $frm.file.Replace('\','/')   # 2.0.x/Integrations/.../frmX.cs
    # group entity = the subfolder under Integrations (FloorPlan/Kiosk/LCB/LMP/Messages/ScanData)
    $entity = ($file -split '/')[2]
    foreach ($a in $frm.anchors) {
        $key = "{0}:{1}" -f $file, $a.line
        if ($cited.ContainsKey($key)) { continue }   # preserve existing coverage

        $cid = NextId
        $lbl = LabelDisplay $a.label
        switch ($a.type) {
            'action' {
                $tk = 'happy-path'; $typ='e2e'; $run='ranorex'
                $titleAct = if ($lbl) { "{0} ({1})" -f $a.control, $lbl } else { $a.control }
                $title = "{0}: action {1}" -f $form, $titleAct
                $stepAct = if ($lbl) { "Activate {0} ({1})" -f $a.control, $lbl } else { "Activate {0}" -f $a.control }
                $steps = @("Open $form under Integrations", $stepAct, "Observe the resulting action")
                $expected = @("Handler $($a.handler) runs and the expected screen/state change occurs")
                $evText = $a.handler
                $newCtl = "$form action at line $($a.line)"
            }
            'message' {
                $typ='integration'; $run='nunit'
                if ($a.kind -eq 'Success') { $tk='happy-path' } else { $tk='validation' }
                $title = "{0}: {1} - {2}" -f $form, $a.kind, (LabelDisplay $a.text)
                if ($a.kind -eq 'Success') {
                    $steps = @("Open $form under Integrations", "Perform the action that succeeds", "Observe the confirmation message")
                } else {
                    $steps = @("Open $form under Integrations", "Perform the action that triggers the guard", "Observe the warning message")
                }
                $expected = @("Message shown: $(LabelDisplay $a.text)")
                $evText = $a.text
                $newCtl = "$form message at line $($a.line)"
            }
            'toggle' {
                $tk='setting-behavior'; $typ='integration'; $run='nunit'
                $title = "{0}: toggle {1}{2}" -f $form, $a.control, $(if($lbl){" ($lbl)"}else{""})
                $steps = @("Open $form under Integrations", "Toggle $($a.control)$(if($lbl){" ($lbl)"}else{""})", "Observe the setting-driven behavior")
                $expected = @("Handler $($a.handler) runs and the toggle state is persisted/applied")
                $evText = $a.handler
                $newCtl = "$form toggle at line $($a.line)"
            }
            'permission' {
                $tk='permission'; $typ='integration'; $run='nunit'
                $title = "{0}: permission {1} ({2})" -f $form, $a.control, $a.right
                $steps = @("Open $form under Integrations", "Attempt to use $($a.control)$(if($lbl){" ($lbl)"}else{""}) without right $($a.right)", "Observe access is gated by the permission")
                $expected = @("Access to $($a.control) is controlled by right $($a.right)")
                $evText = "perm:$($a.right)"
                $newCtl = "$form permission at line $($a.line)"
            }
            'calc' {
                $tk='calculation'; $typ='unit'; $run='nunit'
                $title = "{0}: calculation $($a.expr)" -f $form
                $steps = @("Set up inputs for the calculation in $form", "Invoke the calculation", "Compare the computed result")
                $expected = @("Computed value matches the formula: $($a.expr)")
                $evText = $a.expr
                $newCtl = "$form calc at line $($a.line)"
            }
        }

        # priority: Save/OK actions & Success/permission -> P1; warnings -> P2; rest P3
        $pri = 'P3'
        if ($a.type -eq 'message' -and $a.kind -eq 'Success') { $pri='P1' }
        elseif ($a.type -eq 'message') { $pri='P2' }
        elseif ($a.type -eq 'permission') { $pri='P1' }
        elseif ($a.type -eq 'toggle') { $pri='P2' }
        elseif ($a.type -eq 'action' -and ($a.handler -match 'btnOK|btnFinish|btnSave|OnSave|btnSendNow|btnMSS|btnEOD')) { $pri='P1' }

        # composes/reference_recordings: e2e actions reuse the shell recordings; nunit
        # cases are code-level and reference no recordings.
        if ($run -eq 'ranorex') {
            $composes = '[StartAUT, LoginAsAdminUser, GoToModule, CloseAUT]'
            $refrec   = '[StartAUT, LoginAsAdminUser, GoToModule]'
        } else {
            $composes = '[]'
            $refrec   = '[]'
        }

        $out.Add("  - id: $cid")
        $out.Add("    title: $(YDq $title)")
        $out.Add("    kind: atomic")
        $out.Add("    priority: $pri")
        $out.Add("    type: $typ")
        $out.Add("    flavors: []")
        $out.Add("    categories: { test_kind: $tk, ui_area: $form, entity: $entity }")
        $out.Add("    preconditions: [Logged in as admin]")
        $out.Add("    steps:")
        foreach ($s in $steps) { $out.Add("      - $(YDq $s)") }
        $out.Add("    expected:")
        foreach ($e in $expected) { $out.Add("      - $(YDq $e)") }
        $out.Add("    runner: $run")
        $out.Add("    composes: $composes")
        $out.Add("    reference_recordings: $refrec")
        $out.Add("    binding: needs-recording")
        $out.Add("    matches_existing: null")
        $out.Add("    new_controls: [$(YDq $newCtl)]")
        $out.Add("    source_files: [$file]")
        $out.Add("    evidence:")
        $out.Add("      - { file: $file, line: $($a.line), text: $(YDq $evText) }")
        $out.Add("    flow_hash: """"")
        $out.Add("    source_refs: { tickets: [], prs: [] }")
        $out.Add("    origin: discovery")
        $out.Add("")
    }
}

Write-Host "Generated $($script:id - $maxId) new atomic cases (INT-$(($maxId+1).ToString('0000'))..INT-$($script:id.ToString('0000')))."
# write fragment to a temp file for review/append
$fragFile = Join-Path $Root '_gen-integrations.fragment.yaml'
$out -join "`n" | Set-Content $fragFile -Encoding utf8 -NoNewline
Write-Host "Fragment: $fragFile"
