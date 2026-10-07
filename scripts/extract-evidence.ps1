<#
.SYNOPSIS
  Extract code-grounded, FORM-BASED behavior anchors for a feature. No AI.

.DESCRIPTION
  Enforces "tests come from code" and "work from the forms: actions, flows, features."
  For each form (frm*.cs) in the feature, it pairs the code-behind with its .Designer.cs and
  extracts the full ACTION SURFACE as typed anchors. Every anchor is a testable behavior the
  discovery agent must cover:

    message    - Lib.Warning/Success/Error/Dialog literal      (observable outcome)
    action     - a handler: btn*/mnu*/lnk*/tab* *Click, ucNavOnSave, *CellClick (a user action)
    toggle     - chk*/cbo* CheckedChanged/SelectedIndexChanged   (a settings option that changes behavior)
    permission - X.Access = Global.StaffSecurity.*               (a silent role gate)
    calc       - tax/total/discount/cost computation hints       (value behavior, no message)

  Designer files supply each control's user-visible Text label (so anchors carry the button
  name a tester sees). Output groups anchors by form = the form's actions/flows/features.

.OUTPUTS
  qa-ai\catalog\_baseline\evidence-<Feature>.json   (forms[] each with typed anchors[])
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Feature,
    [string[]]$SourcePaths,                       # default 2.0.x\<Feature>; override for fuzzy names
    [string]$AppRoot = (Resolve-Path (Join-Path (&{ if ($env:EXATOUCH_ROOT) { $env:EXATOUCH_ROOT } else { 'C:\Dev\Exatouch' } }) '2.0.x')).Path,
    [string]$OutDir  = (Join-Path $PSScriptRoot '..\catalog\_baseline')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
if (-not $SourcePaths) { $SourcePaths = @((Join-Path $AppRoot $Feature)) }
$repoRoot = Split-Path $AppRoot -Parent

# gather code-behind forms (+ loose .cs), excluding designer files
$codeFiles = @()
foreach ($p in $SourcePaths) {
    $full = if ([IO.Path]::IsPathRooted($p)) { $p } else { Join-Path $repoRoot $p }
    if (Test-Path $full) {
        $codeFiles += Get-ChildItem $full -Recurse -Filter '*.cs' -File |
                      Where-Object { $_.Name -notlike '*.Designer.cs' -and $_.Name -notlike '*.designer.cs' }
    } else { Write-Warning "Source path not found: $full" }
}

function Get-DesignerLabels([string]$codePath) {
    $labels = @{}
    foreach ($suffix in '.Designer.cs','.designer.cs') {
        $d = [IO.Path]::ChangeExtension($codePath, $null).TrimEnd('.') + $suffix
        if (Test-Path $d) {
            foreach ($m in [regex]::Matches((Get-Content $d -Raw), 'this\.(\w+)\.Text\s*=\s*"([^"]+)"')) {
                $labels[$m.Groups[1].Value] = $m.Groups[2].Value
            }
        }
    }
    return $labels
}

$forms = @()
$counts = @{ message=0; action=0; toggle=0; permission=0; calc=0 }

foreach ($f in $codeFiles) {
    $rel = $f.FullName.Substring($repoRoot.Length).TrimStart('\','/')
    $labels = Get-DesignerLabels $f.FullName
    $lines = Get-Content $f.FullName
    $anchors = @()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $ln = $lines[$i]; $n = $i + 1

        # message
        $m = [regex]::Match($ln, 'Lib\.(Warning|Success|Error|Dialog)\s*\(\s*(?:[^,"]*,\s*)?[\$@]*"([^"]+)"')
        if ($m.Success) { $anchors += [pscustomobject]@{ type='message'; line=$n; kind=$m.Groups[1].Value; text=$m.Groups[2].Value }; $counts.message++; continue }

        # action handler (button/menu/link/tab/grid + save)
        $h = [regex]::Match($ln, '(?:void|public)\s+((?:btn|mnu|lnk|tab|dgv|grd|tsm|tsb)\w*(?:Click|DoubleClick|CellClick|CellContentClick)|ucNavOnSave|\w*OnSave)\s*\(')
        if ($h.Success) {
            $ctrl = ($h.Groups[1].Value -replace '(Click|DoubleClick|CellClick|CellContentClick|OnSave)$','')
            $anchors += [pscustomobject]@{ type='action'; line=$n; handler=$h.Groups[1].Value; control=$ctrl; label=$labels[$ctrl] }
            $counts.action++; continue
        }

        # toggle (settings option)
        $t = [regex]::Match($ln, '(?:void|public)\s+((?:chk|cbo|rdo|opt)\w*(?:CheckedChanged|SelectedIndexChanged|Changed))\s*\(')
        if ($t.Success) {
            $ctrl = ($t.Groups[1].Value -replace '(CheckedChanged|SelectedIndexChanged|Changed)$','')
            $anchors += [pscustomobject]@{ type='toggle'; line=$n; handler=$t.Groups[1].Value; control=$ctrl; label=$labels[$ctrl] }
            $counts.toggle++; continue
        }

        # permission gate (silent role check)
        $p = [regex]::Match($ln, '(\w+)\.Access\s*=\s*Global\.StaffSecurity\.([\w.]+)')
        if ($p.Success) {
            $anchors += [pscustomobject]@{ type='permission'; line=$n; control=$p.Groups[1].Value; right=$p.Groups[2].Value.TrimEnd(';'); label=$labels[$p.Groups[1].Value] }
            $counts.permission++; continue
        }

        # calc hint (tax/total/discount/cost math producing a value)
        if ($ln -match '\b(Tax|Total|Discount|Cost|Markup|Price|Subtotal|Gratuity|Tip)\b\s*=' -and $ln -notmatch '//' -and $ln -match '[*/+\-]') {
            $anchors += [pscustomobject]@{ type='calc'; line=$n; expr=($ln.Trim() -replace '\s+',' ' | ForEach-Object { if ($_.Length -gt 120) { $_.Substring(0,120) } else { $_ } }) }
            $counts.calc++
        }
    }

    if ($anchors.Count) {
        $forms += [pscustomobject]@{
            form = [IO.Path]::GetFileNameWithoutExtension($f.Name)
            file = $rel
            anchorCount = $anchors.Count
            anchors = $anchors
        }
    }
}

$evidence = [pscustomobject]@{
    feature = $Feature
    sourcePaths = $SourcePaths
    formCount = $forms.Count
    anchorTotal = ($forms | Measure-Object -Property anchorCount -Sum).Sum
    byType = $counts
    forms = $forms
}
$outFile = Join-Path $OutDir "evidence-$Feature.json"
$evidence | ConvertTo-Json -Depth 8 | Set-Content $outFile -Encoding utf8

Write-Host "Evidence for '$Feature' (form-based):" -ForegroundColor Cyan
Write-Host ("  forms      : {0}" -f $forms.Count)
Write-Host ("  anchors    : {0}" -f $evidence.anchorTotal)
Write-Host ("    message    {0}" -f $counts.message)
Write-Host ("    action     {0}" -f $counts.action)
Write-Host ("    toggle     {0}" -f $counts.toggle)
Write-Host ("    permission {0}" -f $counts.permission)
Write-Host ("    calc       {0}" -f $counts.calc)
Write-Host "  -> $outFile"
