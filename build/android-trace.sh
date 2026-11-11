#!/usr/bin/env bash
#
# Records a Perfetto trace of a game on a USB-connected Android phone running the developer APK
# (-p:VitaSharpDeveloper=true): CPU scheduling, CPU/GPU frequency, GPU counters and render stages when the driver
# offers them, and the emulator's own markers (VITASHARP_TRACE=1: guest frames, GPU worker frames and submits, sampled
# HLE calls, JIT compiles, audio grains). Open the .perfetto-trace at https://ui.perfetto.dev (tools/dev/README.md,
# "Reading a trace").
# Usage: build/android-trace.sh TITLEID [SECONDS] [DELAY] [INPUT]   (same as build/android-trace.ps1)
#   SECONDS traced (default 10) after DELAY seconds of play (default 40); INPUT = VITASHARP_ANDROID_INPUT script.
#   env: OUT (default ./android-trace), HLE (one HLE call in HLE traced, default 64), EXTRA_ENV (';'-separated
#   env.txt lines).

set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PKG=io.github.vitasharp
readonly FILES="/sdcard/Android/data/${PKG}/files"
readonly TRACE="/data/misc/perfetto-traces/vitasharp.perfetto-trace"
readonly USAGE="usage: android-trace.sh TITLEID [SECONDS] [DELAY] [INPUT]"

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

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,12s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local title="${1:?${USAGE}}"
  local seconds="${2:-10}"
  local delay="${3:-40}"
  local input="${4:-}"
  local out="${OUT:-${PWD}/android-trace}"
  local root
  root="$(cd "${HERE}/.." && pwd)"

  "${HERE}/android-awake.sh" start # the phone's screen is kept on until the end (build/android-awake.sh)
  trap '"${HERE}/android-awake.sh" stop' EXIT
  mkdir -p "${out}"
  if [[ -n "$(adb shell pidof "${PKG}:game" | tr -d '\r')" ]]; then
    echo "a game is running on the phone: not starting" >&2
    return 1
  fi

  {
    echo "VITASHARP_TRACE=1"
    echo "VITASHARP_TRACE_HLE=${HLE:-64}"
    if [[ -n "${input}" ]]; then
      echo "VITASHARP_ANDROID_INPUT=${input}"
    fi
    if [[ -n "${EXTRA_ENV:-}" ]]; then
      echo "${EXTRA_ENV}" | tr ';' '\n'
    fi
  } >"${out}/env.txt"
  {
    echo "duration_ms: $((seconds * 1000))"
    cat "${root}/tools/dev/perfetto-vitasharp.pbtx"
  } >"${out}/config.pbtx"

  local activity
  activity="$(adb shell cmd package resolve-activity --brief "${PKG}" | tr -d '\r' | tail -n 1)"
  adb shell am force-stop "${PKG}"
  "${HERE}/android-awake.sh" reopen
  adb push "$(lp "${out}/env.txt")" "${FILES}/config/env.txt" >/dev/null
  adb shell am start -n "${activity}" --es titleId "${title}" >/dev/null
  sleep "${delay}"
  adb shell rm -f "${TRACE}"
  adb shell perfetto --txt -c - -o "${TRACE}" <"${out}/config.pbtx" >"${out}/perfetto.txt" 2>&1 \
    || cat "${out}/perfetto.txt" >&2
  adb pull "${TRACE}" "$(lp "${out}/${title}.perfetto-trace")" >/dev/null
  adb pull "${FILES}/logs/last.log" "$(lp "${out}/last.log")" >/dev/null || true
  adb shell rm -f "${TRACE}" "${FILES}/config/env.txt"
  adb shell am force-stop "${PKG}"
  "${HERE}/android-awake.sh" reopen
  ls -la "${out}/${title}.perfetto-trace"
  echo "open ${out}/${title}.perfetto-trace at https://ui.perfetto.dev"
}

main "$@"
