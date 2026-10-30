# Multiplatform rule: every script ships for Windows (name.ps1) and Linux/macOS (name.sh), doing the same thing.
# Fails when a tracked .ps1 has no .sh twin or a .sh has no .ps1 twin, except the single-platform scripts listed below.
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

# inherently single-platform: run by users of that platform's package only
$exempt = @(
    'build/package/install-desktop-entry.sh'   # Linux desktop entry, shipped in the Linux tar.gz/AppImage
)

$missing = $false
foreach ($f in (git ls-files '*.ps1')) {
    $twin = $f -replace '\.ps1$', '.sh'
    if ($exempt -notcontains $f -and -not (Test-Path $twin)) { "missing: $twin (twin of $f)"; $missing = $true }
}
foreach ($f in (git ls-files '*.sh')) {
    $twin = $f -replace '\.sh$', '.ps1'
    if ($exempt -notcontains $f -and -not (Test-Path $twin)) { "missing: $twin (twin of $f)"; $missing = $true }
    if ((git ls-files -s $f).Substring(0, 6) -ne '100755') { "not executable in git: $f"; $missing = $true }
}
if ($missing) { exit 1 }
'script parity: ok'
