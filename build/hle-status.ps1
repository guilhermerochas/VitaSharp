# Regenerates the README's HLE status from the installed firmware and games: docs/hle/data/hle-status.tsv (per-library
# summary), the badges and progress map in docs/assets/status, and docs/hle/progress.md
# (VitaSharp.Desktop hle-coverage --status; HleStatusReportTests checks the committed files match the summary).
# -Data: the data folder with the firmware (os0/vs0) and the games (ux0:app); default VitaSharp's own (only read).
# -Exe: a built VitaSharp.Desktop to use; default: build one into -Out (default: a folder in the temp directory).
# Linux/macOS: build/hle-status.sh (same steps).
param([string]$Data = '', [string]$Exe = '', [string]$Out = '')
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Exe) {
    if (-not $Out) { $Out = Join-Path ([IO.Path]::GetTempPath()) 'vitasharp-hle-status' }
    dotnet build (Join-Path $root 'src/VitaSharp.Desktop') -c Release -m:2 -p:UseSharedCompilation=false -v q -o $Out
    if ($LASTEXITCODE -ne 0) { throw 'build failed' }
    $Exe = Join-Path $Out 'VitaSharp.Desktop'
    if (Test-Path "$Exe.exe") { $Exe = "$Exe.exe" }
}
$cmd = @('hle-coverage', "--vitasdk=$(Join-Path $root 'docs/hle/data/vitasdk-user-functions.tsv')", "--status=$root")
if ($Data) { $cmd = @("--data=$Data") + $cmd }
& $Exe @cmd
if ($LASTEXITCODE -ne 0) { throw 'hle-coverage failed' }
