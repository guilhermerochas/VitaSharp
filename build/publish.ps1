# Self-contained release builds of VitaSharp (launcher + emulator in one executable). No .NET install is needed to run them.
#   build\publish.ps1                         all platforms, single-file ReadyToRun
#   build\publish.ps1 -Rid win-x64,linux-x64  only these
#   build\publish.ps1 -Aot -Rid win-x64       Native AOT (see below)
#   build\publish.ps1 -Package -Rid win-x64   also build the Windows packages (see below)
# Output: artifacts\publish\<rid>\ (override with -Out). ReadyToRun builds can be published for any platform from any
# host; FFmpeg is downloaded for the target platform (macOS uses the system FFmpeg: brew install ffmpeg).
#
# -Aot compiles the whole app to native code ahead of time (faster start, less memory, no JIT at run time). Native
# AOT cannot cross-compile between operating systems, so it is used only for platforms of the host OS (Windows: win-*,
# needs the MSVC C++ build tools, e.g. VS 2022 Build Tools with "Desktop development with C++"); the other platforms
# fall back to ReadyToRun with a warning. Debug symbols (.pdb) go to artifacts\publish\<rid>-symbols\.
#
# -Package writes <out>\packages\VitaSharp-<version>-<rid>.zip (a VitaSharp\ folder with the executable, native
# libraries, ffmpeg\, LICENSE and README.md) and, when Inno Setup 6 or 7 is found (ISCC.exe on PATH, in its default
# folder, or named by the ISCC environment variable), the installer VitaSharp-<version>-<rid>-setup.exe. Linux
# (.tar.gz, AppImage) and macOS (.app) packages need Unix file permissions: build them with build/publish.sh --package
# on Linux or macOS.
# -Version names the packages (default: dev). -SkipPublish packages an existing publish output without rebuilding.
param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [switch]$Aot,
    [switch]$Package,
    [string]$Version = 'dev',
    [switch]$SkipPublish
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'src\VitaSharp.Desktop\VitaSharp.Desktop.csproj'
$Out = [IO.Path]::GetFullPath($Out)

function Test-AotHost([string]$r) {
    if ($IsLinux) { $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower(); return $r -eq "linux-$hostArch" }
    if ($IsMacOS) { return $r -like 'osx-*' }
    return $r -like 'win-*' # Windows PowerShell 5.1 has no $IsWindows
}

function Find-Iscc {
    if ($env:ISCC -and (Test-Path $env:ISCC)) { return $env:ISCC }
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    foreach ($dir in @(${env:ProgramFiles(x86)}, $env:ProgramFiles, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
        if (-not $dir) { continue }
        foreach ($v in 'Inno Setup 7', 'Inno Setup 6') {
            $p = Join-Path $dir "$v\ISCC.exe"
            if (Test-Path $p) { return $p }
        }
    }
    return $null
}

function New-WindowsPackage([string]$r, [string]$src) {
    $pkgDir = Join-Path $Out 'packages'
    $name = "VitaSharp-$Version-$r"
    $stage = Join-Path $pkgDir "stage\$r\VitaSharp"
    if (Test-Path (Split-Path $stage)) { Remove-Item -Recurse -Force (Split-Path $stage) }
    New-Item -ItemType Directory -Force $stage | Out-Null
    Copy-Item -Recurse -Force (Join-Path $src '*') $stage
    Copy-Item (Join-Path $root 'LICENSE'), (Join-Path $root 'README.md') $stage

    $zip = Join-Path $pkgDir "$name.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
    Write-Host ("   {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))

    $iscc = Find-Iscc
    if ($iscc) {
        & $iscc /Q "/DSourceDir=$stage" "/DOutputDir=$pkgDir" "/DAppVersion=$Version" "/DOutputName=$name-setup" (Join-Path $PSScriptRoot 'package\VitaSharp.iss')
        if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed for $r" }
        $setup = Join-Path $pkgDir "$name-setup.exe"
        Write-Host ("   {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
    }
    else { Write-Host '   (no installer: Inno Setup not found; install it from https://jrsoftware.org/isinfo.php or `winget install JRSoftware.InnoSetup`)' }
    Remove-Item -Recurse -Force (Join-Path $pkgDir 'stage')
}

if ($Aot -and -not ($IsLinux -or $IsMacOS)) {
    # the ILCompiler finds link.exe through vcvarsall.bat, which runs vswhere.exe by name: make sure it is on PATH
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$installer;$env:PATH" }
}

foreach ($r in $Rid) {
    $dest = Join-Path $Out $r
    if ($SkipPublish) {
        if (-not (Test-Path (Join-Path $dest '*'))) { throw "-SkipPublish: nothing published in $dest" }
    }
    elseif ($Aot -and (Test-AotHost $r)) {
        Write-Host "== $r (Native AOT)"
        dotnet publish $project -c Release -r $r -p:VitaSharpRelease=true -p:VitaSharpAot=true -o $dest
        if ($LASTEXITCODE -ne 0) { throw "publish $r failed" }
        $symbols = Join-Path $Out "$r-symbols"
        New-Item -ItemType Directory -Force $symbols | Out-Null
        Get-ChildItem $dest -File | Where-Object { $_.Extension -in '.pdb', '.dbg' -or $_.Name -like '*.dSYM' } | Move-Item -Destination $symbols -Force
        Get-ChildItem $dest -Directory -Filter '*.dSYM' | Move-Item -Destination $symbols -Force
    }
    else {
        if ($Aot) { Write-Warning "$r`: Native AOT cannot be cross-compiled from this OS, publishing ReadyToRun instead (build it on $r for AOT)" }
        Write-Host "== $r (ReadyToRun)"
        dotnet publish $project -c Release -r $r -p:VitaSharpRelease=true -p:DebugType=embedded -o $dest
        if ($LASTEXITCODE -ne 0) { throw "publish $r failed" }
    }

    if ($Package) {
        if ($r -like 'win-*') { Write-Host "== $r package"; New-WindowsPackage $r $dest }
        else { Write-Warning "$r`: Linux/macOS packages need Unix file permissions, build them with build/publish.sh --package on Linux or macOS" }
    }
}
