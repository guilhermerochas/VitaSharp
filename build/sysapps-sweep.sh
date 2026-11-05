#!/usr/bin/env bash
# System apps sweep: boots the real shell headless once per screen script in tests/nav/sysapps (each opens an app from
# the home screen, walks its menus, PS button, closes it, twice) and reports per script whether it finished (the
# script's own quit), crashed (a guest/process crash in the log) or hung (no quit before the timeout, or a watchdog
# STALL). Shots and logs go to <out>/<script>/. Needs a data root with the firmware installed (os0, vs0, sa0).
#   build/sysapps-sweep.sh --data=<root> [--exe=<VitaSharp.Desktop>] [--out=<dir>] [--only=music,settings] [--timeout=900]
# Expected (no network, PSN, USB or device): see docs/notes/system-apps.md, "Sweep checklist".
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
data="" exe="" out="" only="" timeout_sec=900
for a in "$@"; do
  case "$a" in
    --data=*) data="${a#*=}" ;;
    --exe=*) exe="${a#*=}" ;;
    --out=*) out="${a#*=}" ;;
    --only=*) only="${a#*=}" ;;
    --timeout=*) timeout_sec="${a#*=}" ;;
    *) echo "unknown option $a" >&2; exit 2 ;;
  esac
done
[ -n "$data" ] || { echo "usage: $0 --data=<root> [--exe=...] [--out=...] [--only=a,b] [--timeout=900]" >&2; exit 2; }
[ -n "$exe" ] || exe="$root/src/VitaSharp.Desktop/bin/Release/net10.0/VitaSharp.Desktop"
[ -x "$exe" ] || { echo "VitaSharp.Desktop not found: $exe (build it or pass --exe=)" >&2; exit 2; }
[ -n "$out" ] || out="$root/tests/output/sysapps-sweep"
mkdir -p "$out"
summary="$out/summary.txt"
: > "$summary"
fail=0
for s in "$root"/tests/nav/sysapps/*.nav; do
  name="$(basename "$s" .nav)"
  if [ -n "$only" ] && [[ ",$only," != *",$name,"* ]]; then continue; fi
  dir="$out/$name"
  mkdir -p "$dir"
  log="$dir/log.txt"
  nice -n 10 "$exe" "--data=$data" boot-shell --headless --vulkan --no-inspect "--nav=$s" "--nav-out=$dir" "--log=$log" \
    > "$dir/stdout.txt" 2> "$dir/stderr.txt" &
  pid=$!
  finished=0
  for ((i = 0; i < timeout_sec; i++)); do
    if ! kill -0 "$pid" 2>/dev/null; then finished=1; break; fi
    sleep 1
  done
  if [ "$finished" -eq 0 ]; then kill "$pid" 2>/dev/null; fi
  wait "$pid" 2>/dev/null
  crash=0 stall=0
  if [ -f "$log" ]; then
    crash=$(grep -cE "crashed in thread|\) crashed:|could not start" "$log")
    stall=$(grep -c "\[watchdog\] STALL" "$log")
  fi
  if [ "$finished" -eq 0 ]; then status=HANG; elif [ "$crash" -gt 0 ]; then status=CRASH; elif [ "$stall" -gt 0 ]; then status=STALL; else status=OK; fi
  shots=$(find "$dir" -maxdepth 1 -name '*.png' | wc -l)
  line=$(printf "%-20s %-6s crashes %s stalls %s shots %s" "$name" "$status" "$crash" "$stall" "$shots")
  echo "$line"
  echo "$line" >> "$summary"
  [ "$status" = OK ] || fail=1
done
exit "$fail"
