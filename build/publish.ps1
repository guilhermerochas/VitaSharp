# Self-contained, single-file ReadyToRun builds of VitaSharp (launcher + emulator in one executable).
#   build\publish.ps1                         all platforms
#   build\publish.ps1 -Rid win-x64,linux-x64  only these
# Output: artifacts\publish\<rid>\ (override with -Out). Any host can publish for any platform; FFmpeg is
# downloaded for the target platform (macOS uses the system FFmpeg: brew install ffmpeg).
param(
    [string[]]$Rid = @('win-x64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string]$Out = (Join-Path $PSScriptRoot '..\artifacts\publish')
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\VitaSharp.Desktop\VitaSharp.Desktop.csproj'
foreach ($r in $Rid) {
    Write-Host "== $r"
    dotnet publish $project -c Release -r $r -p:VitaSharpRelease=true -p:DebugType=embedded -o (Join-Path $Out $r)
    if ($LASTEXITCODE -ne 0) { throw "publish $r failed" }
}
