#!/usr/bin/env bash
#
# Records the media parity trace (src/VitaSharp.Core/Media/MediaContract.cs) on an Android phone or the Android
# Emulator with the developer APK installed: pushes the audio clips of tests/VitaSharp.Tests/Fixtures/Media, runs the
# app's MediaParityActivity (MediaCodec H.264, AAC, MP3) and pulls the trace, which MediaParityTests compares with the
# desktop's (FFmpeg): VITASHARP_ANDROID_MEDIA_TRACE=<trace> dotnet test tests/VitaSharp.Tests --filter MediaParityTests
# Usage: build/android-media-parity.sh [OUT]   (same as build/android-media-parity.ps1)
#   OUT    trace file to write (default ./android-media-parity/trace.txt)
#   env:   SERIAL (adb device), SYNC=0 (the old MediaCodec output timing, VITASHARP_MEDIACODEC_SYNC=0), TIMEOUT (s, 120),
#          APP_ENV ("NAME=VALUE;..." set in the app's parity process)

set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PKG=io.github.vitasharp
readonly FILES="/sdcard/Android/data/${PKG}/files/media-parity"
readonly ACTIVITY="${PKG}/${PKG}.MediaParityActivity"
readonly CLIPS="${HERE}/../tests/VitaSharp.Tests/Fixtures/Media"
readonly TIMEOUT="${TIMEOUT:-120}"

#######################################
# adb on SERIAL's device when it is set.
# Arguments:
#   adb arguments.
#######################################
device_adb() {
  if [[ -n "${SERIAL:-}" ]]; then
    adb -s "${SERIAL}" "$@"
  else
    adb "$@"
  fi
}

#######################################
# A local path as adb wants it (Windows paths under Git Bash).
# Arguments:
#   Local path.
#######################################
local_path() {
  if command -v cygpath >/dev/null 2>&1; then
    cygpath -w "$1"
  else
    printf "%s" "$1"
  fi
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,10s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local out="${1:-./android-media-parity/trace.txt}"
  if [[ "$(device_adb get-state 2>/dev/null)" != "device" ]]; then
    echo "no Android device or emulator ready (adb get-state)" >&2
    return 1
  fi
  device_adb shell mkdir -p "${FILES}"
  local clip
  for clip in "${CLIPS}"/*; do
    device_adb push "$(local_path "${clip}")" "${FILES}/" >/dev/null
  done
  device_adb shell rm -f "${FILES}/done.txt" "${FILES}/trace.txt"
  device_adb shell am start -n "${ACTIVITY}" --es sync "'${SYNC:-}'" --es env "'${APP_ENV:-}'" >/dev/null
  local waited=0
  until device_adb shell "[ -f ${FILES}/done.txt ]"; do
    if ((waited >= TIMEOUT)); then
      echo "the parity run did not finish in ${TIMEOUT} s (developer APK installed?)" >&2
      return 1
    fi
    sleep 2
    waited=$((waited + 2))
  done
  local status
  status="$(device_adb shell cat "${FILES}/done.txt" | tr -d '\r')"
  if [[ "${status}" != "ok" ]]; then
    echo "the parity run failed on the device: ${status}" >&2
    return 1
  fi
  mkdir -p "$(dirname "${out}")"
  device_adb pull "${FILES}/trace.txt" "$(local_path "${out}")" >/dev/null
  device_adb pull "${FILES}/parity.log" "$(local_path "${out%.txt}.log")" >/dev/null 2>&1 || true
  echo "trace: ${out}"
  echo "compare: VITASHARP_ANDROID_MEDIA_TRACE=${out} dotnet test tests/VitaSharp.Tests --filter MediaParityTests"
}

main "$@"
