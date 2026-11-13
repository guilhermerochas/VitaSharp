<#
.SYNOPSIS
    Builds build\VitaSharpTest.vpk on Windows by running build.sh inside WSL (VitaSDK lives in the WSL user's home).

.DESCRIPTION
    Usage: .\build.ps1 [-InstallSdk] [-Clean] [-Distro <name>]
      -InstallSdk  installs VitaSDK into ~/vitasdk inside WSL first (official vdpm bootstrap)
      -Clean       removes build\ before building
      -Distro      WSL distribution to use (default: Debian)

    Requirements: WSL 2 with a Debian or Ubuntu distribution that has git, curl, make and python3.
#>
param(
    [switch]$InstallSdk,
    [switch]$Clean,
    [string]$Distro = "Debian"
)

$ErrorActionPreference = "Stop"

$repoWindowsPath = $PSScriptRoot
$repoWslPath = (wsl -d $Distro -- wslpath -a ($repoWindowsPath -replace '\\', '/')).Trim()
if (-not $repoWslPath)
{
    throw "build.ps1: cannot translate $repoWindowsPath to a WSL path (is the $Distro distribution installed?)"
}

$arguments = @()
if ($InstallSdk)
{
    $arguments += "--install-sdk"
}
if ($Clean)
{
    $arguments += "--clean"
}

$command = "ulimit -c 0; cd '$repoWslPath' && bash ./build.sh $($arguments -join ' ')"
wsl -d $Distro -- bash -lc $command
if ($LASTEXITCODE -ne 0)
{
    throw "build.ps1: build failed (exit code $LASTEXITCODE)"
}
