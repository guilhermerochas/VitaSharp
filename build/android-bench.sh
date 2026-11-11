#!/usr/bin/env bash
#
# GPU bench on a USB-connected Android phone with the developer APK (-p:VitaSharpDeveloper=true): pushes the fixtures
# and variants, runs every (fixture, variant) ROUNDS times interleaved A/B/B/A (one process per run, REPS timed
# repetitions each), pulls the results and prints fixture x variant -> median GPU ms, spread and delta
# (tools/dev/README.md).
# Usage: build/android-bench.sh FIXTURES VARIANTS [ROUNDS] [REPS]   (same as build/android-bench.ps1)
#   FIXTURES, VARIANTS: comma-separated names (BENCH_DIR/fixtures/NAME.vsgd, BENCH_DIR/variants/NAME; "base" = none).
#   env: BENCH_DIR (local folder with fixtures/ and variants/, default ./bench), PARAM (more bench parameters,
#   ';'-separated, for every run), PERF (vitasharp-perf command for the table; default dotnet run of
#   tools/VitaSharp.PerfTools with -p:VitaSharpDeveloper=true), SCENES (top scenes per row, default 3).

set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PKG=io.github.vitasharp
readonly REMOTE="/sdcard/Android/data/${PKG}/files/bench"
readonly USAGE="usage: android-bench.sh FIXTURES VARIANTS [ROUNDS] [REPS]"

#######################################
# A local path as adb wants it (Windows path under Git Bash).
# Arguments:
#   Local path.
#######################################
lp() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf "%s" "$1"
  fi
}

#######################################
# Prints the fixture x variant table of one run's results.
# Arguments:
#   Results .tsv, base variant.
#######################################
print_table() {
  local tsv="$1" base="$2"
  local tools
  tools="$(lp "${HERE}/../tools/VitaSharp.PerfTools")"
  if [[ -n "${PERF:-}" ]]; then
    # shellcheck disable=SC2086  # PERF may be a command with arguments
    ${PERF} bench table "$(lp "${tsv}")" --base "${base}" --scenes "${SCENES:-3}"
  else
    # dotnet run does not take MSBuild's -m (it reached the tool as an argument): build first, then run the build
    dotnet build "${tools}" -c Release -p:VitaSharpDeveloper=true -p:UseSharedCompilation=false -m:2 -v q -nologo \
      >/dev/null
    dotnet run --no-build --project "${tools}" -c Release -p:VitaSharpDeveloper=true -- \
      bench table "$(lp "${tsv}")" --base "${base}" --scenes "${SCENES:-3}"
  fi
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,11s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local fixtures="${1:?${USAGE}}"
  local variants="${2:?${USAGE}}"
  local rounds="${3:-4}"
  local reps="${4:-3}"
  local bench="${BENCH_DIR:-${PWD}/bench}"
  "${HERE}/android-awake.sh" start # the phone's screen is kept on until the end (build/android-awake.sh)
  trap '"${HERE}/android-awake.sh" stop' EXIT
  local run
  run="$(date +%Y%m%d_%H%M%S)"
  local -a fx vs
  IFS=',' read -r -a fx <<<"${fixtures}"
  IFS=',' read -r -a vs <<<"${variants}"

  if [[ -n "$(adb shell pidof "${PKG}:game" | tr -d '\r')" ]]; then
    echo "a game is running on the phone: not starting the bench" >&2
    return 1
  fi
  adb shell mkdir -p "${REMOTE}/fixtures" "${REMOTE}/variants" "${REMOTE}/results"
  local f v local_size remote_size
  for f in "${fx[@]}"; do
    local_size=$(wc -c <"${bench}/fixtures/${f}.vsgd" | tr -d ' ') # portable (GNU stat -c / BSD stat -f differ)
    remote_size=$(adb shell stat -c %s "${REMOTE}/fixtures/${f}.vsgd" 2>/dev/null | tr -d '\r' || true)
    if [[ "${local_size}" != "${remote_size}" ]]; then
      adb push "$(lp "${bench}/fixtures/${f}.vsgd")" "${REMOTE}/fixtures/${f}.vsgd" >/dev/null
    fi
  done
  for v in "${vs[@]}"; do
    adb shell rm -rf "${REMOTE}/variants/${v}"
    if [[ -d "${bench}/variants/${v}" ]]; then
      adb push "$(lp "${bench}/variants/${v}")" "${REMOTE}/variants/" >/dev/null
    fi
  done

  local start r i t
  local -a order
  start=$(date +%s)
  for ((r = 0; r < rounds; r++)); do
    order=("${vs[@]}")
    if ((r % 2 == 1)); then
      order=()
      for ((i = ${#vs[@]} - 1; i >= 0; i--)); do
        order+=("${vs[i]}")
      done
    fi
    for f in "${fx[@]}"; do
      for v in "${order[@]}"; do
        adb shell am start -W -n "${PKG}/${PKG}.BenchActivity" \
          --es bench "'fixture=${f};variant=${v};reps=${reps};run=${run};${PARAM:-}'" >/dev/null
        for ((t = 0; t < 240; t++)); do
          if [[ -z "$(adb shell pidof "${PKG}:bench" | tr -d '\r')" ]]; then
            break
          fi
          sleep 0.5
        done
        echo "round ${r} ${f} ${v}: $(($(date +%s) - start)) s"
      done
    done
  done
  mkdir -p "${bench}/results"
  adb pull "${REMOTE}/results/${run}.tsv" "$(lp "${bench}/results/${run}.tsv")" >/dev/null 2>&1 || true
  # stamps=1 runs: the per-op intervals (results/RUN.stamps.tsv)
  adb pull "${REMOTE}/results/${run}.stamps.tsv" "$(lp "${bench}/results/${run}.stamps.tsv")" >/dev/null 2>&1 || true
  # flicker runs (PARAM=mode=flicker): one row per run, and the flagged frames' pictures
  if adb pull "${REMOTE}/results/${run}.flicker.tsv" "$(lp "${bench}/results/${run}.flicker.tsv")" >/dev/null 2>&1; then
    adb pull "${REMOTE}/results/${run}_flicker" "$(lp "${bench}/results/")" >/dev/null 2>&1 || true
    echo "fixture variant pace frames flagged fps frames-flagged"
    cat "${bench}/results/${run}.flicker.tsv"
    return 0
  fi

  adb shell cat "${REMOTE}/results/errors.txt" 2>/dev/null | tail -n 5 || true
  echo "${rounds} rounds in $(($(date +%s) - start)) s; results ${bench}/results/${run}.tsv"
  print_table "${bench}/results/${run}.tsv" "${vs[0]}"
}

main "$@"
