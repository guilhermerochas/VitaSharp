# Repeatable frame-rate measurement of one game scene on a USB-connected Android phone (developer APK): every run is a
# cold start (app closed, battery cooled to -MaxTemp), loads the scene's save state with a scripted loadstate step,
# waits -Warmup seconds in gameplay (excluded), then measures -Measure seconds: display frame times from SurfaceFlinger
# (median / p90 frame ms, median and mean fps), the app's PERF lines over the same window (GPU ms per frame, CPU ms of
# the game thread, GPU worker and whole process), the big/little core clocks, battery/skin temperature and thermal
# status, and a screenshot at the start and end of the window (proof of gameplay). Variants are env.txt settings,
# run interleaved (A B B A ...), so runs stay comparable across APKs and days. Results: Out/results.tsv (one row per
# run) and a per-variant median table. The screen watchdog (build/android-awake.ps1) is expected to run.
# Usage: build/android-perf-run.ps1 -Scene kz [-Variants "base|early=VITASHARP_DISPLAY_EARLY_SIGNAL=1"] [-Runs 3]
#   -Scene: kz (Killzone Mercenary, staircase, slot 1), uc (Uncharted Golden Abyss gameplay, slot 1), f1 (F1 2011
#     race start, slot 1), or TITLEID:SLOT for another game's save state.
#   -Variants: '|'-separated NAME[@APK][=ENV1=V1;ENV2=V2] ("base" = no extra settings); a variant with @APK installs
#     that APK (adb install -r keeps the app data) before its runs, to compare builds; an entry starting with @ is a
#     phone shell command run before each start (@cmd game mode performance io.github.vitasharp; set back to standard).
#   -Warmup 60 -Measure 30 -LoadAt (s after the first pad poll; default 30, f1 60) -MaxTemp 37 (battery deg C, waits at most 5 min)
#   -Apk (install it first) -Out (results folder) -ExtraEnv (env.txt lines for every variant, ';'-separated)
#   -Scene kzboot: the staircase without a state (new campaign through the menus by touch; -BootWait s after the
#   start, default 150). -Stat: simpleperf stat (cycles, instructions, L1I refills) of the guest main thread, 10 s.
#   app_fps column: the app's own presented-frame rate ([perf] lines); with bursty output (queued frames shown back to
#   back after a stall) the display median overstates, the mean and app_fps are the throughput.
# Several phones attached: set $env:ANDROID_SERIAL = "<serial>" first (adb uses it).
# Linux/macOS: build/android-perf-run.sh (same steps).
param(
    [Parameter(Mandatory)][string]$Scene,
    [string]$Variants = 'base',
    [int]$Runs = 3,
    [int]$Warmup = 60,
    [int]$Measure = 30,
    [int]$LoadAt = 0,
    [double]$MaxTemp = 37,
    [string]$Apk = '',
    [string]$Out = '',
    [string]$ExtraEnv = '',
    [int]$BootWait = 150,
    [switch]$Stat
)
$ErrorActionPreference = 'Stop'
$pkg = 'io.github.vitasharp'
$files = "/sdcard/Android/data/$pkg/files"
$inv = [Globalization.CultureInfo]::InvariantCulture
switch -Regex ($Scene) {
    '^kz$' { $title = 'PCSC00045'; $slot = 1 }
    '^uc$' { $title = 'PCSA00029'; $slot = 1 }
    '^f1$' { $title = 'PCSE00002'; $slot = 1; if (-not $LoadAt) { $LoadAt = 60 } }   # after the intro videos
    # no save state (builds whose snapshot schema differs): new single-player campaign, normal difficulty, through the
    # Valor card and menus by touch, to the first gameplay (the staircase) about 2 min after the start
    '^kzboot$' { $title = 'PCSC00045'; $slot = 0; $boot = 'tap@40:0.82:0.77,tap@52:0.25:0.37,tap@62:0.5:0.55' }
    '^[^:]+:\d+$' { $title, $slot = $Scene.Split(':'); $slot = [int]$slot }
    default { throw "unknown scene '$Scene' (kz, kzboot, uc, f1 or TITLEID:SLOT)" }
}
if (-not $LoadAt) { $LoadAt = 30 }
if (-not $Out) { $Out = Join-Path (Get-Location) ("android-perf-run\{0}_{1}" -f $Scene.Split(':')[0], (Get-Date -Format 'yyyyMMdd_HHmmss')) }
New-Item -ItemType Directory -Force $Out | Out-Null
function Sh([string]$cmd) { ((adb shell $cmd 2>$null) -join "`n") -replace "`r", '' }

