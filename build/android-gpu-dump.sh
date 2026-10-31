#!/usr/bin/env bash
# Records a GPU dump of a game on a USB-connected Android phone and replays it on the PC with the Vulkan validation
# layer, synchronization validation included (missing barriers, write-after-read hazards: the usual causes of flicker
# on tile-based GPUs). The phone only captures; the investigation runs on the PC (docs/notes/gpu-dump.md).
# Usage: build/android-gpu-dump.sh TITLEID FIRST:LAST [SECONDS] [INPUT]   (same as build/android-gpu-dump.ps1)
#   FIRST:LAST are renderer presents of the running executable (a LoadExec starts them again from 1).
#   env: OUT (results folder, default ./android-gpu-dump/TITLEID), DESKTOP (VitaSharp.Desktop executable for the
#   replay; empty = no replay), REPLAY_ARGS (more replay-gpu options), EXTRA_ENV (more env.txt lines, ';'-separated).
set -euo pipefail
export MSYS_NO_PATHCONV=1
title="${1:?usage: android-gpu-dump.sh TITLEID FIRST:LAST [SECONDS] [INPUT]}"
frames="${2:?usage: android-gpu-dump.sh TITLEID FIRST:LAST [SECONDS] [INPUT]}"
seconds="${3:-300}"
input="${4:-}"
out="${OUT:-$PWD/android-gpu-dump/$title}"
pkg=io.github.vitasharp
files="/sdcard/Android/data/$pkg/files"
dump="$title.vsgd"
mkdir -p "$out"
lp() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf "%s" "$1"; fi; }

activity="$(adb shell cmd package resolve-activity --brief $pkg | tr -d '\r' | tail -n 1)"
{
  echo "VITASHARP_GPU_DUMP_AT=$frames:$dump"
  [ -n "$input" ] && echo "VITASHARP_ANDROID_INPUT=$input"
  if [ -n "${EXTRA_ENV:-}" ]; then echo "$EXTRA_ENV" | tr ';' '\n'; fi
} > "$out/env.txt"
adb shell am force-stop $pkg
adb shell rm -rf "$files/gpudumps"
adb push "$(lp "$out/env.txt")" "$files/config/env.txt" >/dev/null
adb shell am start -n "$activity" --es titleId "$title" >/dev/null
# the recorder logs "[gpudump] saved" once the file is complete
for ((t = 0; t < seconds; t += 5)); do
  sleep 5
  if adb shell "grep -q 'gpudump\] saved' $files/logs/last.log" 2>/dev/null; then break; fi
done
adb pull "$files/gpudumps/$dump" "$(lp "$out/$dump")" >/dev/null || echo "no dump recorded (frames $frames not reached in $seconds s?)" >&2
adb pull "$files/logs/last.log" "$(lp "$out/last.log")" >/dev/null || true
adb shell am force-stop $pkg
adb shell rm -rf "$files/gpudumps" "$files/config/env.txt"
[ -f "$out/$dump" ] || exit 1
echo "dump: $out/$dump"

if [ -n "${DESKTOP:-}" ]; then
  # Khronos validation with synchronization validation (layer settings through the environment)
  VK_KHRONOS_VALIDATION_VALIDATE_SYNC=true \
    "$DESKTOP" replay-gpu "$(lp "$out/$dump")" --out="$(lp "$out/replay")" --validation ${REPLAY_ARGS:-} > "$out/replay.txt" 2>&1 || true
  echo "validation messages: $(grep -c 'VUID\|SYNC-' "$out/replay/log.txt" 2>/dev/null || echo 0)"
  grep -o 'VUID-[A-Za-z0-9_-]*\|SYNC-[A-Z0-9_-]*' "$out/replay/log.txt" 2>/dev/null | sort | uniq -c | sort -rn | head -n 10 || true
fi
