# Runs the desktop emulator as the Android build runs (docs/notes/cpu-phases.md, "Reproducing the phone's timing on
# the PC"): the Native AOT build (no JIT), --phone-profile (waits without spinning, Mali GPU profile, 333 MHz, two host
# cores, 4 MB GC gen0 budget), a case-sensitive data folder and the CPU phase profile on, then summarises the log.
# Usage: build/phone-profile.ps1 [-Help] [-Out folder] [-Publish] [-Data folder] [-Log file] [-Affinity mask] [-NoRun]
#                                [<VitaSharp.Desktop arguments>]
#   (same as build/phone-profile.sh --help --out --publish --data --log --affinity --no-run -- <arguments>)
#   -Out: where the Native AOT build is published once (default <temp>/vitasharp-phone-profile); -Publish publishes
#     it again (after a code change).
#   -Data: the data folder of the run; a new one is made case-sensitive (Windows: fsutil setCaseSensitiveInfo, needs
#     an empty folder; Linux: always; macOS: only on a case-sensitive volume, a warning otherwise).
#   -Log: the run's log (default <data>/logs/phone-profile.log); -Affinity: VITASHARP_PHONE_AFFINITY (host cores mask,
#     default two cores of the upper half); -NoRun: publish and prepare only.
#   Example: build/phone-profile.ps1 -Data $env:TEMP\pp run tests/VitaSharp.TestGame/build/VitaSharpTest.vpk
#     --reinstall --vulkan --seconds=150 --dialogs=auto
[CmdletBinding(PositionalBinding = $false)]
param(
    [string]$Out = '',
    [switch]$Publish,
    [string]$Data = '',
    [string]$Log = '',
    [string]$Affinity = '',
    [switch]$NoRun,
    [switch]$Help,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments = @()
)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Get-Content $PSCommandPath -TotalCount 14 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}

# the .NET runtime identifier of this machine
function Get-HostRid {
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    if ($IsLinux) {
        return "linux-$arch"
    }
    if ($IsMacOS) {
        return "osx-$arch"
    }
    return "win-$arch"
}

# makes a new, empty data folder case-sensitive where the system allows it
function Set-CaseSensitiveFolder([string]$Folder) {
    if ((Test-Path $Folder) -and (Get-ChildItem -Force $Folder | Select-Object -First 1)) {
        Write-Host "phone-profile.ps1: $Folder exists: its case sensitivity is kept (the run log says which it is)"
        return
    }
    New-Item -ItemType Directory -Force $Folder | Out-Null
    if ($IsMacOS) {
        Write-Warning 'macOS volumes are usually case-insensitive; use a case-sensitive APFS volume for -Data'
    }
    elseif (-not $IsLinux) {
        fsutil file setCaseSensitiveInfo $Folder enable | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "fsutil could not make $Folder case-sensitive"
        }
    }
}

# the phone-profile lines of a run log: the profile, fps, PERF phases and phase alerts
function Show-Summary([string]$LogFile) {
    if (-not (Test-Path $LogFile)) {
        Write-Warning "no log at $LogFile"
        return
    }
    Write-Host "== $LogFile"
    $profileLine = Select-String -Path $LogFile -Pattern 'Phone profile:' | Select-Object -First 1
    if ($profileLine) {
        $profileLine.Line
    }
    else {
        "(no 'Phone profile:' line)"
    }
    Select-String -Path $LogFile -Pattern 'fps over|FPS frame' | Select-Object -Last 5 | ForEach-Object { $_.Line }
    Select-String -Path $LogFile -Pattern 'PERF phases' | Select-Object -Last 6 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(400, $_.Line.Length)) }
    $alerts = @(Select-String -Path $LogFile -Pattern '\[phase-alert\]')
    "phase alerts: $($alerts.Count)"
    $alerts | Select-Object -Last 3 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
}

$root = Split-Path -Parent $PSScriptRoot
if (-not $Out) {
    $Out = Join-Path ([System.IO.Path]::GetTempPath()) 'vitasharp-phone-profile'
}
$rid = Get-HostRid
$exe = Join-Path $Out 'VitaSharp.Desktop'
if ($rid -like 'win-*') {
    $exe += '.exe'
}
if ($Publish -or -not (Test-Path $exe)) {
    Write-Host "phone-profile.ps1: publishing the Native AOT build ($rid) to $Out"
    New-Item -ItemType Directory -Force $Out | Out-Null
    # the Native AOT linker step finds the MSVC tools with vswhere, which the Visual Studio installer does not put on PATH
    if (-not $IsLinux -and -not $IsMacOS -and -not (Get-Command vswhere.exe -ErrorAction SilentlyContinue)) {
        $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
        if (Test-Path (Join-Path $installer 'vswhere.exe')) {
            $env:PATH = "$installer;$env:PATH"
        }
    }
    $ErrorActionPreference = 'Continue' # dotnet's stderr lines must not stop the script
    dotnet publish (Join-Path $root 'src/VitaSharp.Desktop') -c Release -r $rid -p:VitaSharpRelease=true `
        -p:VitaSharpAot=true -p:UseSharedCompilation=false -m:2 -o $Out 2>&1 | Out-File -Encoding utf8 "$Out.publish.log"
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        throw "the publish failed: $Out.publish.log"
    }
}

$extra = @()
if ($Data) {
    Set-CaseSensitiveFolder $Data
    $extra += "--data=$Data"
    if (-not $Log) {
        $Log = Join-Path $Data 'logs/phone-profile.log'
    }
}
if ($Log) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $Log) | Out-Null
    $extra += "--log=$Log"
}
if ($NoRun) {
    exit 0
}
if ($Arguments.Count -eq 0) {
    throw 'no VitaSharp.Desktop arguments (see -Help)'
}

$env:VITASHARP_PHONE_PROFILE = '1'
if (-not $env:DOTNET_GCgen0size) {
    $env:DOTNET_GCgen0size = '0x400000'
}
if (-not $env:VITASHARP_PHASE_PROF) {
    $env:VITASHARP_PHASE_PROF = '1'
}
if ($Affinity) {
    $env:VITASHARP_PHONE_AFFINITY = $Affinity
}
& $exe @Arguments @extra
$rc = $LASTEXITCODE
if ($Log) {
    Show-Summary $Log
}
exit $rc