adb get-state | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'no phone connected (adb get-state failed)' }
if ($Apk) { "installing $Apk"; adb install -r $Apk | Select-Object -Last 1 }
if ($slot -ne 0 -and (Sh "[ -f $files/savestates/$title/slot$slot.vss ] && echo ok") -notmatch 'ok') { throw "no save state $title slot $slot on the phone" }
$activity = ((Sh "cmd package resolve-activity --brief $pkg") -split "`n" | Select-Object -Last 1).Trim()
$apkInfo = ((Sh "dumpsys package $pkg") -split "`n" | Where-Object { $_ -match 'lastUpdateTime' } | Select-Object -First 1).Trim()

$vs = @(foreach ($v in $Variants.Split('|')) {
    $head = $v; $e = ''
    if ($v.Contains('=')) { $i = $v.IndexOf('='); $head = $v.Substring(0, $i); $e = $v.Substring($i + 1) }
    $name = $head; $apkPath = ''
    if ($head.Contains('@')) { $i = $head.IndexOf('@'); $name = $head.Substring(0, $i); $apkPath = $head.Substring($i + 1) }
    [pscustomobject]@{ Name = $name; Apk = $apkPath; Env = $e }
})
$installed = ''

function BatteryTemp { $m = [regex]::Match((Sh 'dumpsys battery'), 'temperature: (\d+)'); if ($m.Success) { [int]$m.Groups[1].Value / 10.0 } else { -1 } }
function Cool { for ($i = 0; $i -lt 30; $i++) { $t = BatteryTemp; if ($t -le $MaxTemp) { return $t }; Start-Sleep 10 }; return $t }
function GameLayer {
    # newer SurfaceFlinger lists RequestedLayerState{<id> <name> parentId=...}: --latency wants "<id> <name>"
    $l = (Sh 'dumpsys SurfaceFlinger --list') -split "`n" | Where-Object { $_.Contains("SurfaceView[$pkg/") -and $_.Contains('(BLAST)') -and $_ -notmatch 'Background' } | Select-Object -Last 1
    if (-not $l) { return '' }
    ($l -replace '^RequestedLayerState\{', '' -replace ' parentId=.*', '' -replace '\}$', '').Trim()
}
function Median($xs) { $a = @($xs | Sort-Object); if ($a.Count -eq 0) { return $null }; $a[[int][Math]::Floor(($a.Count - 1) / 2)] }
function Num([string]$s) { [double]::Parse($s.Replace(',', '.'), $inv) }

$header = "variant`trun`tvalid`tsf_frames`tmedian_ms`tp90_ms`tmedian_fps`tmean_fps`tgpu_ms`tgame_ms`tworker_ms`tproc_cpu_ms`tbig_mhz`tlittle_mhz`tgpu_mhz`ttemp_start`ttemp_end`tskin_end`tthermal`tapp_fps`tnote"
Set-Content -Path (Join-Path $Out 'results.tsv') -Value $header -Encoding utf8
$info = "scene $Scene ($title slot $slot), $($vs.Count) variant(s) x $Runs, warm-up $Warmup s, measure $Measure s; $apkInfo"
$info; Set-Content -Path (Join-Path $Out 'info.txt') -Value $info -Encoding utf8

