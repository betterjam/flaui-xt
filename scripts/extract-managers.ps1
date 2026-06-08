<#
.SYNOPSIS
  Extract testable logic anchors from Managers (tax/total/discount/calc + public methods). No AI.

.DESCRIPTION
  Forms miss behavior that lives in the static Managers layer (no UI): tax, totals, tips,
  discounts, fees, security rules. Each PUBLIC method there is an independently testable unit
  -> an NUnit `integration`/`unit` test, not a Ranorex e2e flow. This completes the coverage
  denominator: form anchors (e2e) + manager anchors (unit) = the whole app's testable surface.

  Anchor types emitted:
    method      - any public/internal method signature (a unit under test)
    calc-method - a public method whose name or body involves Tax/Total/Discount/Cost/Tip/Fee
                  (flagged P1: money math must be asserted)

.OUTPUTS
  qa-ai\catalog\_baseline\evidence-Managers.json   (managers[] each with methods[])
#>
[CmdletBinding()]
param(
    [string]$ManagersDir = (Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..\2.0.x')).Path 'Managers'),
    [string]$OutDir      = (Join-Path $PSScriptRoot '..\catalog\_baseline')
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

$calcWords = 'Tax|Total|Discount|Cost|Markup|Price|Subtotal|Gratuity|Tip|Fee|Refund|Change|Balance|Round'
$managers = @(); $methodCount = 0; $calcCount = 0

foreach ($f in (Get-ChildItem $ManagersDir -Filter '*Manager.cs' -File)) {
    $rel = $f.FullName.Substring($repoRoot.Length).TrimStart('\','/')
    $lines = Get-Content $f.FullName
    $methods = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        # public/internal method signature (exclude property getters and ctors loosely)
        $m = [regex]::Match($lines[$i], '^\s*(?:public|internal)\s+(?:static\s+)?(?:async\s+)?([A-Za-z_][\w<>,\[\]\.]*)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)')
        if (-not $m.Success) { continue }
        $ret = $m.Groups[1].Value; $name = $m.Groups[2].Value; $args = $m.Groups[3].Value
        if ($name -in @('get','set','class')) { continue }
        if ($ret -eq 'class') { continue }
        $isCalc = ($name -match $calcWords) -or ($args -match $calcWords) -or ($ret -match 'decimal')
        $methods += [pscustomobject]@{
            type = $(if ($isCalc) { 'calc-method' } else { 'method' })
            line = ($i+1); name = $name; returns = $ret
            signature = ("{0} {1}({2})" -f $ret, $name, ($args.Trim()))
        }
        $methodCount++; if ($isCalc) { $calcCount++ }
    }
    if ($methods.Count) {
        $managers += [pscustomobject]@{
            manager = $f.BaseName; file = $rel
            methodCount = $methods.Count; calcCount = ($methods | Where-Object { $_.type -eq 'calc-method' }).Count
            methods = $methods
        }
    }
}

$evidence = [pscustomobject]@{
    feature = 'Managers'
    managerCount = $managers.Count
    anchorTotal = $methodCount
    calcMethods = $calcCount
    managers = ($managers | Sort-Object methodCount -Descending)
}
$outFile = Join-Path $OutDir 'evidence-Managers.json'
$evidence | ConvertTo-Json -Depth 8 | Set-Content $outFile -Encoding utf8

Write-Host "Evidence for 'Managers' (logic/calc):" -ForegroundColor Cyan
Write-Host ("  managers     : {0}" -f $managers.Count)
Write-Host ("  public methods (units) : {0}" -f $methodCount)
Write-Host ("  calc methods (money math, P1) : {0}" -f $calcCount)
Write-Host "  top calc-bearing managers:"
$managers | Where-Object { $_.calcCount -gt 0 } | Sort-Object calcCount -Descending | Select-Object -First 8 |
    ForEach-Object { Write-Host ("    {0,-26} {1} calc / {2} methods" -f $_.manager, $_.calcCount, $_.methodCount) }
Write-Host "  -> $outFile"
