# Checks the C# code style of .editorconfig (the Ryujinx/Ryubing style): `dotnet format whitespace` (layout) and
# `dotnet format style` (the rules set to :warning, through VitaSharp.sln), both with --verify-no-changes, over the
# repository's tracked .cs files only (git ls-files: never .claude/ with the agents' worktrees, nor untracked files).
# The folders listed in build/format-pending.txt are skipped until they are converted.
# Usage: build/format-check.ps1 [-Fix] [-WhitespaceOnly] [-Help]   (same as build/format-check.sh)
#   -Fix             rewrite the files instead of verifying (dotnet format, build/NamingTool: names renamed to the
#                    naming rules, build/FormatTool: one-line blocks split, accessor lists on one line); review the
#                    diff before committing
#   -WhitespaceOnly  only the layout check (no project load, no restore needed)
param([switch]$Fix, [switch]$WhitespaceOnly, [switch]$Help)
$ErrorActionPreference = 'Stop'

if ($Help) {
    Get-Content $PSCommandPath -TotalCount 9 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}

$root = Split-Path $PSScriptRoot -Parent
$pending = @(Get-Content (Join-Path $root 'build/format-pending.txt') |
    ForEach-Object { ($_ -replace '#.*$', '').Trim().TrimEnd('/') } | Where-Object { $_ })
# .claude/ holds the agents' worktrees (whole other checkouts): never part of this checkout's check
$skipped = @($pending) + '.claude'
$files = @(git -C $root ls-files -- '*.cs' | Where-Object {
        $file = $_
        -not ($skipped | Where-Object { $file.StartsWith("$_/", [StringComparison]::Ordinal) })
    })
if ($LASTEXITCODE -ne 0 -or $files.Count -eq 0) {
    Write-Error 'format-check: git ls-files found no .cs files (run it from a git checkout)'
    exit 2
}
# the file list goes through a response file (one token per line): it is longer than a Windows command line
$includeFile = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllLines($includeFile, [string[]](@('--include') + $files))
$include = "@$includeFile"
$verify = @(if (-not $Fix) { '--verify-no-changes' })

# `dotnet format` expands the response file itself and starts dotnet-format.dll with every path on its command line,
# longer than Windows allows (32K characters) once the whole tree is listed: the dll of the active SDK is run directly so
# that it reads the response file. Other SDK layouts fall back to `dotnet format`.
function Get-FormatCommand {
    $version = (dotnet --version | Select-Object -First 1).Trim()
    foreach ($line in @(dotnet --list-sdks)) {
        if ($line -match '^(\S+) \[(.+)\]$' -and $Matches[1] -eq $version) {
            $dll = Join-Path $Matches[2] "$version/DotnetTools/dotnet-format/dotnet-format.dll"
            if (Test-Path -LiteralPath $dll) {
                return @('exec', $dll)
            }
        }
    }
    return @('format')
}

Push-Location $root
try {
    $format = Get-FormatCommand
    "format-check: whitespace ($($files.Count) tracked files, $($pending.Count) folders pending)"
    dotnet @format whitespace --folder @verify $include
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not $WhitespaceOnly) {
        'format-check: style (severity warn)'
        dotnet @format style VitaSharp.sln --severity warn @verify $include
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        if ($Fix) {
            # a second pass: the fix-all of a rule applies one kind of its fixes per run (IDE0008 rewrites `var` of
            # built-in types and of the other types as two kinds)
            dotnet @format style VitaSharp.sln --severity warn $include
            if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        }
    }
    if ($Fix) {
        # the naming rules (IDE1006): dotnet format reports them but has no fix for them
        'format-check: naming (build/NamingTool)'
        dotnet run --project (Join-Path $root 'build/NamingTool') -c Release -v q -- $root
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        # what dotnet format cannot do: split one-line statement blocks, keep { get; set; } on one line (the check is
        # tests/VitaSharp.Tests/OneLineBlockTests); then indent the split lines. FormatTool reads the same list.
        'format-check: layout (build/FormatTool)'
        dotnet run --project (Join-Path $root 'build/FormatTool') -c Release -v q -- $root $includeFile
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        dotnet @format whitespace --folder $include
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
    'format-check: ok'
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $includeFile -ErrorAction SilentlyContinue
}
