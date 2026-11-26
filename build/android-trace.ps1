# Records a Perfetto trace of a game on a USB-connected Android phone running the developer APK (-p:VitaSharpDeveloper=true):
# CPU scheduling, CPU/GPU frequency, GPU counters and render stages when the driver offers them, and the emulator's own
# markers (VITASHARP_TRACE=1: guest frames, GPU worker frames and submits, sampled HLE calls, JIT compiles, audio grains).
# Open the .perfetto-trace at https://ui.perfetto.dev (docs/notes/developer-tools.md, "Reading a trace").
# Usage: build/android-trace.ps1 TITLEID [-Seconds 10] [-Delay 40] [-Input SCRIPT] [-Out DIR] [-Hle 64] [-ExtraEnv 'K=V;K=V']
param(
    [Parameter(Mandatory = $true)][string]$TitleId,
    [int]$Seconds = 10,
    [int]$Delay = 40,
    [string]$Input = '',
    [string]$Out = (Join-Path (Get-Location) 'android-trace'),
    [int]$Hle = 64,
    [string]$ExtraEnv = ''
)
$ErrorActionPreference = 'Stop'
$pkg = 'io.github.vitasharp'
& "$PSScriptRoot\android-awake.ps1" start   # the phone's screen is kept on until the end (build/android-awake.ps1)
# the app's data folder (docs/android/README.md, "Storage"): the app's own answer (data-root.txt), checked
# against "All files access" (appops), by the phone-side command in build/android-data-root.txt
$dataRoot = ''
try
{
    $dataRoot = (adb shell (Get-Content -Raw (Join-Path $PSScriptRoot '..\tools\dev\android-data-root.txt')).Trim() 2>$null | Out-String).Trim()
}
catch
{
    $dataRoot = ''
}
if (-not $dataRoot)
{
    $dataRoot = '/sdcard/Android/data/io.github.vitasharp/files'
}
$files = $dataRoot
$trace = '/data/misc/perfetto-traces/vitasharp.perfetto-trace'
$here = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Force $Out | Out-Null
if ((adb shell pidof "${pkg}:game") -ne $null) { Write-Error 'a game is running on the phone: not starting' }

$envLines = @('VITASHARP_TRACE=1', "VITASHARP_TRACE_HLE=$Hle")
if ($Input) { $envLines += "VITASHARP_ANDROID_INPUT=$Input" }
if ($ExtraEnv) { $envLines += $ExtraEnv.Split(';') }
[System.IO.File]::WriteAllText((Join-Path $Out 'env.txt'), ($envLines -join "`n") + "`n")
$cfg = Join-Path $Out 'config.pbtx'
[System.IO.File]::WriteAllText($cfg, "duration_ms: $($Seconds * 1000)`n" + [System.IO.File]::ReadAllText((Join-Path $here 'tools/VitaSharp.PerfTools/perfetto-vitasharp.pbtx')))
$activity = (adb shell cmd package resolve-activity --brief $pkg | Select-Object -Last 1).Trim()
adb shell am force-stop $pkg
& "$PSScriptRoot\android-awake.ps1" reopen
adb push (Join-Path $Out 'env.txt') "$files/config/env.txt" | Out-Null
adb shell am start -n $activity --es titleId $TitleId | Out-Null
Start-Sleep -Seconds $Delay
adb shell rm -f $trace
# the config goes through stdin (cmd /c: PowerShell 5 pipes text with a BOM and CRLF)
cmd /c "adb shell perfetto --txt -c - -o $trace < `"$cfg`"" | Out-File (Join-Path $Out 'perfetto.txt')
$local = Join-Path $Out "$TitleId.perfetto-trace"
adb pull $trace $local | Out-Null
adb pull "$files/logs/last.log" (Join-Path $Out 'last.log') 2>$null | Out-Null
adb shell rm -f $trace "$files/config/env.txt"
adb shell am force-stop $pkg
& "$PSScriptRoot\android-awake.ps1" reopen
Get-Item $local | Select-Object FullName, Length
Write-Host "open $local at https://ui.perfetto.dev"
& "$PSScriptRoot\android-awake.ps1" stop
