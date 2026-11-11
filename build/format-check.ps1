# Checks the C# code style of .editorconfig (the Ryujinx/Ryubing style): `dotnet format whitespace` (layout) over
# every .cs file and `dotnet format style` (the rules set to :warning) over VitaSharp.sln, both with
# --verify-no-changes. The folders listed in build/format-pending.txt are skipped until they are converted.
# Usage: build/format-check.ps1 [-Fix] [-WhitespaceOnly] [-Help]   (same as build/format-check.sh)
#   -Fix             rewrite the files instead of verifying (review the diff before committing)
#   -WhitespaceOnly  only the layout check (no project load, no restore needed)
param([switch]$Fix, [switch]$WhitespaceOnly, [switch]$Help)
$ErrorActionPreference = 'Stop'

if ($Help) {
    Get-Content $PSCommandPath -TotalCount 6 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}

$root = Split-Path $PSScriptRoot -Parent
$exclude = @(Get-Content (Join-Path $root 'build/format-pending.txt') |
    ForEach-Object { ($_ -replace '#.*$', '').Trim() } | Where-Object { $_ })
$verify = @(if (-not $Fix) { '--verify-no-changes' })
$excludeArgs = @(if ($exclude.Count -gt 0) { '--exclude'; $exclude })

Push-Location $root
try {
    "format-check: whitespace ($($exclude.Count) folders pending)"
    dotnet format whitespace --folder @verify @excludeArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not $WhitespaceOnly) {
        'format-check: style (severity warn)'
        dotnet format style VitaSharp.sln --severity warn @verify @excludeArgs
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'format-check: ok'
}
finally {
    Pop-Location
}
