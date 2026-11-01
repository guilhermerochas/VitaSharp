# GPU bench on a USB-connected Android phone with the developer APK (-p:VitaSharpDeveloper=true): pushes the fixtures and
# variants, runs every (fixture, variant) ROUNDS times interleaved A/B/B/A (one process per run, REPS timed repetitions
# each), pulls the results and prints fixture x variant -> median GPU ms, spread and delta (tools/dev/README.md).
# Usage: build/android-bench.ps1 FIXTURES VARIANTS [ROUNDS] [REPS]   (same as build/android-bench.sh)
#   FIXTURES, VARIANTS: comma-separated names (BenchDir/fixtures/NAME.vsgd, BenchDir/variants/NAME; "base" = none).
#   -BenchDir (default ./bench), -Param (more bench parameters, ';'-separated), -Perf (vitasharp-perf executable for
#   the table; default dotnet run of tools/VitaSharp.PerfTools with -p:VitaSharpDeveloper=true), -Scenes (default 3).
param(
    [Parameter(Mandatory = $true)][string]$Fixtures,
    [Parameter(Mandatory = $true)][string]$Variants,
    [int]$Rounds = 4,
    [int]$Reps = 3,
    [string]$BenchDir = (Join-Path (Get-Location) 'bench'),
    [string]$Param = '',
    [string]$Perf = '',
    [int]$Scenes = 3
)
$ErrorActionPreference = 'Stop'
$pkg = 'io.github.vitasharp'
$remote = "/sdcard/Android/data/$pkg/files/bench"
$run = Get-Date -Format 'yyyyMMdd_HHmmss'
$here = Split-Path -Parent $PSScriptRoot
$fx = @($Fixtures.Split(',') | Where-Object { $_ })
$vs = @($Variants.Split(',') | Where-Object { $_ })


if ((adb shell pidof "${pkg}:game") -ne $null) { Write-Error 'a game is running on the phone: not starting the bench' }
adb shell mkdir -p "$remote/fixtures" "$remote/variants" "$remote/results" | Out-Null
foreach ($f in $fx) {
    $local = Join-Path $BenchDir "fixtures/$f.vsgd"
    $remoteSize = (adb shell stat -c %s "$remote/fixtures/$f.vsgd" 2>$null)
    if ("$((Get-Item $local).Length)" -ne "$remoteSize".Trim()) { adb push $local "$remote/fixtures/$f.vsgd" | Out-Null }
}
foreach ($v in $vs) {
    adb shell rm -rf "$remote/variants/$v" | Out-Null
    $dir = Join-Path $BenchDir "variants/$v"
    if (Test-Path $dir) { adb push $dir "$remote/variants/" | Out-Null }
}

$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($r = 0; $r -lt $Rounds; $r++) {
    $order = if ($r % 2 -eq 1) { $vs[($vs.Count - 1)..0] } else { $vs }
    foreach ($f in $fx) {
        foreach ($v in $order) {
            adb shell am start -W -n "$pkg/$pkg.BenchActivity" --es bench "'fixture=$f;variant=$v;reps=$Reps;run=$run;$Param'" | Out-Null
            for ($t = 0; $t -lt 240; $t++) {
                if ((adb shell pidof "${pkg}:bench") -eq $null) { break }
                Start-Sleep -Milliseconds 500
            }
            Write-Host ("round $r $f ${v}: {0:F0} s" -f $sw.Elapsed.TotalSeconds)
        }
    }
}
$results = Join-Path $BenchDir 'results'
New-Item -ItemType Directory -Force $results | Out-Null
$tsv = Join-Path $results "$run.tsv"
adb pull "$remote/results/$run.tsv" $tsv | Out-Null
adb shell cat "$remote/results/errors.txt" 2>$null | Select-Object -Last 5
Write-Host ("$Rounds rounds in {0:F0} s; results $tsv" -f $sw.Elapsed.TotalSeconds)
if ($Perf) { & $Perf bench table $tsv --base $vs[0] --scenes $Scenes }
else { dotnet run --project (Join-Path $here 'tools/VitaSharp.PerfTools') -c Release -p:VitaSharpDeveloper=true -m:2 -v q -- bench table $tsv --base $vs[0] --scenes $Scenes }
