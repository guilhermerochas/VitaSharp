# Records the media parity trace (src/VitaSharp.Core/Media/MediaContract.cs) on an Android phone or the Android
# Emulator with the developer APK installed: pushes the audio clips of tests/VitaSharp.Tests/Fixtures/Media, runs the
# app's MediaParityActivity (MediaCodec H.264, AAC, MP3) and pulls the trace, which MediaParityTests compares with the
# desktop's (FFmpeg): $env:VITASHARP_ANDROID_MEDIA_TRACE='<trace>'; dotnet test tests/VitaSharp.Tests --filter MediaParityTests
# Usage: build/android-media-parity.ps1 [-Out FILE] [-Serial SERIAL] [-Sync 0] [-AppEnv 'NAME=VALUE;...'] [-Timeout 120]
#   (same as build/android-media-parity.sh)
#   -Out     trace file to write (default ./android-media-parity/trace.txt)
#   -Sync 0  the old MediaCodec output timing (VITASHARP_MEDIACODEC_SYNC=0)
#   -AppEnv  variables set in the app's parity process
param(
    [string]$Out = './android-media-parity/trace.txt',
    [string]$Serial = '',
    [string]$Sync = '',
    [string]$AppEnv = '',
    [int]$Timeout = 120
)
$ErrorActionPreference = 'Stop'
$pkg = 'io.github.vitasharp'
$files = "/sdcard/Android/data/$pkg/files/media-parity"
$activity = "$pkg/$pkg.MediaParityActivity"
$clips = Join-Path $PSScriptRoot '..\tests\VitaSharp.Tests\Fixtures\Media'
$adbArgs = @()
if ($Serial)
{
    $adbArgs = @('-s', $Serial)
}

$state = (& adb @adbArgs get-state 2>$null | Out-String).Trim()
if ($state -ne 'device')
{
    Write-Error 'no Android device or emulator ready (adb get-state)'
}

& adb @adbArgs shell mkdir -p $files | Out-Null
foreach ($clip in Get-ChildItem -File $clips)
{
    & adb @adbArgs push $clip.FullName "$files/" | Out-Null
}

& adb @adbArgs shell rm -f "$files/done.txt" "$files/trace.txt" | Out-Null
& adb @adbArgs shell am start -n $activity --es sync "'$Sync'" --es env "'$AppEnv'" | Out-Null

$waited = 0
while ($true)
{
    $exists = (& adb @adbArgs shell "[ -f $files/done.txt ] && echo yes" | Out-String).Trim()
    if ($exists -eq 'yes')
    {
        break
    }

    if ($waited -ge $Timeout)
    {
        Write-Error "the parity run did not finish in $Timeout s (developer APK installed?)"
    }

    Start-Sleep -Seconds 2
    $waited += 2
}

$status = (& adb @adbArgs shell cat "$files/done.txt" | Out-String).Trim()
if ($status -ne 'ok')
{
    Write-Error "the parity run failed on the device: $status"
}

$outDir = Split-Path -Parent $Out
if ($outDir)
{
    New-Item -ItemType Directory -Force $outDir | Out-Null
}

& adb @adbArgs pull "$files/trace.txt" $Out | Out-Null
& adb @adbArgs pull "$files/parity.log" ([IO.Path]::ChangeExtension($Out, '.log')) 2>$null | Out-Null
Write-Host "trace: $Out"
Write-Host "compare: `$env:VITASHARP_ANDROID_MEDIA_TRACE='$Out'; dotnet test tests/VitaSharp.Tests --filter MediaParityTests"
