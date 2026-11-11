#!/usr/bin/env bash
#
# Analyses a capture recorded with the pause menu ("Gravar tudo" in every build, docs/notes/session-capture.md;
# "Gravar tempo/conteúdo" in the developer APK): pulls it from the phone (or takes a local folder) and prints one
# summary: for "Gravar tudo", the report of vitasharp-perf capture; fps and the timing part, the PERF lines, the busiest
# HLE functions and threads the log reports, the audio check of every guest port WAV, and, from a replay of the GPU
# dump on this PC, the flicker check over every frame and the GPU ms per scene of one frame (tools/dev/README.md,
# "Captures").
# Usage: build/android-capture-analyze.sh [CAPTURE_NAME|LOCAL_FOLDER]   (same as .ps1; no argument = newest on the phone)
#   env: OUT (default ./captures), PERF (vitasharp-perf built with -p:VitaSharpDeveloper=true; default dotnet run),
#   GPU_PROFILE (replay profile, default mali), DESKTOP (VitaSharp.Desktop executable: adds a validation-layer replay).

set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PKG=io.github.vitasharp
readonly REMOTE="/sdcard/Android/data/${PKG}/files/captures"

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
# Runs vitasharp-perf: PERF when set, else dotnet run of tools/VitaSharp.PerfTools (developer build).
# Arguments:
#   The vitasharp-perf arguments.
#######################################
perf() {
  if [[ -n "${PERF:-}" ]]; then
    "${PERF}" "$@"
  else
    dotnet run --project "$(lp "${HERE}/../tools/VitaSharp.PerfTools")" -c Release -p:VitaSharpDeveloper=true -m:2 \
      -v q -- "$@"
  fi
}

#######################################
# Replays the capture's GPU dump on this PC: flicker check, GPU ms per scene of the middle frame and, with DESKTOP, a
# validation-layer replay.
# Arguments:
#   Capture folder, dump file.
#######################################
analyze_dump() {
  local dir="$1" dump="$2"
  local profile="${GPU_PROFILE:-mali}"
  local bench="${dir}/bench"
  mkdir -p "${bench}/fixtures"
  cp -f "${dump}" "${bench}/fixtures/capture.vsgd"
  echo "== flicker over the dump (replay on this PC, ${profile} profile)"
  perf bench --child "fixture=capture;mode=flicker;loops=1;gpuprofile=${profile};run=analyze" --root "$(lp "${bench}")" \
    | grep "BENCH\|\[bench\] frame" | head -n 15
  local frames mid
  frames=$(perf bench cut "$(lp "${dump}")" | grep -c '^frame')
  mid=$((frames / 2))
  echo "== GPU ms per scene, frame ${mid} of ${frames} (this PC's GPU: relative costs, not phone ms; run" \
    "build/android-bench.* with this fixture for phone ms)"
  perf bench cut "$(lp "${dump}")" --frame "${mid}" --name frame_mid --root "$(lp "${bench}")" >/dev/null
  perf bench --child "fixture=frame_mid;reps=3;gpuprofile=${profile};run=analyze" --root "$(lp "${bench}")" \
    | grep "BENCH\|\[bench\]   s" | head -n 30
  if [[ -n "${DESKTOP:-}" ]]; then
    echo "== Vulkan validation (replay-gpu --validation, mali profile)"
    VK_KHRONOS_VALIDATION_VALIDATE_SYNC=true "${DESKTOP}" replay-gpu "$(lp "${dump}")" \
      --out="$(lp "${dir}/validation")" --validation --gpu-profile=mali >"${dir}/validation.txt" 2>&1 || true
    grep -o 'VUID-[A-Za-z0-9_-]*\|SYNC-[A-Z0-9_-]*' "${dir}/validation/log.txt" 2>/dev/null \
      | sort | uniq -c | sort -rn | head -n 10 || echo "no validation messages"
  fi
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,11s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  "${HERE}/android-awake.sh" start # the phone's screen is kept on until the end (build/android-awake.sh)
  trap '"${HERE}/android-awake.sh" stop' EXIT
  local out="${OUT:-${PWD}/captures}"
  local arg="${1:-}"
  local dir name
  if [[ -n "${arg}" && -d "${arg}" ]]; then
    dir="$(cd "${arg}" && pwd)"
  else
    name="${arg:-$(adb shell ls -t "${REMOTE}" | tr -d '\r' | grep -v '\.zip$' | head -n 1)}"
    mkdir -p "${out}"
    adb pull "${REMOTE}/${name}" "$(lp "${out}/")" >/dev/null
    dir="${out}/${name}"
  fi
  echo "== ${dir}"
  if [[ -f "${dir}/session.txt" ]]; then
    echo "== Gravar tudo: vitasharp-perf capture (black frames, flashes, presentation order, stalls, audio; with the" \
      "GPU dump and the input)"
    perf capture "$(lp "${dir}")" || true
  else
    grep -v '^$' "${dir}/info.txt" | head -n 12
  fi
  echo "== fps (log, 30 s windows) and capture lines"
  grep -h "\[perf\] [0-9]\|\[capture\]" "${dir}/log.txt" | sed 's/^\[[^]]*\] \[[^]]*\] \[[^]]*\] //' | tail -n 12 || true
  echo "== PERF lines"
  grep -h "^PERF\|PERF " "${dir}/info.txt" | cut -c1-300 | head -n 12 || true
  echo "== HLE and threads (log)"
  grep -h "HLE top\|hot HLE\|thread '" "${dir}/log.txt" | tail -n 12 | cut -c1-200 || true
  if [[ -d "${dir}/audio" ]]; then
    echo "== audio (guest ports)"
    perf audio-check "$(lp "${dir}/audio")" || true
  fi
  local dump
  dump="$(find "${dir}" -maxdepth 1 -name '*.vsgd' 2>/dev/null | sort | head -n 1 || true)"
  if [[ -n "${dump}" ]]; then
    analyze_dump "${dir}" "${dump}"
  fi
}

main "$@"
