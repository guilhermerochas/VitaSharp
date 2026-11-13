# Runs, on exactly what is committed (HEAD, in a clean temporary worktree), the checks the CI runs: script parity,
# scripts' --help, ShellCheck (when installed), the C# code style, a 0-warning Release build of the solution and the
# Category!=Slow test suite. Push only when it ends with "prepush-check: ok".
# Same as build/prepush-check.sh.
param([switch]$Help)
$ErrorActionPreference = 'Stop'

if ($Help) {
    'Usage: build/prepush-check.ps1 [-Help]'
    '  Checks HEAD (committed files only, in a temporary worktree) the way the CI does:'
    '  script parity, scripts'' --help, ShellCheck (if installed), format-check, Release build with 0 warnings,'
    '  dotnet test --filter "Category!=Slow". Exits 1 at the first failure.'
    exit 0
}

function Fail([string]$message)
{
    Write-Host "prepush-check: $message"
    exit 1
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("prepush-" + [System.Guid]::NewGuid().ToString('N'))
$tree = Join-Path $work 'tree'
New-Item -ItemType Directory -Path $work | Out-Null
git -C $root worktree add --detach $tree HEAD | Out-Null
try
{
    Set-Location $tree
    'prepush-check: script parity'
    & powershell -NoProfile -ExecutionPolicy Bypass -File build/check-script-parity.ps1
    if ($LASTEXITCODE -ne 0) { Fail 'script parity' }
    'prepush-check: scripts --help'
    & powershell -NoProfile -ExecutionPolicy Bypass -File build/check-scripts-help.ps1
    if ($LASTEXITCODE -ne 0) { Fail 'scripts --help' }
    if (Get-Command shellcheck -ErrorAction SilentlyContinue)
    {
        'prepush-check: shellcheck'
        & shellcheck (git ls-files '*.sh')
        if ($LASTEXITCODE -ne 0) { Fail 'shellcheck' }
    }
    else
    {
        'prepush-check: shellcheck not installed, skipped (the CI runs it)'
    }
    'prepush-check: format-check'
    & powershell -NoProfile -ExecutionPolicy Bypass -File build/format-check.ps1
    if ($LASTEXITCODE -ne 0) { Fail 'format-check' }
    'prepush-check: build'
    $buildLog = Join-Path $work 'build.log'
    & dotnet build VitaSharp.sln -c Release -m:2 -nologo -v q *> $buildLog
    if ($LASTEXITCODE -ne 0)
    {
        Get-Content $buildLog -Tail 30
        Fail 'build'
    }
    if (Select-String -Path $buildLog -Pattern '[1-9][0-9]* (Warning|Aviso)\(s\)' -Quiet)
    {
        Select-String -Path $buildLog -Pattern 'warning ' | Select-Object -First 20 | ForEach-Object { $_.Line }
        Fail 'build has warnings'
    }
    'prepush-check: tests'
    & dotnet test tests/VitaSharp.Tests -c Release --no-build --filter 'Category!=Slow' --blame-hang-timeout 10m -- RunConfiguration.MaxCpuCount=2
    if ($LASTEXITCODE -ne 0) { Fail 'tests' }
    'prepush-check: ok'
}
finally
{
    Set-Location $root
    git -C $root worktree remove --force $tree 2>$null | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
