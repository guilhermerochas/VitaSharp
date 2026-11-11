# Smoke test of the PowerShell scripts: every tracked .ps1 must parse (the PowerShell parser, no execution).
# Usage: build/check-scripts-help.ps1   (the twin of build/check-scripts-help.sh, which also runs every .sh --help;
# the .ps1 scripts take -Help or have no side effects only for some, so this one only parses them)
param([switch]$Help)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Get-Content $PSCommandPath -TotalCount 3 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}
Set-Location (Join-Path $PSScriptRoot '..')
$failed = $false
foreach ($f in (git ls-files '*.ps1')) {
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path $f), [ref]$tokens, [ref]$errors) | Out-Null
    if ($errors.Count -gt 0) {
        "does not parse: $f"
        $errors | Select-Object -First 3 | ForEach-Object { "  $($_.Extent.StartLineNumber): $($_.Message)" }
        $failed = $true
    }
}
if ($failed) { exit 1 }
'scripts parse: ok'
