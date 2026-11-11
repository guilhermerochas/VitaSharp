#!/usr/bin/env bash
#
# Keeps the phone's screen on while the Android scripts work (HyperOS refuses `svc power stayon` and `adb shell input`):
# a background loop checks `dumpsys power` every INTERVAL seconds and reopens the developer APK's AwakeActivity (a blank
# screen with KeepScreenOn) only when mWakefulness is not Awake. When the phone is offline, unauthorized or its screen
# stays off, it plays an alert sound every 90 s. The Android scripts call `start` first and `stop` when they finish.
# (Play() is asynchronous: the PowerShell process stays until the sound has played.)
# Usage: build/android-awake.sh start|stop|check|reopen   (same as build/android-awake.ps1; env: SERIAL, INTERVAL=30)
#   start   starts the loop (nothing when one is running)
#   stop    ends the loop and closes the AwakeActivity
#   check   one check now, printing the state (also what the loop does)
#   reopen  opens the AwakeActivity now (after an `am force-stop` of the app, which kills it too)

set -uo pipefail
export MSYS_NO_PATHCONV=1

readonly PKG=io.github.vitasharp
readonly AWAKE="${PKG}/${PKG}.AwakeActivity"
readonly INTERVAL="${INTERVAL:-30}"
readonly PIDFILE="${TMPDIR:-/tmp}/vitasharp-android-awake${SERIAL:+-${SERIAL}}.pid"
readonly USAGE="usage: android-awake.sh start|stop|check|reopen"

#######################################
# adb, on SERIAL's phone when it is set.
# Arguments:
#   The adb arguments.
#######################################
adb_() {
  if [[ -n "${SERIAL:-}" ]]; then
    adb -s "${SERIAL}" "$@"
  else
    adb "$@"
  fi
}

#######################################
# Plays the alert sound (Windows) or rings the terminal bell.
#######################################
alert() {
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
      powershell -NoProfile -c "[System.Media.SystemSounds]::Exclamation.Play(); Start-Sleep -Milliseconds 1500" \
        >/dev/null 2>&1
      ;;
    *) printf '\a' >/dev/tty 2>/dev/null || true ;;
  esac
}

#######################################
# Opens the AwakeActivity.
# Outputs:
#   A note to stdout when the activity is not available (developer APK needed).
#######################################
reopen() {
  local out
  out=$(adb_ shell am start -n "${AWAKE}" 2>&1)
  case "${out}" in
    *Error* | *"does not exist"*) echo "AwakeActivity not available (developer APK needed): ${out}" ;;
  esac
}

#######################################
# timeout(1) is GNU coreutils: macOS has none by default (gtimeout with Homebrew coreutils); perl is always there.
# Arguments:
#   Seconds, then the command.
#######################################
to_() {
  if command -v timeout >/dev/null 2>&1; then
    timeout "$@"
  elif command -v gtimeout >/dev/null 2>&1; then
    gtimeout "$@"
  else
    perl -e 'alarm shift; exec @ARGV' "$@"
  fi
}

#######################################
# One check. An adb call that hangs while the phone drops off the USB is cut after 20 s, so the loop keeps alerting.
# Outputs:
#   'device' (awake), 'asleep' (reopened the activity), or the adb state (offline, unauthorized, absent,
#   unresponsive) to stdout.
#######################################
check() {
  local state wake
  if [[ -n "${SERIAL:-}" ]]; then
    state=$(to_ 20 adb devices 2>/dev/null | awk -v s="${SERIAL}" '$1==s{print $2}')
  else
    state=$(to_ 20 adb devices 2>/dev/null | awk 'NR>1 && NF==2{print $2; exit}')
  fi
  state=${state:-absent}
  if [[ "${state}" != "device" ]]; then
    echo "${state}"
    return 0
  fi
  wake=$(to_ 20 "$0" dumpsys-power 2>/dev/null | tr -d '\r' | grep -m1 -o 'mWakefulness=[A-Za-z]*' | cut -d= -f2)
  if [[ -z "${wake}" ]]; then
    echo unresponsive
    return 0
  fi
  if [[ "${wake}" == "Awake" ]]; then
    echo device
    return 0
  fi
  reopen
  echo asleep
}

#######################################
# The background loop: a check every INTERVAL seconds, an alert at most every 90 s while the phone is not usable.
#######################################
watch_loop() {
  local last_alert=0 bad=0 s now
  while true; do
    s=$(check)
    now=$(date +%s)
    if [[ "${s}" == device ]]; then
      bad=0
    else
      bad=$((bad + 1))
      # an asleep screen gets one reopen before the alert; offline/unauthorized alert at once
      if [[ "${s}" != asleep || "${bad}" -ge 2 ]] && ((now - last_alert >= 90)); then
        alert
        last_alert=${now}
      fi
    fi
    sleep "${INTERVAL}"
  done
}

main() {
  local action="${1:?${USAGE}}"
  local s
  case "${action}" in
    -h | --help) sed -n '3,12s/^# \{0,1\}//p' "${BASH_SOURCE[0]}" ;;
    dumpsys-power) adb_ shell dumpsys power ;; # internal: check runs it under timeout
    check)
      s=$(check)
      echo "phone: ${s}"
      if [[ "${s}" != device ]]; then
        alert
      fi
      ;;
    reopen) reopen ;;
    start)
      if [[ -f "${PIDFILE}" ]] && kill -0 "$(cat "${PIDFILE}")" 2>/dev/null; then
        echo "screen watchdog already running"
        return 0
      fi
      nohup "$0" loop >/dev/null 2>&1 &
      echo $! >"${PIDFILE}"
      echo "screen watchdog started (pid $!, every ${INTERVAL} s)"
      check >/dev/null
      ;;
    stop)
      if [[ -f "${PIDFILE}" ]]; then
        kill "$(cat "${PIDFILE}")" 2>/dev/null || true
        rm -f "${PIDFILE}"
      fi
      adb_ shell am start -n "${AWAKE}" --ez finish true >/dev/null 2>&1 || true
      echo "screen watchdog stopped"
      ;;
    loop) watch_loop ;;
    *)
      echo "${USAGE}" >&2
      return 1
      ;;
  esac
}

main "$@"
