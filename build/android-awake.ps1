# Keeps the phone's screen on while the Android scripts work (HyperOS refuses `svc power stayon` and `adb shell input`):
# a background loop checks `dumpsys power` every Interval seconds and reopens the developer APK's AwakeActivity (a blank
# screen with KeepScreenOn) only when mWakefulness is not Awake. When the phone is offline, unauthorized or its screen
# stays off, it plays an alert sound every 90 s. The Android scripts call `begin` first and `stop` at the end only when
# their `begin` started the loop, so a watchdog that was already running stays on.
# Usage: build/android-awake.ps1 start|begin|running|stop|check|reopen [-Serial SERIAL] [-Interval 30]
#   (same as build/android-awake.sh; -Serial defaults to $env:ANDROID_SERIAL)
#   start   starts the loop (nothing when one is running); reopen the AwakeActivity after every `am force-stop` of the app
#   begin   starts the loop when none is running; exit 0 = this call started it (stop it at the end), 3 = one was running
#   running exit 0 when a watchdog runs for this phone (or one started without a serial; no -Serial: any), else 1
#   stop    ends the loop and closes the AwakeActivity
#   check   one check now, printing the state (also what the loop does)
#   reopen  opens the AwakeActivity now (after an `am force-stop` of the app, which kills it too)
param(
    [Parameter(Mandatory = $true)][ValidateSet('start', 'begin', 'running', 'stop', 'check', 'reopen', 'loop')][string]$Action,
    [string]$Serial = $env:ANDROID_SERIAL,
    [int]$Interval = 30
)
$ErrorActionPreference = 'Continue'
$pkg = 'io.github.vitasharp'
$awake = "$pkg/$pkg.AwakeActivity"
$pidDir = [IO.Path]::GetTempPath()
$pidFile = Join-Path $pidDir "vitasharp-android-awake$(if ($Serial) { "-$Serial" }).pid"
$adbArgs = @(); if ($Serial) { $adbArgs = @('-s', $Serial) }

function Alert { try { [System.Media.SystemSounds]::Exclamation.Play(); Start-Sleep -Milliseconds 1500 } catch { [console]::Beep(800, 400) } }

function Reopen {
    $out = (& adb @adbArgs shell am start -n $awake 2>&1 | Out-String)
    if ($out -match 'Error type|does not exist') { Write-Host "AwakeActivity not available (developer APK needed): $($out.Trim())" }
}

# adb output, or nothing when the call takes more than 20 s (adb can hang while the phone drops off the USB)
function AdbTimed([string[]]$Argv) {
    $job = Start-Job -ScriptBlock { param($a) & adb @a 2>$null } -ArgumentList (, $Argv)
    $out = if (Wait-Job $job -Timeout 20) { Receive-Job $job } else { $null }
    Remove-Job $job -Force
    return $out
}

# one check: returns 'device' (awake), 'asleep' (reopened the activity), or the adb state (offline, unauthorized, absent,
# unresponsive: an adb call that hangs while the phone drops off the USB is cut after 20 s, so the loop keeps alerting)
function Check {
    $line = (AdbTimed @('devices')) | Where-Object { $_ -match '^\S+\s+\S+$' -and $_ -notmatch '^List' }
    if ($Serial) { $line = $line | Where-Object { $_ -match "^$Serial\s" } }
    $state = if ($line) { ((($line | Select-Object -First 1) -split '\s+')[1]) } else { 'absent' }
    if ($state -ne 'device') { return $state }
    $w = (AdbTimed ($adbArgs + @('shell', 'dumpsys', 'power')) | Select-String -Pattern 'mWakefulness=(\w+)' | Select-Object -First 1)
    if (-not $w) { return 'unresponsive' }
    $wake = $w.Matches[0].Groups[1].Value
    if ($wake -eq 'Awake') { return 'device' }
    Reopen
    return 'asleep'
}

# whether a watchdog is running: this phone's (-Serial) or the one started without a serial; with no -Serial, any
function Test-Running {
    if ($Serial) {
        $files = @($pidFile, (Join-Path $pidDir 'vitasharp-android-awake.pid')) | Where-Object { Test-Path $_ }
    }
    else {
        $files = @(Get-ChildItem -Path $pidDir -Filter 'vitasharp-android-awake*.pid' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
    }
    foreach ($f in $files) {
        $id = 0
        if ([int]::TryParse(((Get-Content $f -ErrorAction SilentlyContinue) | Select-Object -First 1), [ref]$id)) {
            if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
                return $true
            }
        }
    }
    return $false
}

function Start-Watchdog {
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, 'loop', '-Interval', $Interval)
    if ($Serial) {
        $argv += @('-Serial', $Serial)
    }
    $p = Start-Process powershell -ArgumentList $argv -WindowStyle Hidden -PassThru
    Set-Content $pidFile $p.Id
    Write-Host "screen watchdog started (pid $($p.Id), every $Interval s)"
    Check | Out-Null
}

switch ($Action) {
    'check' { $s = Check; Write-Host "phone: $s"; if ($s -ne 'device') { Alert } }
    'reopen' { Reopen }
    'start' {
        if (Test-Running) {
            Write-Host 'screen watchdog already running'
            break
        }
        Start-Watchdog
    }
    'begin' {
        if (Test-Running) {
            Write-Host 'screen watchdog already running (left on at the end)'
            Reopen
            exit 3
        }
        Start-Watchdog
        exit 0
    }
    'running' {
        if (Test-Running) {
            Write-Host 'screen watchdog running'
            exit 0
        }
        Write-Host 'screen watchdog not running'
        exit 1
    }
    'stop' {
        if (Test-Path $pidFile) { Stop-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue; Remove-Item $pidFile -ErrorAction SilentlyContinue }
        & adb @adbArgs shell am start -n $awake --ez finish true 2>&1 | Out-Null
        Write-Host 'screen watchdog stopped'
    }
    'loop' {
        $lastAlert = [DateTime]::MinValue
        $bad = 0
        while ($true) {
            $s = Check
            if ($s -eq 'device') { $bad = 0 }
            else {
                $bad++
                # an asleep screen gets one reopen before the alert; offline/unauthorized alert at once
                if (($s -ne 'asleep' -or $bad -ge 2) -and ([DateTime]::Now - $lastAlert).TotalSeconds -ge 90) { Alert; $lastAlert = [DateTime]::Now }
            }
            Start-Sleep -Seconds $Interval
        }
    }
}
