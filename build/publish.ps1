# Self-contained release builds of VitaSharp (launcher + emulator in one executable).
#   build\publish.ps1                         all platforms, single-file ReadyToRun
#   build\publish.ps1 -Rid win-x64,linux-x64  only these
#   build\publish.ps1 -Aot -Rid win-x64       Native AOT (see below)
# Output: artifacts\publish\<rid>\ (override with -Out). ReadyToRun builds can be published for any platform from any
# host; FFmpeg is downloaded for the target platform (macOS uses the system FFmpeg: brew install ffmpeg).
#
# -Aot compiles the whole app to native code ahead of time (faster start, less memory, no JIT at run time). Native
# AOT cannot cross-compile between operating systems, so it is used only for platforms of the host OS (Windows: win-*,
# needs the MSVC C++ build tools, e.g. VS 2022 Build Tools with "Desktop development with C++"); the other platforms
# fall back to ReadyToRun with a warning. Debug symbols (.pdb) go to artifacts\publish\<rid>-symbols\.
param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts\publish'),
    [switch]$Aot
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\VitaSharp.Desktop\VitaSharp.Desktop.csproj'

function Test-AotHost([string]$r) {
    if ($IsLinux) { $hostArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLower(); return $r -eq "linux-$hostArch" }
    if ($IsMacOS) { return $r -like 'osx-*' }
    return $r -like 'win-*' # Windows PowerShell 5.1 has no $IsWindows
}

if ($Aot -and -not ($IsLinux -or $IsMacOS)) {
    # the ILCompiler finds link.exe through vcvarsall.bat, which runs vswhere.exe by name: make sure it is on PATH
    $installer = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    if (Test-Path (Join-Path $installer 'vswhere.exe')) { $env:PATH = "$installer;$env:PATH" }
}

foreach ($r in $Rid) {
    $dest = Join-Path $Out $r
    if ($Aot -and (Test-AotHost $r)) {
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
}
