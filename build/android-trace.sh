#!/usr/bin/env bash
# Records a Perfetto trace of a game on a USB-connected Android phone running the developer APK (-p:VitaSharpDeveloper=true):
# CPU scheduling, CPU/GPU frequency, GPU counters and render stages when the driver offers them, and the emulator's own
# markers (VITASHARP_TRACE=1: guest frames, GPU worker frames and submits, sampled HLE calls, JIT compiles, audio grains).
# Open the .perfetto-trace at https://ui.perfetto.dev (tools/dev/README.md, "Reading a trace").
# Usage: build/android-trace.sh TITLEID [SECONDS] [DELAY] [INPUT]   (same as build/android-trace.ps1)
#   SECONDS traced (default 10) after DELAY seconds of play (default 40); INPUT = VITASHARP_ANDROID_INPUT script.
#   env: OUT (default ./android-trace), HLE (one HLE call in HLE traced, default 64), EXTRA_ENV (';'-separated env.txt lines).
set -euo pipefail
export MSYS_NO_PATHCONV=1
title="${1:?usage: android-trace.sh TITLEID [SECONDS] [DELAY] [INPUT]}"
seconds="${2:-10}"
delay="${3:-40}"
input="${4:-}"
out="${OUT:-$PWD/android-trace}"
pkg=io.github.vitasharp
files="/sdcard/Android/data/$pkg/files"
trace="/data/misc/perfetto-traces/vitasharp.perfetto-trace"
here="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$out"
lp() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf "%s" "$1"; fi; }
if [ -n "$(adb shell pidof $pkg:game | tr -d '\r')" ]; then echo "a game is running on the phone: not starting" >&2; exit 1; fi

{
  echo "VITASHARP_TRACE=1"
  echo "VITASHARP_TRACE_HLE=${HLE:-64}"
  [ -n "$input" ] && echo "VITASHARP_ANDROID_INPUT=$input"
  if [ -n "${EXTRA_ENV:-}" ]; then echo "$EXTRA_ENV" | tr ';' '\n'; fi
} > "$out/env.txt"
{ echo "duration_ms: $((seconds * 1000))"; cat "$here/tools/dev/perfetto-vitasharp.pbtx"; } > "$out/config.pbtx"

activity="$(adb shell cmd package resolve-activity --brief $pkg | tr -d '\r' | tail -n 1)"
adb shell am force-stop $pkg
adb push "$(lp "$out/env.txt")" "$files/config/env.txt" >/dev/null
adb shell am start -n "$activity" --es titleId "$title" >/dev/null
sleep "$delay"
adb shell rm -f "$trace"
adb shell perfetto --txt -c - -o "$trace" < "$out/config.pbtx" > "$out/perfetto.txt" 2>&1 || cat "$out/perfetto.txt" >&2
adb pull "$trace" "$(lp "$out/$title.perfetto-trace")" >/dev/null
adb pull "$files/logs/last.log" "$(lp "$out/last.log")" >/dev/null || true
adb shell rm -f "$trace" "$files/config/env.txt"
adb shell am force-stop $pkg
ls -la "$out/$title.perfetto-trace"
echo "open $out/$title.perfetto-trace at https://ui.perfetto.dev"