$order = for ($r = 0; $r -lt $Runs; $r++) {
    $idx = 0..($vs.Count - 1); if ($r % 2 -eq 1) { [array]::Reverse($idx) }
    foreach ($i in $idx) { [pscustomobject]@{ V = $i; R = $r } }
}
$rows = @()
foreach ($item in $order) {
    $v = $vs[$item.V]; $r = $item.R
    $rd = Join-Path $Out "$($v.Name)_$r"; New-Item -ItemType Directory -Force $rd | Out-Null
    if ($v.Apk -and $installed -ne $v.Apk) { adb install -r $v.Apk | Select-Object -Last 1; $installed = $v.Apk }
    adb shell am force-stop $pkg
    & "$PSScriptRoot\android-awake.ps1" reopen | Out-Null
    $t0 = Cool
    $steps = if ($slot -eq 0) { $boot } else { "loadstate@${LoadAt}:$slot" }
    $envLines = @("VITASHARP_ANDROID_INPUT=$steps", 'VITASHARP_SESSION_REPORT_SECONDS=10')
    if ($ExtraEnv) { $envLines += $ExtraEnv.Split(';') }
    if ($v.Env) { $envLines += $v.Env.Split(';') }
    # entries starting with @ are phone shell commands run before the start (the game mode goes back to standard after)
    $shellCmds = @($envLines | Where-Object { $_.StartsWith("@") } | ForEach-Object { $_.Substring(1) })
    $envLines = @($envLines | Where-Object { -not $_.StartsWith("@") })
    foreach ($c in $shellCmds) { adb shell $c | Out-Null }
    [IO.File]::WriteAllText((Join-Path $rd 'env.txt'), ($envLines -join "`n") + "`n")
    adb push (Join-Path $rd 'env.txt') "$files/config/env.txt" | Out-Null
    adb shell am start -n $activity --es titleId $title | Out-Null
    # a launch whose game process never appears (the launcher activity died at start) is retried once
    for ($i = 0; $i -lt 10 -and -not (Sh "pidof ${pkg}:game").Trim(); $i++) { Start-Sleep 1 }
    if (-not (Sh "pidof ${pkg}:game").Trim()) {
        Write-Warning "$($v.Name) run ${r}: no game process 10 s after the start, launching again"
        adb shell am force-stop $pkg; & "$PSScriptRoot\android-awake.ps1" reopen | Out-Null; Start-Sleep 2
        adb shell am start -n $activity --es titleId $title | Out-Null
    }
    $start = Get-Date; $loaded = $false; $why = ''
    if ($slot -eq 0) {   # no state: the scripted steps reach gameplay after -BootWait s
        while (((Get-Date) - $start).TotalSeconds -lt $BootWait) {
            Start-Sleep 5
            if (-not (Sh "pidof ${pkg}:game").Trim()) { $why = 'game process gone'; break }
        }
        $loaded = -not $why
    }
    while ($slot -ne 0 -and ((Get-Date) - $start).TotalSeconds -lt $LoadAt + 150) {
        Start-Sleep 5
        # the scripted step logs its result; only "Save state: <path> loaded in" means the state is in
        if ((Sh "grep -c 'Scripted load state, slot ${slot}: ' $files/logs/last.log").Trim() -ne '0') {
            if ((Sh "grep -c 'Save state: .*slot$slot.vss loaded in' $files/logs/last.log").Trim() -ne '0') { $loaded = $true }
            else { $why = (Sh "grep -m1 'Scripted load state, slot ${slot}: ' $files/logs/last.log") -replace '.*Scripted load state, slot \d+: ', '' }
            break
        }
        if (-not (Sh "pidof ${pkg}:game").Trim()) { $why = 'game process gone'; break }
    }
    if (-not $loaded) {
        adb pull "$files/logs/last.log" (Join-Path $rd 'last.log') 2>$null | Out-Null
        $rows += [pscustomobject]@{ Name = $v.Name; Valid = $false }
        $note = 'state not loaded' + $(if ($why) { ": $why" } else { '' })
        # 20 columns: variant run valid, 12 empty (sf_frames .. gpu_mhz), temp_start, 3 empty, note
        Add-Content -Path (Join-Path $Out 'results.tsv') -Value (@($v.Name, $r, 0) + @('') * 12 + @($t0, '', '', '', '', $note) -join "`t")
        "$($v.Name) run ${r}: $note"; continue
    }
    Start-Sleep $Warmup
    cmd /c "adb exec-out screencap -p > `"$(Join-Path $rd 'start.png')`""
    $layer = GameLayer
    $sf = New-Object System.Collections.Generic.List[string]; $clocks = New-Object System.Collections.Generic.List[string]
    adb shell dumpsys SurfaceFlinger --latency-clear 2>$null | Out-Null
    # clocks over 10 s of the window from the kernel trace (time-weighted: GPU mfg_pll_ck, big cores 6-7)
    $atrace = Start-Process adb -ArgumentList 'shell', 'atrace', '-t', '10', 'freq', 'power' -NoNewWindow -PassThru `
        -RedirectStandardOutput (Join-Path $rd 'atrace.txt') -RedirectStandardError (Join-Path $rd 'atrace.err')
    # -Stat: hardware counters of the guest main thread for 10 s of the window (simpleperf stat, profileable APK)
    $statProc = $null
    if ($Stat) {
        $logText = Sh "cat $files/logs/last.log"
        $main = ([regex]::Match($logText, "pinned guest main '([^']+)'")).Groups[1].Value
        $mt = ([regex]::Matches($logText, "(\d+) '" + [regex]::Escape($main) + "'") | Select-Object -Last 1).Groups[1].Value
        if ($mt) {
            $statProc = Start-Process adb -ArgumentList 'shell', 'simpleperf', 'stat', '--app', $pkg, '-e', 'cpu-cycles,instructions,raw-l1i-cache-refill,raw-l1i-tlb-refill', '-t', $mt, '--duration', '10' `
                -NoNewWindow -PassThru -RedirectStandardOutput (Join-Path $rd 'stat.txt') -RedirectStandardError (Join-Path $rd 'stat.err')
        }
    }
    $ws = (Sh 'date +%H:%M:%S').Trim(); $mstart = Get-Date
    do {
        $sf.AddRange([string[]]((Sh "dumpsys SurfaceFlinger --latency `"$layer`"") -split "`n" | Select-Object -Skip 1))
        $clocks.Add(((Sh 'cat /sys/devices/system/cpu/cpu7/cpufreq/scaling_cur_freq /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq') -split "`n") -join ' ')
        Start-Sleep 2
    } while (((Get-Date) - $mstart).TotalSeconds -lt $Measure)
    $sf.AddRange([string[]]((Sh "dumpsys SurfaceFlinger --latency `"$layer`"") -split "`n" | Select-Object -Skip 1))
    $we = (Sh 'date +%H:%M:%S').Trim()
    $atrace.WaitForExit(30000) | Out-Null
    if ($statProc) { $statProc.WaitForExit(30000) | Out-Null }
    cmd /c "adb exec-out screencap -p > `"$(Join-Path $rd 'end.png')`""
    $tb = BatteryTemp
    $therm = Sh 'dumpsys thermalservice'
    $tst = ([regex]::Match($therm, 'Thermal Status: (\d+)')).Groups[1].Value
    $ts = ([regex]::Match($therm, 'mValue=([0-9.]+), mType=3, mName=SKIN')).Groups[1].Value
    $alive = [bool](Sh "pidof ${pkg}:game").Trim()
    $sf | Set-Content (Join-Path $rd 'sf.txt'); $clocks | Set-Content (Join-Path $rd 'clocks.txt')
    adb pull "$files/logs/last.log" (Join-Path $rd 'last.log') 2>$null | Out-Null
    adb shell am force-stop $pkg
    & "$PSScriptRoot\android-awake.ps1" reopen | Out-Null
    if ($shellCmds.Count) { adb shell cmd game mode standard $pkg | Out-Null }
    $note = if ($alive) { '' } else { 'game process gone' }

    # display: unique actual-present times (column 2, ns) -> intervals within the window
    $present = @($sf | ForEach-Object { $p = $_.Trim() -split '\s+'; if ($p.Count -eq 3) { [long]$p[1] } } | Where-Object { $_ -gt 0 -and $_ -lt 9e18 } | Sort-Object -Unique)
    $d = @(for ($i = 1; $i -lt $present.Count; $i++) { $x = ($present[$i] - $present[$i - 1]) / 1e6; if ($x -gt 0 -and $x -lt 1000) { $x } }) | Sort-Object
    $sfn = $d.Count; $med = $p90 = $mfps = $meanfps = 0
    if ($sfn -ge 10) {
        $med = $d[[int][Math]::Floor(($sfn + 1) / 2) - 1]; $p90 = $d[[int][Math]::Floor($sfn * 0.9 + 0.5) - 1]
        $mfps = 1000 / $med; $meanfps = 1000 * $sfn / ($d | Measure-Object -Sum).Sum
    }
    # cadence: display intervals in vblanks (16.67 ms), e.g. "1v:112 2v:70 3v:96 ..." (also in RUN\hist.txt)
    $hist = ($d | Group-Object { [int][Math]::Floor($_ / 16.667 + 0.5) } | Sort-Object { [int]$_.Name } | ForEach-Object { "$($_.Name)v:$($_.Count)" }) -join ' '
    Set-Content -Path (Join-Path $rd 'hist.txt') -Value $hist
    # PERF summaries inside the window: deltas between the first and the last summary written after the warm-up
    $log = Get-Content (Join-Path $rd 'last.log')
    $mainName = ([regex]::Match(($log -join "`n"), "pinned guest main '([^']+)'")).Groups[1].Value
    # placement off: the busiest guest thread (the first PERF cycles line)
    if (-not $mainName) { $mainName = ([regex]::Match(($log -join "`n"), 'PERF cycles ([^:]+):')).Groups[1].Value }
    $sum = @(); $cur = $null; $inw = $false
    foreach ($line in $log) {
        if ($line -match '^\[(\d\d:\d\d:\d\d)\.\d+\].*PERF periodic summary: frame (\d+)') {
            $inw = ($Matches[1] -ge $ws -and $Matches[1] -le $we)
            if ($inw) { $cur = @{ F = [double]$Matches[2]; G = 0.0; C = 0.0; W = 0.0; M = 0.0 }; $sum += $cur }
            continue
        }
        if (-not $inw) { continue }
        if ($line -match 'PERF gpu: .*\((\d+) submits, ([0-9,]+) ms each') { $cur.G = (Num $Matches[1]) * (Num $Matches[2]) }
        elseif ($line -match 'PERF cpu ([0-9,]+) s') { $cur.C = Num $Matches[1] }
        elseif ($line -match 'PERF thread GPU worker: ([0-9,]+) s cpu') { $cur.W = Num $Matches[1] }
        elseif ($mainName -and $line.Contains("PERF thread ${mainName}: ") -and $line -match ': ([0-9,]+) s cpu') { $cur.M = Num $Matches[1] }
    }
    # the app's own presented-frame rate over the window ([perf] lines every 30 s; the first one after the window too)
    $appVals = @(); $late = $false
    foreach ($line in $log) {
        if ($line -match '^\[(\d\d:\d\d:\d\d)\.\d+\].*\[perf\] ([0-9,]+) fps over') {
            $t = $Matches[1]
            if (($t -ge $ws -and $t -le $we) -or ($t -gt $we -and -not $late)) { $appVals += (Num $Matches[2]); if ($t -gt $we) { $late = $true } }
        }
    }
    $appFps = if ($appVals.Count) { (($appVals | Measure-Object -Average).Average).ToString('F1', $inv) } else { '-' }
    $gpu = $game = $worker = $proc = '-'
    if ($sum.Count -ge 2 -and $sum[-1].F -gt $sum[0].F) {
        $df = $sum[-1].F - $sum[0].F
        $gpu = '{0:F1}' -f (($sum[-1].G - $sum[0].G) / $df); $game = '{0:F1}' -f (($sum[-1].M - $sum[0].M) * 1000 / $df)
        $worker = '{0:F1}' -f (($sum[-1].W - $sum[0].W) * 1000 / $df); $proc = '{0:F1}' -f (($sum[-1].C - $sum[0].C) * 1000 / $df)
    }
    $big = Median ($clocks | ForEach-Object { [int](($_ -split ' ')[0]) / 1000 }); $little = Median ($clocks | ForEach-Object { [int](($_ -split ' ')[1]) / 1000 })
    # time-weighted clocks from the trace: the GPU PLL (MediaTek mfg_pll_ck) and the two fastest cores (6, 7)
    $gpuMhz = '-'; $t0s = $t1s = 0.0; $gl = $gf = $gs = $gw = 0.0; $cl = @{}; $cf = @{}; $cs = $cw = 0.0
    foreach ($line in (Get-Content (Join-Path $rd 'atrace.txt') -ErrorAction SilentlyContinue)) {
        if ($line -notmatch ' (\d+\.\d+): ') { continue }
        $t = [double]::Parse($Matches[1], $inv); if (-not $t0s) { $t0s = $t }; $t1s = $t
        if ($line -match 'clk_set_rate: mfg_pll_ck (\d+)') { if ($gl) { $gs += $gf * ($t - $gl); $gw += $t - $gl }; $gf = [double]$Matches[1] / 1e6; $gl = $t }
        elseif ($line -match 'cpu_frequency: state=(\d+) cpu_id=([67])$') {
            $id = $Matches[2]; $f = [double]$Matches[1] / 1000
            if ($cl.ContainsKey($id)) { $cs += $cf[$id] * ($t - $cl[$id]); $cw += $t - $cl[$id] }; $cf[$id] = $f; $cl[$id] = $t
        }
    }
    if ($gl) { $gs += $gf * ($t1s - $gl); $gw += $t1s - $gl }
    foreach ($id in @($cl.Keys)) { $cs += $cf[$id] * ($t1s - $cl[$id]); $cw += $t1s - $cl[$id] }
    if ($gw -gt 1) { $gpuMhz = [int]($gs / $gw) }
    if ($cw -gt 1) { $big = [int]($cs / $cw) }
    $valid = -not $note
    if ($sfn -lt 10) { $valid = $false; $note = (@($note, "no display frames (layer '$layer')") | Where-Object { $_ }) -join '; ' }
    $fmt = { param($x) if ($x -is [double]) { $x.ToString('F1', $inv) } else { "$x" } }
    $cols = @($v.Name, $r, [int]$valid, $sfn, (& $fmt $med), (& $fmt $p90), (& $fmt $mfps), (& $fmt $meanfps), $gpu, $game, $worker, $proc, $big, $little, $gpuMhz, $t0, $tb, $ts, $tst, $appFps, $note)
    Add-Content -Path (Join-Path $Out 'results.tsv') -Value ($cols -join "`t")
    $rows += [pscustomobject]@{ Name = $v.Name; Valid = $valid; Med = $med; P90 = $p90; MFps = $mfps; MeanFps = $meanfps; Gpu = $gpu; Game = $game; Worker = $worker; Big = $big; GpuMhz = $gpuMhz; AppFps = $appFps }
    "$($v.Name) run ${r}: median $(& $fmt $med) ms (p90 $(& $fmt $p90)), $(& $fmt $mfps) fps median, $(& $fmt $meanfps) mean, app $appFps fps; GPU $gpu ms at $gpuMhz MHz, game $game, worker $worker; big cores $big MHz; $t0 -> $tb C; cadence $hist $note"
}
adb shell rm -f "$files/config/env.txt" "$files/config/input_live.txt"

''
"== medians per variant (valid runs): $(Join-Path $Out 'results.tsv')"
$table = foreach ($v in $vs) {
    $ok = @($rows | Where-Object { $_.Name -eq $v.Name -and $_.Valid })
    $m = { param($k) $x = Median ($ok | ForEach-Object { $_.$k } | Where-Object { $_ -ne '-' } | ForEach-Object { [double]$_ }); if ($null -eq $x) { '-' } else { $x.ToString('0.#', $inv) } }
    [pscustomobject]@{ variant = $v.Name; runs = $ok.Count; median_ms = (& $m 'Med'); p90_ms = (& $m 'P90'); median_fps = (& $m 'MFps'); mean_fps = (& $m 'MeanFps'); app_fps = (& $m 'AppFps'); gpu_ms = (& $m 'Gpu'); game_ms = (& $m 'Game'); worker = (& $m 'Worker'); big_mhz = (& $m 'Big'); gpu_mhz = (& $m 'GpuMhz') }
}
$text = $table | Format-Table -AutoSize | Out-String
$text; Set-Content -Path (Join-Path $Out 'summary.txt') -Value $text -Encoding utf8
