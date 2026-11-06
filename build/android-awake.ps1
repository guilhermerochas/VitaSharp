# Keeps the phone's screen on while the Android scripts work (HyperOS refuses `svc power stayon` and `adb shell input`):
# a background loop checks `dumpsys power` every Interval seconds and reopens the developer APK's AwakeActivity (a blank
# screen with KeepScreenOn) only when mWakefulness is not Awake. When the phone is offline, unauthorized or its screen
# stays off, it plays an alert sound every 90 s. The Android scripts call `start` first and `stop` when they finish.
# Usage: build/android-awake.ps1 start|stop|check|reopen [-Serial SERIAL] [-Interval 30]   (same as build/android-awake.sh)
#   start   starts the loop (nothing when one is running); reopen the AwakeActivity after every `am force-stop` of the app
#   stop    ends the loop and closes the AwakeActivity
#   check   one check now, printing the state (also what the loop does)
#   reopen  opens the AwakeActivity now (after an `am force-stop` of the app, which kills it too)
param(
    [Parameter(Mandatory = $true)][ValidateSet('start', 'stop', 'check', 'reopen', 'loop')][string]$Action,
    [string]$Serial = '',
    [int]$Interval = 30
)
$ErrorActionPreference = 'Continue'
$pkg = 'io.github.vitasharp'
$awake = "$pkg/$pkg.AwakeActivity"
$pidFile = Join-Path ([IO.Path]::GetTempPath()) "vitasharp-android-awake$(if ($Serial) { "-$Serial" }).pid"
$adbArgs = @(); if ($Serial) { $adbArgs = @('-s', $Serial) }

function Alert { try { [System.Media.SystemSounds]::Exclamation.Play(); Start-Sleep -Milliseconds 1500 } catch { [console]::Beep(800, 400) } }

function Reopen {
    $out = (& adb @adbArgs shell am start -n $awake 2>&1 | Out-String)
    if ($out -match 'Error|does not exist') { Write-Host "AwakeActivity not available (developer APK needed): $($out.Trim())" }
}

# one check: returns 'device' (awake), 'asleep' (reopened the activity), or the adb state (offline, unauthorized, absent)
function Check {
    $line = (& adb devices 2>$null) | Where-Object { $_ -match '^\S+\s+\S+$' -and $_ -notmatch '^List' }
    if ($Serial) { $line = $line | Where-Object { $_ -match "^$Serial\s" } }
    $state = if ($line) { ((($line | Select-Object -First 1) -split '\s+')[1]) } else { 'absent' }
    if ($state -ne 'device') { return $state }
    $w = (& adb @adbArgs shell dumpsys power 2>$null | Select-String -Pattern 'mWakefulness=(\w+)' | Select-Object -First 1)
    $wake = if ($w) { $w.Matches[0].Groups[1].Value } else { '?' }
    if ($wake -eq 'Awake') { return 'device' }
    Reopen
    return 'asleep'
}

switch ($Action) {
    'check' { $s = Check; Write-Host "phone: $s"; if ($s -ne 'device') { Alert } }
    'reopen' { Reopen }
    'start' {
        if ((Test-Path $pidFile) -and (Get-Process -Id ([int](Get-Content $pidFile)) -ErrorAction SilentlyContinue)) { Write-Host 'screen watchdog already running'; break }
        $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, 'loop', '-Interval', $Interval)
        if ($Serial) { $args += @('-Serial', $Serial) }
        $p = Start-Process powershell -ArgumentList $args -WindowStyle Hidden -PassThru
        Set-Content $pidFile $p.Id
        Write-Host "screen watchdog started (pid $($p.Id), every $Interval s)"
        Check | Out-Null
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
