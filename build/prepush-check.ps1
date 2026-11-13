# Runs, on exactly what is committed (HEAD, in a clean temporary worktree), the checks the CI runs: script parity,
# scripts' --help, ShellCheck (when installed), the C# code style, a 0-warning Release build of the solution and the
# Category!=Slow test suite (on Windows also on Linux, through WSL, when it has a .NET SDK in ~/.dotnet). Push only
# when it ends with "prepush-check: ok". Same as build/prepush-check.sh.
param([switch]$TestsOnly, [switch]$Help)
$ErrorActionPreference = 'Stop'

if ($Help)
{
    'Usage: build/prepush-check.ps1 [-TestsOnly] [-Help]'
    '  Checks HEAD (committed files only, in a temporary worktree) the way the CI does:'
    '  script parity, scripts'' --help, ShellCheck (if installed), format-check, Release build with 0 warnings,'
    '  dotnet test --filter "Category!=Slow"; on Windows the suite also runs on Linux through WSL when WSL has a'
    '  .NET SDK in ~/.dotnet. Exits 1 at the first failure.'
    '  The suite fails when dotnet test fails, when the run was aborted (test host crash) or when it ran fewer tests'
    '  than dotnet test --list-tests finds for the filter.'
    '  -TestsOnly  only builds the test project and runs the suite, in the current tree (what the WSL leg runs)'
    exit 0
}

$testProject = 'tests/VitaSharp.Tests'
$testFilter = 'Category!=Slow'
$wslTree = 'vitasharp-prepush'
# the PowerShell running this script (powershell.exe on Windows, pwsh on Linux/macOS) runs the other scripts
$powerShell = (Get-Process -Id $PID).Path

function Fail([string]$message)
{
    Write-Host "prepush-check: $message"
    exit 1
}

# Fails unless a test run's output shows a complete run.
function Test-SuiteLog([string]$label, [string]$log, [int]$code, [int]$expected)
{
    if ($code -ne 0)
    {
        Fail "${label}: dotnet test exited with $code"
    }

    if (Select-String -Path $log -Pattern 'Test Run Aborted|Test host process crashed|test run was aborted' -Quiet)
    {
        Fail "${label}: the test run was aborted (test host crash): not every test ran"
    }

    $summary = @(Select-String -Path $log -Pattern '^(Passed|Failed)!.*Total: *([0-9]+)')
    if ($summary.Count -eq 0)
    {
        Fail "${label}: no test summary in the output of dotnet test"
    }

    $total = [int]$summary[-1].Matches[0].Groups[2].Value
    if ($total -lt $expected)
    {
        Fail "${label}: $total tests ran, $expected expected (dotnet test --list-tests)"
    }

    "prepush-check: ${label}: $total tests ran ($expected listed)"
}

# Runs the Category!=Slow suite of the built test project and checks that it ran completely.
function Invoke-Suite([string]$label, [string]$out)
{
    $listed = @(& dotnet test $testProject -c Release --no-build --list-tests --filter $testFilter)
    if ($LASTEXITCODE -ne 0)
    {
        Fail "${label}: dotnet test --list-tests failed"
    }

    $expected = @($listed | Where-Object { $_ -match '^    \S' }).Count
    $log = Join-Path $out 'tests.log'
    # Tee-Object writes no file when dotnet test prints nothing
    Set-Content -Path $log -Value '' -Encoding UTF8
    # stderr lines too (the abort message), as text: Windows PowerShell wraps them as errors otherwise
    $ErrorActionPreference = 'Continue'
    & dotnet test $testProject -c Release --no-build --filter $testFilter --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none -- RunConfiguration.MaxCpuCount=2 2>&1 |
        ForEach-Object { "$_" } | Tee-Object -FilePath $log
    $code = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Test-SuiteLog $label $log $code $expected
}

# Runs a check script with this PowerShell and stops at its failure.
function Invoke-Check([string]$label, [string]$script)
{
    "prepush-check: $label"
    & $powerShell -NoProfile -ExecutionPolicy Bypass -File $script
    if ($LASTEXITCODE -ne 0)
    {
        Fail $label
    }
}

# Runs the suite on Linux in WSL, on HEAD extracted into ~/vitasharp-prepush, when WSL has a .NET SDK in ~/.dotnet.
function Invoke-WslSuite([string]$work)
{
    if (-not (Get-Command wsl -ErrorAction SilentlyContinue))
    {
        return
    }

    $hasSdk = (& wsl -- bash -lc 'test -x "$HOME/.dotnet/dotnet" && echo yes') -join ''
    if (($hasSdk -replace "[`r`n`0]", '') -ne 'yes')
    {
        'prepush-check: no .NET SDK in WSL, Linux tests skipped (the CI runs them)'
        return
    }

    'prepush-check: tests on Linux (WSL)'
    $archive = Join-Path $work 'head.tar'
    git archive -o $archive HEAD
    $linuxArchive = ((& wsl -- wslpath -a ($archive -replace '\\', '/')) -join '').Trim()
    $command = 'export PATH="$HOME/.dotnet:$PATH" DOTNET_ROOT="$HOME/.dotnet"' +
        " && rm -rf ~/$wslTree && mkdir ~/$wslTree && tar -xf '$linuxArchive' -C ~/$wslTree" +
        " && bash ~/$wslTree/build/prepush-check.sh --tests-only; code=`$?; rm -rf ~/$wslTree; exit `$code"
    & wsl -- bash -lc $command
    if ($LASTEXITCODE -ne 0)
    {
        Fail 'tests on Linux (WSL)'
    }
}

# No process of this check writes a .NET crash dump (Linux and WSL: build/prepush-check.sh also limits core dumps).
$env:DOTNET_DbgEnableMiniDump = '0'
# English output from dotnet: the run summary and the abort message are read from it
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path ([System.IO.Path]::GetTempPath()) ('prepush-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

if ($TestsOnly)
{
    try
    {
        Set-Location $root
        'prepush-check: build of the tests'
        $buildLog = Join-Path $work 'build.log'
        & dotnet build $testProject -c Release -m:2 -p:UseSharedCompilation=false -nologo -v q *> $buildLog
        if ($LASTEXITCODE -ne 0)
        {
            Get-Content $buildLog -Tail 30
            Fail 'build of the tests'
        }

        'prepush-check: tests'
        Invoke-Suite 'tests' $work
        'prepush-check: ok'
    }
    finally
    {
        Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
    }
    exit 0
}

$tree = Join-Path $work 'tree'
git -C $root worktree add --detach $tree HEAD | Out-Null
try
{
    Set-Location $tree
    Invoke-Check 'script parity' 'build/check-script-parity.ps1'
    Invoke-Check 'scripts --help' 'build/check-scripts-help.ps1'
    if (Get-Command shellcheck -ErrorAction SilentlyContinue)
    {
        'prepush-check: shellcheck'
        & shellcheck (git ls-files '*.sh')
        if ($LASTEXITCODE -ne 0)
        {
            Fail 'shellcheck'
        }
    }
    else
    {
        'prepush-check: shellcheck not installed, skipped (the CI runs it)'
    }

    Invoke-Check 'format-check' 'build/format-check.ps1'
    'prepush-check: build'
    $buildLog = Join-Path $work 'build.log'
    & dotnet build VitaSharp.sln -c Release -m:2 -p:UseSharedCompilation=false -nologo -v q *> $buildLog
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
    Invoke-Suite 'tests' $work
    Invoke-WslSuite $work
    'prepush-check: ok'
}
finally
{
    $ErrorActionPreference = 'Continue'
    Set-Location $root
    git -C $root worktree remove --force $tree 2>&1 | Out-Null
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
