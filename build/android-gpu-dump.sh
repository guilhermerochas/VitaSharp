#!/usr/bin/env bash
#
# Records a GPU dump of a game on a USB-connected Android phone and replays it on the PC with the Vulkan validation
# layer, synchronization validation included (missing barriers, write-after-read hazards: the usual causes of flicker
# on tile-based GPUs). The phone only captures; the investigation runs on the PC (docs/notes/gpu-dump.md).
# Usage: build/android-gpu-dump.sh TITLEID FIRST:LAST [SECONDS] [INPUT]   (same as build/android-gpu-dump.ps1)
#   FIRST:LAST are renderer presents of the running executable (a LoadExec starts them again from 1).
#   env: OUT (results folder, default ./android-gpu-dump/TITLEID), DESKTOP (VitaSharp.Desktop executable for the
#   replay; empty = no replay), REPLAY_ARGS (more replay-gpu options), EXTRA_ENV (more env.txt lines, ';'-separated).

set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PKG=io.github.vitasharp
readonly FILES="/sdcard/Android/data/${PKG}/files"
readonly USAGE="usage: android-gpu-dump.sh TITLEID FIRST:LAST [SECONDS] [INPUT]"

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
# Replays the dump with Khronos validation, synchronization validation included (layer settings through the
# environment), and prints the message counts.
# Arguments:
#   Results folder, dump file.
#######################################
replay_with_validation() {
  local out="$1" dump="$2"
  # shellcheck disable=SC2086  # REPLAY_ARGS holds several options
  VK_KHRONOS_VALIDATION_VALIDATE_SYNC=true \
    "${DESKTOP}" replay-gpu "$(lp "${out}/${dump}")" --out="$(lp "${out}/replay")" --validation ${REPLAY_ARGS:-} \
    >"${out}/replay.txt" 2>&1 || true
  echo "validation messages: $(grep -c 'VUID\|SYNC-' "${out}/replay/log.txt" 2>/dev/null || echo 0)"
  grep -o 'VUID-[A-Za-z0-9_-]*\|SYNC-[A-Z0-9_-]*' "${out}/replay/log.txt" 2>/dev/null \
    | sort | uniq -c | sort -rn | head -n 10 || true
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,9s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local title="${1:?${USAGE}}"
  local frames="${2:?${USAGE}}"
  local seconds="${3:-300}"
  local input="${4:-}"
  local out="${OUT:-${PWD}/android-gpu-dump/${title}}"
  local dump="${title}.vsgd"

  "${HERE}/android-awake.sh" start # the phone's screen is kept on until the end (build/android-awake.sh)
  trap '"${HERE}/android-awake.sh" stop' EXIT
  mkdir -p "${out}"

  local activity
  activity="$(adb shell cmd package resolve-activity --brief "${PKG}" | tr -d '\r' | tail -n 1)"
  {
    echo "VITASHARP_GPU_DUMP_AT=${frames}:${dump}"
    if [[ -n "${input}" ]]; then
      echo "VITASHARP_ANDROID_INPUT=${input}"
    fi
    if [[ -n "${EXTRA_ENV:-}" ]]; then
      echo "${EXTRA_ENV}" | tr ';' '\n'
    fi
  } >"${out}/env.txt"
  adb shell am force-stop "${PKG}"
  "${HERE}/android-awake.sh" reopen
  adb shell rm -rf "${FILES}/gpudumps"
  adb push "$(lp "${out}/env.txt")" "${FILES}/config/env.txt" >/dev/null
  adb shell am start -n "${activity}" --es titleId "${title}" >/dev/null
  # the recorder logs "[gpudump] saved" once the file is complete
  local t
  for ((t = 0; t < seconds; t += 5)); do
    sleep 5
    if adb shell "grep -q 'gpudump\] saved' ${FILES}/logs/last.log" 2>/dev/null; then
      break
    fi
  done
  adb pull "${FILES}/gpudumps/${dump}" "$(lp "${out}/${dump}")" >/dev/null \
    || echo "no dump recorded (frames ${frames} not reached in ${seconds} s?)" >&2
  adb pull "${FILES}/logs/last.log" "$(lp "${out}/last.log")" >/dev/null || true
  adb shell am force-stop "${PKG}"
  "${HERE}/android-awake.sh" reopen
  adb shell rm -rf "${FILES}/gpudumps" "${FILES}/config/env.txt"
  if [[ ! -f "${out}/${dump}" ]]; then
    return 1
  fi
  echo "dump: ${out}/${dump}"

  if [[ -n "${DESKTOP:-}" ]]; then
    replay_with_validation "${out}" "${dump}"
  fi
}

main "$@"
