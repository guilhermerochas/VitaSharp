#!/usr/bin/env bash
# GPU bench on a USB-connected Android phone with the developer APK (-p:VitaSharpDeveloper=true): pushes the fixtures and
# variants, runs every (fixture, variant) ROUNDS times interleaved A/B/B/A (one process per run, REPS timed repetitions
# each), pulls the results and prints fixture x variant -> median GPU ms, spread and delta (tools/dev/README.md).
# Usage: build/android-bench.sh FIXTURES VARIANTS [ROUNDS] [REPS]   (same as build/android-bench.ps1)
#   FIXTURES, VARIANTS: comma-separated names (BENCH_DIR/fixtures/NAME.vsgd, BENCH_DIR/variants/NAME; "base" = none).
#   env: BENCH_DIR (local folder with fixtures/ and variants/, default ./bench), PARAM (more bench parameters,
#   ';'-separated, for every run), PERF (vitasharp-perf command for the table; default dotnet run of tools/VitaSharp.PerfTools
#   with -p:VitaSharpDeveloper=true), SCENES (top scenes per row, default 3).
set -euo pipefail
export MSYS_NO_PATHCONV=1
fixtures="${1:?usage: android-bench.sh FIXTURES VARIANTS [ROUNDS] [REPS]}"
variants="${2:?usage: android-bench.sh FIXTURES VARIANTS [ROUNDS] [REPS]}"
rounds="${3:-4}"
reps="${4:-3}"
bench="${BENCH_DIR:-$PWD/bench}"
pkg=io.github.vitasharp
remote="/sdcard/Android/data/$pkg/files/bench"
run="$(date +%Y%m%d_%H%M%S)"
here="$(cd "$(dirname "$0")/.." && pwd)"
lp() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf "%s" "$1"; fi; }
IFS=',' read -r -a fx <<< "$fixtures"
IFS=',' read -r -a vs <<< "$variants"

if [ -n "$(adb shell pidof $pkg:game | tr -d '\r')" ]; then echo "a game is running on the phone: not starting the bench" >&2; exit 1; fi
adb shell mkdir -p "$remote/fixtures" "$remote/variants" "$remote/results"
for f in "${fx[@]}"; do
  local_size=$(stat -c %s "$bench/fixtures/$f.vsgd")
  remote_size=$(adb shell stat -c %s "$remote/fixtures/$f.vsgd" 2>/dev/null | tr -d '\r' || true)
  [ "$local_size" = "$remote_size" ] || adb push "$(lp "$bench/fixtures/$f.vsgd")" "$remote/fixtures/$f.vsgd" >/dev/null
done
for v in "${vs[@]}"; do
  adb shell rm -rf "$remote/variants/$v"
  [ -d "$bench/variants/$v" ] && adb push "$(lp "$bench/variants/$v")" "$remote/variants/" >/dev/null
done

start=$(date +%s)
for ((r = 0; r < rounds; r++)); do
  order=("${vs[@]}")
  if ((r % 2 == 1)); then order=(); for ((i = ${#vs[@]} - 1; i >= 0; i--)); do order+=("${vs[i]}"); done; fi
  for f in "${fx[@]}"; do
    for v in "${order[@]}"; do
      adb shell am start -W -n "$pkg/$pkg.BenchActivity" --es bench "'fixture=$f;variant=$v;reps=$reps;run=$run;${PARAM:-}'" >/dev/null
      for ((t = 0; t < 240; t++)); do
        [ -z "$(adb shell pidof $pkg:bench | tr -d '\r')" ] && break
        sleep 0.5
      done
      echo "round $r $f $v: $(( $(date +%s) - start )) s"
    done
  done
done
mkdir -p "$bench/results"
adb pull "$remote/results/$run.tsv" "$(lp "$bench/results/$run.tsv")" >/dev/null 2>&1 || true
# flicker runs (PARAM=mode=flicker): one row per run, and the flagged frames' pictures
if adb pull "$remote/results/$run.flicker.tsv" "$(lp "$bench/results/$run.flicker.tsv")" >/dev/null 2>&1; then
  adb pull "$remote/results/${run}_flicker" "$(lp "$bench/results/")" >/dev/null 2>&1 || true
  echo "fixture variant pace frames flagged fps frames-flagged"; cat "$bench/results/$run.flicker.tsv"
  exit 0
fi

adb shell cat "$remote/results/errors.txt" 2>/dev/null | tail -n 5 || true
echo "$rounds rounds in $(( $(date +%s) - start )) s; results $bench/results/$run.tsv"
if [ -n "${PERF:-}" ]; then
  $PERF bench table "$(lp "$bench/results/$run.tsv")" --base "${vs[0]}" --scenes "${SCENES:-3}"
else
  dotnet run --project "$(lp "$here/tools/VitaSharp.PerfTools")" -c Release -p:VitaSharpDeveloper=true -m:2 -v q -- \
    bench table "$(lp "$bench/results/$run.tsv")" --base "${vs[0]}" --scenes "${SCENES:-3}"
fi
