#!/usr/bin/env bash
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
title="${1:?usage: android-device-test.sh TITLEID [SECONDS] [INPUT]}"
seconds="${2:-90}"
input="${3:-}"
step="${STEP:-15}"
out="${OUT:-$PWD/android-device-test/$title}"
if [ -z "${DATA:-}" ]; then
  if [ -n "${APPDATA:-}" ]; then DATA="$APPDATA/VitaSharp"; else DATA="$HOME/.local/share/VitaSharp"; fi
fi
pkg=io.github.vitasharp
files="/sdcard/Android/data/$pkg/files"
mkdir -p "$out/shots"
# adb on Windows (Git Bash) needs Windows paths for local files: path conversion is off for the device paths
lp() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf "%s" "$1"; fi; }

adb get-state >/dev/null
activity="$(adb shell cmd package resolve-activity --brief $pkg | tr -d '\r' | tail -n 1)"
[ -n "$activity" ] || { echo "VitaSharp ($pkg) is not installed on the phone" >&2; exit 1; }

pushed=0
if ! adb shell "[ -d $files/fs/ux0/app/$title ]"; then
  [ -d "$DATA/fs/ux0/app/$title" ] || { echo "$title is neither on the phone nor in $DATA/fs/ux0/app" >&2; exit 1; }
  echo "pushing $title ($(du -sh "$DATA/fs/ux0/app/$title" | cut -f1))"
  adb push "$(lp "$DATA/fs/ux0/app/$title")" "$files/fs/ux0/app/" >/dev/null
  if [ -d "$DATA/fs/ux0/license/$title" ]; then
    adb shell mkdir -p "$files/fs/ux0/license"
    adb push "$(lp "$DATA/fs/ux0/license/$title")" "$files/fs/ux0/license/" >/dev/null
  fi
  pushed=1
fi

{
  [ -n "$input" ] && echo "VITASHARP_ANDROID_INPUT=$input"
  echo "VITASHARP_AUDIO_DUMP=devtest_audio"
  if [ -n "${EXTRA_ENV:-}" ]; then echo "$EXTRA_ENV" | tr ';' '\n'; fi
} > "$out/env.txt"
adb shell am force-stop $pkg
adb shell rm -rf "$files/devtest_audio"
adb push "$(lp "$out/env.txt")" "$files/config/env.txt" >/dev/null
adb shell am start -n "$activity" --es titleId "$title" >/dev/null

for ((t = step; t <= seconds; t += step)); do
  sleep "$step"
  adb exec-out screencap -p > "$out/shots/$(printf '%03d' "$t")s.png"
done

adb pull "$files/logs/last.log" "$(lp "$out/last.log")" >/dev/null || true
adb pull "$files/devtest_audio" "$(lp "$out/audio")" >/dev/null 2>&1 || true
adb shell am force-stop $pkg
adb shell rm -rf "$files/devtest_audio" "$files/config/env.txt"
if [ "$pushed" = 1 ] && [ "${KEEP:-0}" != 1 ]; then
  adb shell rm -rf "$files/fs/ux0/app/$title" "$files/fs/ux0/license/$title"
  echo "removed $title from the phone"
fi

{
  echo "== $title, ${seconds} s"
  grep -E '\[perf\] [0-9]' "$out/last.log" || true
  grep -E 'CPU backend: ' "$out/last.log" | tail -n 1 || true
  grep -E '\[perf\] audio|Audio: AAudio' "$out/last.log" | tail -n 1 || true
  grep -E 'GPU quirks|Mobile paths' "$out/last.log" | head -n 2 || true
  echo "errors: $(grep -c '\[Error\]' "$out/last.log" || true), warnings: $(grep -c '\[Warn \]' "$out/last.log" || true)"
  grep -E '\[Error\]' "$out/last.log" | head -n 5 || true
} | tee "$out/summary.txt"
