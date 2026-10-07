<#
.SYNOPSIS
  Shared helpers for test versioning. Tests are versioned by file copy:
    OpenItemTest.cs        -> class OpenItemTest        / [Test] Open_first_item_in_the_list      (v1)
    OpenItemTest.v2.cs     -> class OpenItemTest_v2     / [Test] Open_first_item_in_the_list_v2   (v2)
  The BASE method is the original name with any trailing _v<N> stripped.
#>

# 'Foo_v3' -> 'Foo'  ;  'Foo' -> 'Foo'
function Get-BaseMethod([string]$Method) {
    return ([regex]::Replace($Method, '_v\d+$', ''))
}

# method name -> its version number (no suffix = 1)
function Get-VersionOf([string]$Method) {
    $m = [regex]::Match($Method, '_v(\d+)$')
    if ($m.Success) { return [int]$m.Groups[1].Value } else { return 1 }
}

# Discover ALL versions of a base test across the .cs files.
# Returns objects: @{ version; method; file; baseFile } sorted ascending by version.
# baseFile = the v1 file name (OpenItemTest.cs) used to derive versioned file names.
function Get-TestVersions {
    param([Parameter(Mandatory)][string]$PocDir, [Parameter(Mandatory)][string]$BaseMethod)
    $found = @()
    $baseFile = $null
    foreach ($cs in Get-ChildItem $PocDir -Filter '*.cs' -File) {
        $raw = Get-Content $cs.FullName -Raw
        # match the base method optionally followed by _vN, as a [Test] method
        foreach ($mm in [regex]::Matches($raw, "public\s+void\s+($([regex]::Escape($BaseMethod))(?:_v(\d+))?)\s*\(")) {
            $method = $mm.Groups[1].Value
            $ver = if ($mm.Groups[2].Success) { [int]$mm.Groups[2].Value } else { 1 }
            $found += [pscustomobject]@{ version = $ver; method = $method; file = $cs.FullName }
            if ($ver -eq 1) { $baseFile = $cs.Name }
        }
    }
    # if no explicit v1 file detected (e.g. only v2+ exist), derive baseFile from the lowest-version file
    if (-not $baseFile -and $found.Count) {
        $lowest = ($found | Sort-Object version | Select-Object -First 1)
        $baseFile = [IO.Path]::GetFileName($lowest.file) -replace '\.v\d+\.cs$', '.cs'
    }
    $found | ForEach-Object { $_ | Add-Member -NotePropertyName baseFile -NotePropertyValue $baseFile -PassThru } |
        Sort-Object version
}

# The latest (highest) version of a base test: @{ version; method; file; baseFile }
function Get-LatestVersion {
    param([Parameter(Mandatory)][string]$PocDir, [Parameter(Mandatory)][string]$BaseMethod)
    $all = @(Get-TestVersions -PocDir $PocDir -BaseMethod $BaseMethod)
    if (-not $all.Count) { throw "No versions found for base test '$BaseMethod'" }
    return $all[-1]
}
