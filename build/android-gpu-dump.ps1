# Records a GPU dump of a game on a USB-connected Android phone and replays it on the PC with the Vulkan validation
# layer, synchronization validation included (missing barriers, write-after-read hazards: the usual causes of flicker
# on tile-based GPUs). The phone only captures; the investigation runs on the PC (docs/notes/gpu-dump.md).
# Usage: build/android-gpu-dump.ps1 -TitleId PCSA00126 -Frames 2700:2760 [-Seconds 300] [-Pad "cross@40"] [-Out dir]
#   [-Desktop path\VitaSharp.Desktop.exe] [-ReplayArgs "--frame=2760"] [-ExtraEnv "VITASHARP_GPU_QUIRKS=-TileBased"]
#   Frames are renderer presents of the running executable (a LoadExec starts them again from 1).
# Linux/macOS: build/android-gpu-dump.sh (same steps).
param(
    [Parameter(Mandatory)][string]$TitleId,
    [Parameter(Mandatory)][string]$Frames,
    [int]$Seconds = 300,
    [string]$Pad = '',
    [string]$Out = (Join-Path (Get-Location) "android-gpu-dump\$TitleId"),
    [string]$Desktop = '',
    [string]$ReplayArgs = '',
    [string[]]$ExtraEnv = @()
)
$ErrorActionPreference = 'Stop'
$pkg = 'io.github.vitasharp'
$files = "/sdcard/Android/data/$pkg/files"
$dump = "$TitleId.vsgd"
New-Item -ItemType Directory -Force $Out | Out-Null

$activity = (adb shell cmd package resolve-activity --brief $pkg | Select-Object -Last 1).Trim()
$envLines = @("VITASHARP_GPU_DUMP_AT=${Frames}:$dump")
if ($Pad) { $envLines += "VITASHARP_ANDROID_INPUT=$Pad" }
$envLines += $ExtraEnv
Set-Content -Path (Join-Path $Out 'env.txt') -Value $envLines -Encoding ascii
adb shell am force-stop $pkg
adb shell rm -rf "$files/gpudumps"
adb push (Join-Path $Out 'env.txt') "$files/config/env.txt" | Out-Null
adb shell am start -n $activity --es titleId $TitleId | Out-Null
# the recorder logs "[gpudump] saved" once the file is complete
for ($t = 0; $t -lt $Seconds; $t += 5) {
    Start-Sleep 5
    adb shell "grep -q 'gpudump\] saved' $files/logs/last.log" 2>$null
    if ($LASTEXITCODE -eq 0) { break }
}
adb pull "$files/gpudumps/$dump" (Join-Path $Out $dump) | Out-Null
adb pull "$files/logs/last.log" (Join-Path $Out 'last.log') | Out-Null
adb shell am force-stop $pkg
adb shell rm -rf "$files/gpudumps" "$files/config/env.txt"
$dumpPath = Join-Path $Out $dump
if (-not (Test-Path $dumpPath)) { throw "no dump recorded (frames $Frames not reached in $Seconds s?)" }
"dump: $dumpPath"

if ($Desktop) {
    # Khronos validation with synchronization validation (layer settings through the environment)
    $env:VK_KHRONOS_VALIDATION_VALIDATE_SYNC = 'true'
    $replayOut = Join-Path $Out 'replay'
    $args2 = @('replay-gpu', $dumpPath, "--out=$replayOut", '--validation') + ($ReplayArgs -split ' ' | Where-Object { $_ })
    & $Desktop @args2 *> (Join-Path $Out 'replay.txt')
    Remove-Item Env:VK_KHRONOS_VALIDATION_VALIDATE_SYNC
    $log = Join-Path $replayOut 'log.txt'
    $ids = if (Test-Path $log) { Select-String -Path $log -Pattern 'VUID-[A-Za-z0-9_-]*|SYNC-[A-Z0-9_-]*' -AllMatches | ForEach-Object { $_.Matches.Value } } else { @() }
    "validation messages: $(@($ids).Count)"
    $ids | Group-Object | Sort-Object Count -Descending | Select-Object -First 10 | ForEach-Object { "{0,7} {1}" -f $_.Count, $_.Name }
}
