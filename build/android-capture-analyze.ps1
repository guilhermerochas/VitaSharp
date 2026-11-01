# Analyses a capture recorded with the pause menu of the developer APK ("Gravar tudo/tempo/conteúdo"): pulls it from the
# phone (or takes a local folder) and prints one summary: fps and the timing part, the PERF lines, the busiest HLE
# functions and threads the log reports, the audio check of every guest port WAV, and, from a replay of the GPU dump on
# this PC, the flicker check over every frame and the GPU ms per scene of one frame (tools/dev/README.md, "Captures").
# Usage: build/android-capture-analyze.ps1 [CAPTURE_NAME|LOCAL_FOLDER] [-Out DIR] [-Perf EXE] [-GpuProfile mali] [-Desktop EXE]
param([string]$Capture = '', [string]$Out = (Join-Path (Get-Location) 'captures'), [string]$Perf = '', [string]$GpuProfile = 'mali', [string]$Desktop = '')
$ErrorActionPreference = 'Stop'
$remote = '/sdcard/Android/data/io.github.vitasharp/files/captures'
$here = Split-Path -Parent $PSScriptRoot
function perf { if ($Perf) { & $Perf @args } else { dotnet run --project (Join-Path $here 'tools/VitaSharp.PerfTools') -c Release -p:VitaSharpDeveloper=true -m:2 -v q -- @args } }
if ($Capture -and (Test-Path $Capture -PathType Container)) { $dir = (Resolve-Path $Capture).Path }
else {
    $name = if ($Capture) { $Capture } else { (adb shell ls -t $remote | Select-Object -First 1).Trim() }
    New-Item -ItemType Directory -Force $Out | Out-Null
    adb pull "$remote/$name" $Out | Out-Null
    $dir = Join-Path $Out $name
}
Write-Host "== $dir"
Get-Content (Join-Path $dir 'info.txt') | Where-Object { $_ } | Select-Object -First 12
Write-Host '== fps (log, 30 s windows) and capture lines'
Select-String -Path (Join-Path $dir 'log.txt') -Pattern '\[perf\] [0-9]|\[capture\]' | Select-Object -Last 12 | ForEach-Object { $_.Line }
Write-Host '== PERF lines'
Select-String -Path (Join-Path $dir 'info.txt') -Pattern 'PERF ' | Select-Object -First 12 | ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
Write-Host '== HLE and threads (log)'
Select-String -Path (Join-Path $dir 'log.txt') -Pattern "HLE top|hot HLE|thread '" | Select-Object -Last 12 | ForEach-Object { $_.Line }
if (Test-Path (Join-Path $dir 'audio')) { Write-Host '== audio (guest ports)'; perf audio-check (Join-Path $dir 'audio') }
$dump = Get-ChildItem $dir -Filter *.vsgd | Select-Object -First 1
if ($dump) {
    $bench = Join-Path $dir 'bench'
    New-Item -ItemType Directory -Force (Join-Path $bench 'fixtures') | Out-Null
    Copy-Item $dump.FullName (Join-Path $bench 'fixtures/capture.vsgd') -Force
    Write-Host "== flicker over the dump (replay on this PC, $GpuProfile profile)"
    perf bench --child "fixture=capture;mode=flicker;loops=1;gpuprofile=$GpuProfile;run=analyze" --root $bench | Select-String 'BENCH|\[bench\] frame' | Select-Object -First 15 | ForEach-Object { $_.Line }
    $frames = @(perf bench cut $dump.FullName | Select-String '^frame').Count
    $mid = [int][Math]::Floor($frames / 2)
    Write-Host "== GPU ms per scene, frame $mid of $frames (this PC's GPU: relative costs; build/android-bench.* with this fixture for phone ms)"
    perf bench cut $dump.FullName --frame $mid --name frame_mid --root $bench | Out-Null
    perf bench --child "fixture=frame_mid;reps=3;gpuprofile=$GpuProfile;run=analyze" --root $bench | Select-String 'BENCH|\[bench\]   s' | Select-Object -First 30 | ForEach-Object { $_.Line }
    if ($Desktop) {
        Write-Host '== Vulkan validation (replay-gpu --validation, mali profile)'
        $env:VK_KHRONOS_VALIDATION_VALIDATE_SYNC = 'true'
        & $Desktop replay-gpu $dump.FullName "--out=$(Join-Path $dir 'validation')" --validation --gpu-profile=mali *> (Join-Path $dir 'validation.txt')
        Select-String -Path (Join-Path $dir 'validation/log.txt') -Pattern 'VUID-[A-Za-z0-9_-]*|SYNC-[A-Z0-9_-]*' -AllMatches -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Matches.Value } | Group-Object | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object { "$($_.Count) $($_.Name)" }
    }
}
