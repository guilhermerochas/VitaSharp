<#
.SYNOPSIS
    Runs the ARM64 code of VitaSharp (the native ARM64 JIT backend, the .NET ARM64 runtime) on Windows without a
    phone: builds the linux-arm64 binaries here and runs them in WSL under qemu-user (build/qemu-arm64.sh).

.DESCRIPTION
    Usage: .\build\qemu-arm64.ps1 [-Install] [-Filter <filter>] [-NoTests] [-TestGame <vpk>] [-Seconds <n>]
                                  [-Out <folder>] [-Distro <name>]
      -Install   installs qemu-user-static, binfmt-support, the arm64 libraries (apt, as root in WSL) and the arm64
                 .NET runtime in ~/.dotnet-arm64 and the portable test platform in ~/.vstest-portable inside WSL
      -Filter    dotnet test filter (default: the ARM64 backend tests, see qemu-arm64.sh)
      -NoTests   runs no tests
      -TestGame  also boots the test game headless (VitaSharp.Desktop run) under qemu
      -Seconds   how long the test game runs (default 300)
      -Out       where the binaries, logs and results go (default: a temp folder)
      -Distro    WSL distribution to use (default: Debian)

    Requirements: WSL 2 (Debian or Ubuntu) with a .NET SDK in ~/.dotnet (dotnet-install.sh --channel 10.0
    --install-dir ~/.dotnet) and the .NET SDK on Windows.
#>
param(
    [switch]$Install,
    [string]$Filter = "",
    [switch]$NoTests,
    [string]$TestGame = "",
    [int]$Seconds = 300,
    [string]$Out = "",
    [string]$Distro = "Debian"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
if (-not $Out)
{
    $Out = Join-Path ([System.IO.Path]::GetTempPath()) ("vitasharp-qemu-arm64-" + [System.IO.Path]::GetRandomFileName())
}
New-Item -ItemType Directory -Force $Out | Out-Null

function ConvertTo-WslPath([string]$windowsPath)
{
    $path = (wsl -d $Distro -- wslpath -a ($windowsPath -replace '\\', '/')).Trim()
    if (-not $path)
    {
        throw "qemu-arm64.ps1: cannot translate $windowsPath to a WSL path (is the $Distro distribution installed?)"
    }
    return $path
}

if ($Install)
{
    # apt needs root; wsl -u root needs no password
    $packages = "unzip qemu-user-static binfmt-support libc6:arm64 libstdc++6:arm64 libgcc-s1:arm64 libssl3:arm64 zlib1g:arm64"
    $aptCommand = "dpkg --add-architecture arm64 && apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends $packages `$(apt-cache search --names-only '^libicu[0-9]+`$' | awk '{print `$1; exit}'):arm64"
    wsl -d $Distro -u root -- bash -c $aptCommand
    if ($LASTEXITCODE -ne 0)
    {
        throw "qemu-arm64.ps1: apt install failed (exit code $LASTEXITCODE)"
    }
}

# WSL without systemd does not register the binfmt entries at boot
wsl -d $Distro -u root -- bash -c "test -e /proc/sys/fs/binfmt_misc/qemu-aarch64 || update-binfmts --enable qemu-aarch64 || true"

$arguments = @()
if ($Install)
{
    # the arm64 runtime (the apt part is done above)
    wsl -d $Distro -- bash -lc "test -x ~/.dotnet-arm64/dotnet || (curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 --runtime dotnet --architecture arm64 --install-dir ~/.dotnet-arm64)"
    # the portable test platform (the version of the test project's Microsoft.NET.Test.Sdk; see qemu-arm64.sh)
    wsl -d $Distro -- bash -lc "test -f ~/.vstest-portable/tools/net8.0/vstest.console.dll || (mkdir -p ~/.vstest-portable && cd ~/.vstest-portable && curl -sSL -o platform.zip https://www.nuget.org/api/v2/package/Microsoft.TestPlatform.Portable/17.14.1 && unzip -q -o platform.zip && rm -f platform.zip)"
}
if ($NoTests)
{
    $arguments += "--no-tests"
}
else
{
    $testsDir = Join-Path $Out "tests-bin"
    dotnet build (Join-Path $root "tests\VitaSharp.Tests\VitaSharp.Tests.csproj") -c Release -r linux-arm64 --self-contained false -o $testsDir -m:2 -p:UseSharedCompilation=false -nologo -v:q
    if ($LASTEXITCODE -ne 0)
    {
        throw "qemu-arm64.ps1: test build failed (exit code $LASTEXITCODE)"
    }
    $arguments += "--tests-dir '$(ConvertTo-WslPath $testsDir)'"
    if ($Filter)
    {
        $arguments += "--filter '$Filter'"
    }
}
if ($TestGame)
{
    $desktopDir = Join-Path $Out "desktop-bin"
    dotnet publish (Join-Path $root "src\VitaSharp.Desktop\VitaSharp.Desktop.csproj") -c Release -r linux-arm64 --self-contained true -o $desktopDir -m:2 -p:UseSharedCompilation=false -nologo -v:q
    if ($LASTEXITCODE -ne 0)
    {
        throw "qemu-arm64.ps1: VitaSharp.Desktop publish failed (exit code $LASTEXITCODE)"
    }
    $arguments += "--testgame '$(ConvertTo-WslPath (Resolve-Path $TestGame).Path)'"
    $arguments += "--desktop-dir '$(ConvertTo-WslPath $desktopDir)'"
    $arguments += "--seconds $Seconds"
}
$arguments += "--out '$(ConvertTo-WslPath $Out)'"

$script = ConvertTo-WslPath (Join-Path $PSScriptRoot "qemu-arm64.sh")
wsl -d $Distro -- bash -lc "bash '$script' $($arguments -join ' ')"
$code = $LASTEXITCODE
Write-Host "qemu-arm64.ps1: output in $Out"
exit $code
