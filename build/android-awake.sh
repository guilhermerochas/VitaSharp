#!/usr/bin/env bash
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
action="${1:?usage: android-awake.sh start|stop|check|reopen}"
interval="${INTERVAL:-30}"
pkg=io.github.vitasharp
awake="$pkg/$pkg.AwakeActivity"
pidfile="${TMPDIR:-/tmp}/vitasharp-android-awake${SERIAL:+-$SERIAL}.pid"
adb_() { if [ -n "${SERIAL:-}" ]; then adb -s "$SERIAL" "$@"; else adb "$@"; fi; }

alert() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) powershell -NoProfile -c "[System.Media.SystemSounds]::Exclamation.Play(); Start-Sleep -Milliseconds 1500" >/dev/null 2>&1 ;;
    *) printf '\a' > /dev/tty 2>/dev/null || true ;;
  esac
}

reopen() {
  local out
  out=$(adb_ shell am start -n "$awake" 2>&1)
  case "$out" in *Error*|*"does not exist"*) echo "AwakeActivity not available (developer APK needed): $out" ;; esac
}

# one check: prints 'device' (awake), 'asleep' (reopened the activity), or the adb state (offline, unauthorized, absent)
check() {
  local state wake
  if [ -n "${SERIAL:-}" ]; then state=$(adb devices 2>/dev/null | awk -v s="$SERIAL" '$1==s{print $2}')
  else state=$(adb devices 2>/dev/null | awk 'NR>1 && NF==2{print $2; exit}'); fi
  state=${state:-absent}
  if [ "$state" != "device" ]; then echo "$state"; return; fi
  wake=$(adb_ shell dumpsys power 2>/dev/null | tr -d '\r' | grep -m1 -o 'mWakefulness=[A-Za-z]*' | cut -d= -f2)
  if [ "$wake" = "Awake" ]; then echo device; return; fi
  reopen
  echo asleep
}

case "$action" in
  check) s=$(check); echo "phone: $s"; [ "$s" = device ] || alert ;;
  reopen) reopen ;;
  start)
    if [ -f "$pidfile" ] && kill -0 "$(cat "$pidfile")" 2>/dev/null; then echo "screen watchdog already running"; exit 0; fi
    nohup "$0" loop > /dev/null 2>&1 &
    echo $! > "$pidfile"
    echo "screen watchdog started (pid $!, every $interval s)"
    check > /dev/null
    ;;
  stop)
    if [ -f "$pidfile" ]; then kill "$(cat "$pidfile")" 2>/dev/null || true; rm -f "$pidfile"; fi
    adb_ shell am start -n "$awake" --ez finish true > /dev/null 2>&1 || true
    echo "screen watchdog stopped"
    ;;
  loop)
    last_alert=0; bad=0
    while true; do
      s=$(check); now=$(date +%s)
      if [ "$s" = device ]; then bad=0
      else
        bad=$((bad + 1))
        # an asleep screen gets one reopen before the alert; offline/unauthorized alert at once
        if { [ "$s" != asleep ] || [ "$bad" -ge 2 ]; } && [ $((now - last_alert)) -ge 90 ]; then alert; last_alert=$now; fi
      fi
      sleep "$interval"
    done
    ;;
  *) echo "usage: android-awake.sh start|stop|check|reopen" >&2; exit 1 ;;
esac
