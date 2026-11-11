#!/usr/bin/env bash
#
# Runs one game on a USB-connected Android phone and collects what the run did: the session log (logs/last.log), a
# screenshot every STEP seconds, the guest's audio as WAVs (VITASHARP_AUDIO_DUMP), and a summary (fps lines, CPU
# backend, audio, errors). The game's input comes from VITASHARP_ANDROID_INPUT (ScriptedPad syntax, seconds since
# the game first polled the pad): phones may refuse `adb shell input` without a developer security setting.
# A game that is not on the phone is pushed first (app folder and license from the desktop data root) and removed
# again afterwards unless KEEP=1; the phone's other data is never touched (only the app's own files folder).
# Usage: build/android-device-test.sh TITLEID [SECONDS] [INPUT]   (same as build/android-device-test.ps1)
#   env: OUT (results folder, default ./android-device-test/TITLEID), DATA (desktop data root with fs/ux0/app and
#   fs/ux0/license, default ~/.local/share/VitaSharp or %APPDATA%/VitaSharp), KEEP=1, STEP (default 15),
#   EXTRA_ENV (more env.txt lines, separated by ';').

set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly HERE
readonly PKG=io.github.vitasharp
readonly FILES="/sdcard/Android/data/${PKG}/files"
readonly USAGE="usage: android-device-test.sh TITLEID [SECONDS] [INPUT]"

#######################################
# A local path as adb wants it: adb on Windows (Git Bash) needs Windows paths for local files (path conversion is
# off for the device paths).
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
# Pushes the game (app folder and license) from the desktop data root when it is not on the phone.
# Arguments:
#   Title ID, desktop data root.
# Outputs:
#   "1" to stdout when it was pushed (and is to be removed afterwards), "0" otherwise.
#######################################
push_game() {
  local title="$1" data="$2"
  if adb shell "[ -d ${FILES}/fs/ux0/app/${title} ]"; then
    echo 0
    return 0
  fi
  if [[ ! -d "${data}/fs/ux0/app/${title}" ]]; then
    echo "${title} is neither on the phone nor in ${data}/fs/ux0/app" >&2
    return 1
  fi
  echo "pushing ${title} ($(du -sh "${data}/fs/ux0/app/${title}" | cut -f1))" >&2
  adb push "$(lp "${data}/fs/ux0/app/${title}")" "${FILES}/fs/ux0/app/" >/dev/null
  if [[ -d "${data}/fs/ux0/license/${title}" ]]; then
    adb shell mkdir -p "${FILES}/fs/ux0/license"
    adb push "$(lp "${data}/fs/ux0/license/${title}")" "${FILES}/fs/ux0/license/" >/dev/null
  fi
  echo 1
}

#######################################
# Prints the run's summary from the log.
# Arguments:
#   Title ID, seconds, log file.
#######################################
summary() {
  local title="$1" seconds="$2" log="$3"
  echo "== ${title}, ${seconds} s"
  grep -E '\[perf\] [0-9]' "${log}" || true
  grep -E 'CPU backend: ' "${log}" | tail -n 1 || true
  grep -E '\[perf\] audio|Audio: AAudio' "${log}" | tail -n 1 || true
  grep -E 'GPU quirks|Mobile paths' "${log}" | head -n 2 || true
  echo "errors: $(grep -c '\[Error\]' "${log}" || true), warnings: $(grep -c '\[Warn \]' "${log}" || true)"
  grep -E '\[Error\]' "${log}" | head -n 5 || true
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,12s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local title="${1:?${USAGE}}"
  local seconds="${2:-90}"
  local input="${3:-}"
  local step="${STEP:-15}"
  local out="${OUT:-${PWD}/android-device-test/${title}}"
  local data="${DATA:-}"
  if [[ -z "${data}" ]]; then
    if [[ -n "${APPDATA:-}" ]]; then
      data="${APPDATA}/VitaSharp"
    else
      data="${HOME}/.local/share/VitaSharp"
    fi
  fi
  mkdir -p "${out}/shots"

  adb get-state >/dev/null
  "${HERE}/android-awake.sh" start # the phone's screen is kept on until the end (build/android-awake.sh)
  trap '"${HERE}/android-awake.sh" stop' EXIT
  local activity
  activity="$(adb shell cmd package resolve-activity --brief "${PKG}" | tr -d '\r' | tail -n 1)"
  if [[ -z "${activity}" ]]; then
    echo "VitaSharp (${PKG}) is not installed on the phone" >&2
    return 1
  fi

  local pushed
  pushed="$(push_game "${title}" "${data}")"

  {
    if [[ -n "${input}" ]]; then
      echo "VITASHARP_ANDROID_INPUT=${input}"
    fi
    echo "VITASHARP_AUDIO_DUMP=devtest_audio"
    if [[ -n "${EXTRA_ENV:-}" ]]; then
      echo "${EXTRA_ENV}" | tr ';' '\n'
    fi
  } >"${out}/env.txt"
  adb shell am force-stop "${PKG}"
  "${HERE}/android-awake.sh" reopen
  adb shell rm -rf "${FILES}/devtest_audio"
  adb push "$(lp "${out}/env.txt")" "${FILES}/config/env.txt" >/dev/null
  adb shell am start -n "${activity}" --es titleId "${title}" >/dev/null

  local t
  for ((t = step; t <= seconds; t += step)); do
    sleep "${step}"
    adb exec-out screencap -p >"${out}/shots/$(printf '%03d' "${t}")s.png"
  done

  adb pull "${FILES}/logs/last.log" "$(lp "${out}/last.log")" >/dev/null || true
  adb pull "${FILES}/devtest_audio" "$(lp "${out}/audio")" >/dev/null 2>&1 || true
  adb shell am force-stop "${PKG}"
  "${HERE}/android-awake.sh" reopen
  adb shell rm -rf "${FILES}/devtest_audio" "${FILES}/config/env.txt"
  if [[ "${pushed}" == 1 && "${KEEP:-0}" != 1 ]]; then
    adb shell rm -rf "${FILES}/fs/ux0/app/${title}" "${FILES}/fs/ux0/license/${title}"
    echo "removed ${title} from the phone"
  fi

  summary "${title}" "${seconds}" "${out}/last.log" | tee "${out}/summary.txt"
}

main "$@"
