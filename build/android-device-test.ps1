# Runs one game on a USB-connected Android phone and collects what the run did: the session log (logs/last.log), a
# screenshot every -Step seconds, the guest's audio as WAVs (VITASHARP_AUDIO_DUMP), and a summary (fps lines, CPU
# backend, audio, errors). The game's input comes from VITASHARP_ANDROID_INPUT (ScriptedPad syntax, seconds since
# the game first polled the pad): phones may refuse `adb shell input` without a developer security setting.
# A game that is not on the phone is pushed first (app folder and license from the desktop data root) and removed
# again afterwards unless -Keep; the phone's other data is never touched (only the app's own files folder).
# Usage: build/android-device-test.ps1 -TitleId PCSB01240 [-Seconds 90] [-Pad "cross@20,start@30"] [-Out dir]
#   [-Data root] [-Keep] [-Step 15] [-ExtraEnv "VITASHARP_GPU_QUIRKS=-TileBased"]
# Linux/macOS: build/android-device-test.sh (same steps).
param(
    [Parameter(Mandatory)][string]$TitleId,
    [int]$Seconds = 90,
    [string]$Pad = '',
    [string]$Out = (Join-Path (Get-Location) "android-device-test\$TitleId"),
    [string]$Data = (Join-Path $env:APPDATA 'VitaSharp'),
    [switch]$Keep,
    [int]$Step = 15,
    [string[]]$ExtraEnv = @()
)
$ErrorActionPreference = 'Stop'
$pkg = 'io.github.vitasharp'
$files = "/sdcard/Android/data/$pkg/files"
New-Item -ItemType Directory -Force (Join-Path $Out 'shots') | Out-Null

adb get-state | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'no phone connected (adb get-state failed)' }
$activity = (adb shell cmd package resolve-activity --brief $pkg | Select-Object -Last 1).Trim()
if (-not $activity -or $activity -notmatch '/') { throw "VitaSharp ($pkg) is not installed on the phone" }

$pushed = $false
adb shell "[ -d $files/fs/ux0/app/$TitleId ]"
if ($LASTEXITCODE -ne 0) {
    $src = Join-Path $Data "fs\ux0\app\$TitleId"
    if (-not (Test-Path $src)) { throw "$TitleId is neither on the phone nor in $Data\fs\ux0\app" }
    Write-Host "pushing $TitleId"
    adb push $src "$files/fs/ux0/app/" | Out-Null
    $lic = Join-Path $Data "fs\ux0\license\$TitleId"
    if (Test-Path $lic) {
        adb shell mkdir -p "$files/fs/ux0/license" | Out-Null
        adb push $lic "$files/fs/ux0/license/" | Out-Null
    }
    $pushed = $true
}

$envLines = @()
if ($Pad) { $envLines += "VITASHARP_ANDROID_INPUT=$Pad" }
$envLines += 'VITASHARP_AUDIO_DUMP=devtest_audio'
$envLines += $ExtraEnv
$envFile = Join-Path $Out 'env.txt'
[IO.File]::WriteAllText($envFile, ($envLines -join "`n") + "`n")
adb shell am force-stop $pkg | Out-Null
adb shell rm -rf "$files/devtest_audio" | Out-Null
adb push $envFile "$files/config/env.txt" | Out-Null
adb shell am start -n $activity --es titleId $TitleId | Out-Null

for ($t = $Step; $t -le $Seconds; $t += $Step) {
    Start-Sleep -Seconds $Step
    $shot = Join-Path $Out ("shots\{0:D3}s.png" -f $t)
    # exec-out through cmd keeps the PNG bytes intact (PowerShell 5 pipes re-encode binary output)
    cmd /c "adb exec-out screencap -p > `"$shot`""
}

$log = Join-Path $Out 'last.log'
adb pull "$files/logs/last.log" $log | Out-Null
adb pull "$files/devtest_audio" (Join-Path $Out 'audio') 2>$null | Out-Null
adb shell am force-stop $pkg | Out-Null
adb shell rm -rf "$files/devtest_audio" "$files/config/env.txt" | Out-Null
if ($pushed -and -not $Keep) {
    adb shell rm -rf "$files/fs/ux0/app/$TitleId" "$files/fs/ux0/license/$TitleId" | Out-Null
    Write-Host "removed $TitleId from the phone"
}

$lines = @("== $TitleId, $Seconds s")
if (Test-Path $log) {
    $text = Get-Content $log
    $lines += $text | Where-Object { $_ -match '\[perf\] [0-9]' }
    $lines += $text | Where-Object { $_ -match 'CPU backend: ' } | Select-Object -Last 1
    $lines += $text | Where-Object { $_ -match '\[perf\] audio|Audio: AAudio' } | Select-Object -Last 1
    $lines += $text | Where-Object { $_ -match 'GPU quirks|Mobile paths' } | Select-Object -First 2
    $errors = @($text | Where-Object { $_ -match '\[Error\]' })
    $lines += "errors: $($errors.Count), warnings: $(@($text | Where-Object { $_ -match '\[Warn \]' }).Count)"
    $lines += $errors | Select-Object -First 5
}
$lines | Tee-Object -FilePath (Join-Path $Out 'summary.txt')
