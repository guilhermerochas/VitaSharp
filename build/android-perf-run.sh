#!/usr/bin/env bash
# Repeatable frame-rate measurement of one game scene on a USB-connected Android phone (developer APK): every run is a
# cold start (app closed, battery cooled to MAXTEMP), loads the scene's save state with a scripted loadstate step,
# waits WARMUP seconds in gameplay (excluded), then measures MEASURE seconds: display frame times from SurfaceFlinger
# (median / p90 frame ms, median and mean fps), the app's PERF lines over the same window (GPU ms per frame, CPU ms of
# the game thread, GPU worker and whole process), the big/little core clocks, battery/skin temperature and thermal
# status, and a screenshot at the start and end of the window (proof of gameplay). Variants are env.txt settings,
# run interleaved (A B B A ...), so runs stay comparable across APKs and days. Results: OUT/results.tsv (one row per
# run) and a per-variant median table on stdout. The screen watchdog (build/android-awake.sh) is expected to run.
# Usage: build/android-perf-run.sh SCENE [VARIANTS] [RUNS]   (same as build/android-perf-run.ps1)
#   SCENE: kz (Killzone Mercenary, staircase, slot 1), kzboot (the same staircase without a state: new campaign through
#     the menus by touch; BOOTWAIT s after the start, default 150), uc (Uncharted Golden Abyss gameplay, slot 1), f1 (F1 2011 race
#     start, slot 1), or TITLEID:SLOT for another game's save state.
#   VARIANTS: '|'-separated NAME[@APK][=ENV1=V1;ENV2=V2] ("base" = no extra settings; default "base"); a variant
#     with @APK installs that APK (adb install -r keeps the app data) before its runs, to compare builds; an entry
#     starting with @ is a phone shell command run before each start (@cmd game mode performance io.github.vitasharp;
#     the game mode is set back to standard after the run).
#   RUNS: runs per variant (default 3).
#   env: WARMUP (s, default 60), MEASURE (s, default 30), LOADAT (s after the first pad poll; default 30, f1 60),
#   MAXTEMP (battery deg C before a run, default 37; waits at most 5 min), APK (install it first), OUT (results folder,
#   default ./android-perf-run/<scene>_<time>), EXTRA_ENV (env.txt lines for every variant, ';'-separated).
#   STAT=1: simpleperf stat (cycles, instructions, L1I refills) of the guest main thread for 10 s of each window.
set -uo pipefail
export MSYS_NO_PATHCONV=1
scene="${1:?usage: android-perf-run.sh SCENE [VARIANTS] [RUNS]}"
variants="${2:-base}"
runs="${3:-3}"
warmup="${WARMUP:-60}"
measure="${MEASURE:-30}"

maxtemp="${MAXTEMP:-37}"
pkg=io.github.vitasharp
files="/sdcard/Android/data/$pkg/files"
here="$(cd "$(dirname "$0")" && pwd)"
case "$scene" in
  kz) title=PCSC00045; slot=1; at=30 ;;
  uc) title=PCSA00029; slot=1; at=30 ;;
  f1) title=PCSE00002; slot=1; at=60 ;;   # after the intro videos (an earlier load waits for the audio thread)
  # no save state (builds whose snapshot schema differs): new single-player campaign, normal difficulty, through the
  # Valor card and menus by touch, to the first gameplay (the staircase) about 2 min after the start
  kzboot) title=PCSC00045; slot=0; at=0; boot="tap@40:0.82:0.77,tap@52:0.25:0.37,tap@62:0.5:0.55" ;;
  *:*) title="${scene%%:*}"; slot="${scene##*:}"; at=30 ;;
  *) echo "unknown scene '$scene' (kz, kzboot, uc, f1 or TITLEID:SLOT)" >&2; exit 1 ;;
esac
loadat="${LOADAT:-$at}"
out="${OUT:-$PWD/android-perf-run/${scene%%:*}_$(date +%Y%m%d_%H%M%S)}"
mkdir -p "$out"
lp() { if command -v cygpath >/dev/null 2>&1; then cygpath -w "$1"; else printf "%s" "$1"; fi; }
sh_() { adb shell "$@" 2>/dev/null | tr -d '\r'; }

adb get-state >/dev/null || { echo "no phone" >&2; exit 1; }
if [ -n "${APK:-}" ]; then
  echo "installing $APK"
  adb install -r "$(lp "$APK")" | tail -n 1
fi
[ "$slot" = 0 ] || sh_ "[ -f $files/savestates/$title/slot$slot.vss ] && echo ok" | grep -q ok || { echo "no save state $title slot $slot on the phone" >&2; exit 1; }
activity="$(sh_ cmd package resolve-activity --brief $pkg | tail -n 1)"
apkinfo="$(sh_ dumpsys package $pkg | grep -m1 lastUpdateTime | sed 's/^ *//')"

IFS='|' read -r -a vs <<< "$variants"
names=(); envs=(); apks=()
for v in "${vs[@]}"; do
  head="$v"; e=""
  if [[ "$v" == *=* ]]; then head="${v%%=*}"; e="${v#*=}"; fi
  if [[ "$head" == *@* ]]; then names+=("${head%%@*}"); apks+=("${head#*@}"); else names+=("$head"); apks+=(""); fi
  envs+=("$e")
done
installed=""

cool() {
  local t i
  for ((i = 0; i < 30; i++)); do
    t=$(sh_ dumpsys battery | awk '/temperature/{print $2/10; exit}')
    awk -v t="$t" -v m="$maxtemp" 'BEGIN{exit !(t <= m)}' && { echo "$t"; return; }
    sleep 10
  done
  echo "$t"
}
thermal() { # "battery skin status"
  local b s st
  b=$(sh_ dumpsys battery | awk '/temperature/{print $2/10; exit}')
  st=$(sh_ dumpsys thermalservice | awk -F': ' '/^Thermal Status/{print $2; exit}')
  s=$(sh_ dumpsys thermalservice | grep -m1 'mName=SKIN' | sed -E 's/.*mValue=([0-9.]+).*/\1/')
  echo "$b $s $st"
}
layer() { # the game's SurfaceView layer, for --latency
  # (newer SurfaceFlinger lists RequestedLayerState{<id> <name> parentId=...}: --latency wants "<id> <name>")
  sh_ dumpsys SurfaceFlinger --list | grep -F "SurfaceView[$pkg/" | grep -F "(BLAST)" | grep -v -i background | tail -n 1 \
    | sed -E 's/^RequestedLayerState\{//; s/ parentId=.*//; s/\}$//'
}

printf "variant\trun\tvalid\tsf_frames\tmedian_ms\tp90_ms\tmedian_fps\tmean_fps\tgpu_ms\tgame_ms\tworker_ms\tproc_cpu_ms\tbig_mhz\tlittle_mhz\tgpu_mhz\ttemp_start\ttemp_end\tskin_end\tthermal\tnote\n" > "$out/results.tsv"
echo "scene $scene ($title slot $slot), ${#vs[@]} variant(s) x $runs, warm-up $warmup s, measure $measure s; $apkinfo" | tee "$out/info.txt"

order=()
for ((r = 0; r < runs; r++)); do
  if ((r % 2 == 0)); then for ((i = 0; i < ${#vs[@]}; i++)); do order+=("$i:$r"); done
  else for ((i = ${#vs[@]} - 1; i >= 0; i--)); do order+=("$i:$r"); done; fi
done

for item in "${order[@]}"; do
  vi=${item%%:*}; r=${item##*:}; name=${names[$vi]}
  rd="$out/${name}_$r"; mkdir -p "$rd"
  if [ -n "${apks[$vi]}" ] && [ "$installed" != "${apks[$vi]}" ]; then
    adb install -r "$(lp "${apks[$vi]}")" | tail -n 1; installed="${apks[$vi]}"
  fi
  adb shell am force-stop $pkg
  "$here/android-awake.sh" reopen >/dev/null
  t0=$(cool)
  {
    if [ "$slot" = 0 ]; then echo "VITASHARP_ANDROID_INPUT=$boot"; else echo "VITASHARP_ANDROID_INPUT=loadstate@$loadat:$slot"; fi
    echo "VITASHARP_SESSION_REPORT_SECONDS=10"
    if [ -n "${EXTRA_ENV:-}" ]; then echo "$EXTRA_ENV" | tr ';' '\n'; fi
    if [ -n "${envs[$vi]}" ]; then echo "${envs[$vi]}" | tr ';' '\n'; fi
  } > "$rd/all.txt"
  # entries starting with @ are phone shell commands run before the start (e.g. @cmd game mode performance <pkg>);
  # the game mode goes back to standard after the run
  grep -v '^@' "$rd/all.txt" > "$rd/env.txt"
  shellcmds=$(grep '^@' "$rd/all.txt" | sed 's/^@//')
  while IFS= read -r c; do [ -n "$c" ] && adb shell "$c" < /dev/null; done <<< "$shellcmds"
  adb push "$(lp "$rd/env.txt")" "$files/config/env.txt" >/dev/null
  adb shell am start -n "$activity" --es titleId "$title" >/dev/null
  start=$(date +%s); loaded=0; why=""
  if [ "$slot" = 0 ]; then   # no state: the scripted steps reach gameplay after BOOTWAIT s (default 150)
    while (( $(date +%s) - start < ${BOOTWAIT:-150} )); do sleep 5; [ -z "$(sh_ pidof $pkg:game)" ] && { why="game process gone"; break; }; done
    [ -z "$why" ] && loaded=1
  fi
  while [ "$slot" != 0 ] && (( $(date +%s) - start < loadat + 150 )); do
    sleep 5
    # the scripted step logs its result; only "Save state: <path> loaded in" means the state is in
    if [ "$(sh_ "grep -c 'Scripted load state, slot $slot: ' $files/logs/last.log")" != 0 ]; then
      if [ "$(sh_ "grep -c 'Save state: .*slot$slot.vss loaded in' $files/logs/last.log")" != 0 ]; then loaded=1
      else why=$(sh_ "grep -m1 'Scripted load state, slot $slot: ' $files/logs/last.log" | sed 's/.*Scripted load state, slot [0-9]*: //'); fi
      break
    fi
    [ -z "$(sh_ pidof $pkg:game)" ] && { why="game process gone"; break; }
  done
  note=""
  if [ "$loaded" != 1 ]; then note="state not loaded${why:+: $why}"; fi
  if [ -n "$note" ]; then
    # 20 columns: variant run valid, 12 empty (sf_frames .. gpu_mhz), temp_start, 3 empty, note
    # 21 columns: variant run valid, 12 empty (sf_frames .. gpu_mhz), temp_start, 4 empty, note
    printf "%s	%s	0													%s					%s
" "$name" "$r" "$t0" "$note" >> "$out/results.tsv"
    adb pull "$files/logs/last.log" "$(lp "$rd/last.log")" >/dev/null 2>&1
    echo "$name run $r: $note"; continue
  fi
  sleep "$warmup"
  adb exec-out screencap -p > "$rd/start.png"
  lay="$(layer)"
  : > "$rd/sf.txt"; : > "$rd/clocks.txt"
  adb shell dumpsys SurfaceFlinger --latency-clear >/dev/null 2>&1
  # clocks over 10 s of the window from the kernel trace (time-weighted: GPU mfg_pll_ck, big cores 6-7)
  adb shell atrace -t 10 freq power 2>/dev/null | tr -d '\r' > "$rd/atrace.txt" &
  atpid=$!
  # STAT=1: hardware counters of the guest main thread for 10 s of the window (simpleperf stat, profileable APK)
  if [ "${STAT:-0}" = 1 ]; then
    mt=$(sh_ "grep -o \"[0-9]* '[^']*'\" $files/logs/last.log" | grep -F "'$(sh_ "grep -m1 -o \"pinned guest main '[^']*'\" $files/logs/last.log" | cut -d"'" -f2)'" | tail -n 1 | cut -d' ' -f1)
    [ -n "$mt" ] && adb shell simpleperf stat --app $pkg -e cpu-cycles,instructions,raw-l1i-cache-refill,raw-l1i-tlb-refill -t "$mt" --duration 10 > "$rd/stat.txt" 2>&1 &
  fi
  mstart=$(date +%s); ws=$(sh_ date +%H:%M:%S)
  while (( $(date +%s) - mstart < measure )); do
    sh_ dumpsys SurfaceFlinger --latency "\"$lay\"" | tail -n +2 >> "$rd/sf.txt"
    sh_ 'cat /sys/devices/system/cpu/cpu7/cpufreq/scaling_cur_freq /sys/devices/system/cpu/cpu0/cpufreq/scaling_cur_freq' | paste -sd' ' >> "$rd/clocks.txt"
    sleep 2
  done
  sh_ dumpsys SurfaceFlinger --latency "\"$lay\"" | tail -n +2 >> "$rd/sf.txt"
  mend=$(date +%s); we=$(sh_ date +%H:%M:%S)
  wait 2>/dev/null   # atrace (and simpleperf stat)
  adb exec-out screencap -p > "$rd/end.png"
  th=$(thermal)
  alive=1; [ -z "$(sh_ pidof $pkg:game)" ] && alive=0
  adb pull "$files/logs/last.log" "$(lp "$rd/last.log")" >/dev/null 2>&1
  adb shell am force-stop $pkg
  "$here/android-awake.sh" reopen >/dev/null
  [ -n "$shellcmds" ] && adb shell cmd game mode standard $pkg >/dev/null 2>&1
  [ "$alive" = 1 ] || note="game process gone"

  # display: unique actual-present times (column 2, ns) -> intervals within the window
  sf=$(awk 'NF==3 && $2>0 && $2<9e18 {print $2}' "$rd/sf.txt" | sort -n -u | awk '
    NR>1 {d=($1-p)/1e6; if (d>0 && d<1000) print d} {p=$1}' | sort -n | awk '
    {a[NR]=$1; s+=$1} END {if (NR<10) {print "0 0 0 0 0"; exit}
      m=a[int((NR+1)/2)]; q=a[int(NR*0.9+0.5)]; printf "%d %.1f %.1f %.1f %.1f", NR, m, q, 1000/m, 1000*NR/s}')
  read -r sfn med p90 mfps meanfps <<< "$sf"
  # cadence: display intervals in vblanks (16.67 ms), e.g. "1v:112 2v:70 3v:96 ..." (also in RUN/hist.txt)
  hist=$(awk 'NF==3 && $2>0 && $2<9e18 {print $2}' "$rd/sf.txt" | sort -n -u | awk 'NR>1 { d = ($1 - p) / 1e6; if (d > 0 && d < 1000) h[int(d / 16.667 + 0.5)]++ } { p = $1 }
    END { for (k = 0; k <= 60; k++) if (h[k]) printf "%dv:%d ", k, h[k] }')
  echo "$hist" > "$rd/hist.txt"
  # PERF summaries inside the window: deltas between the first and the last summary written after the warm-up
  mainname=$(grep -m1 -oE "pinned guest main '[^']+'" "$rd/last.log" | cut -d"'" -f2)
  # placement off: the busiest guest thread (the first PERF cycles line)
  [ -n "$mainname" ] || mainname=$(grep -m1 -oE 'PERF cycles [^:]+:' "$rd/last.log" | sed -E 's/PERF cycles (.*):/\1/')
  perf=$(awk -v ws="$ws" -v we="$we" -v main="${mainname:-?}" '
    function num(s) { gsub(",", ".", s); return s + 0 }
    /PERF periodic summary: frame/ { t=substr($1,2,8); inw=(t>=ws && t<=we); if (inw) { n++; f[n]=num($NF) } }
    inw && /PERF gpu: / { match($0, /\(([0-9]+) submits, ([0-9,]+) ms each/, m); g[n]=num(m[1])*num(m[2]) }
    inw && /PERF cpu [0-9]/ { match($0, /PERF cpu ([0-9,]+) s/, m); c[n]=num(m[1]) }
    inw && /PERF thread GPU worker: / { match($0, /: ([0-9,]+) s cpu/, m); w[n]=num(m[1]) }
    inw && index($0, "PERF thread " main ": ") { match($0, /: ([0-9,]+) s cpu/, m); gt[n]=num(m[1]) }
    END { if (n < 2 || f[n] <= f[1]) { print "- - - -"; exit }
      d=f[n]-f[1]; printf "%.1f %.1f %.1f %.1f", (g[n]-g[1])/d, (gt[n]-gt[1])*1000/d, (w[n]-w[1])*1000/d, (c[n]-c[1])*1000/d }' "$rd/last.log")
  read -r gpu game worker proc <<< "$perf"
  # the app's own presented-frame rate over the window ([perf] lines every 30 s): the display median can overstate
  # bursty output (queued frames shown back to back after a stall); this and the mean are the throughput
  appfps=$(awk -v ws="$ws" -v we="$we" '/\[perf\] [0-9,]+ fps over/ { t = substr($1, 2, 8); if (t >= ws && t <= we || t > we && !late) { v = $0; sub(/.*\[perf\] /, "", v); sub(/ fps over.*/, "", v); gsub(",", ".", v); s += v; n++; if (t > we) late = 1 } } END { if (n) printf "%.1f", s / n; else print "-" }' "$rd/last.log")
  clk=$(awk '{b[NR]=$1/1000; l[NR]=$2/1000} END {n=asort(b); asort(l); if (n) printf "%d %d", b[int((n+1)/2)], l[int((n+1)/2)]; else print "- -"}' "$rd/clocks.txt")
  read -r big little <<< "$clk"
  # time-weighted clocks from the trace: the GPU PLL (MediaTek mfg_pll_ck) and the two fastest cores (6, 7)
  freq=$(awk '
    { if (!match($0, / ([0-9]+\.[0-9]+): /, m)) next; t = m[1] + 0; if (!t0) t0 = t; t1 = t }
    match($0, /clk_set_rate: mfg_pll_ck ([0-9]+)/, g) { if (gl) { gs += gf * (t - gl); gw += t - gl }; gf = g[1] / 1e6; gl = t }
    match($0, /cpu_frequency: state=([0-9]+) cpu_id=([67])$/, c) { id = c[2]; if (cl[id]) { cs += cf[id] * (t - cl[id]); cw += t - cl[id] }; cf[id] = c[1] / 1000; cl[id] = t }
    END { if (gl) { gs += gf * (t1 - gl); gw += t1 - gl }; for (id in cl) { cs += cf[id] * (t1 - cl[id]); cw += t1 - cl[id] }
      printf "%s %s", (gw > 1 ? sprintf("%.0f", gs / gw) : "-"), (cw > 1 ? sprintf("%.0f", cs / cw) : "-") }' "$rd/atrace.txt" 2>/dev/null)
  read -r gpumhz bigavg <<< "${freq:-- -}"
  [ "${bigavg:--}" != - ] && big=$bigavg
  read -r tb ts tst <<< "$th"
  valid=1; [ -n "$note" ] && valid=0; [ "$sfn" = 0 ] && { valid=0; note="${note:+$note; }no display frames (layer '$lay')"; }
  printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n" "$name" "$r" "$valid" "$sfn" "$med" "$p90" \
    "$mfps" "$meanfps" "$gpu" "$game" "$worker" "$proc" "$big" "$little" "$gpumhz" "$t0" "$tb" "$ts" "$tst" "$appfps" "$note" >> "$out/results.tsv"
  echo "$name run $r: median $med ms (p90 $p90), $mfps fps median, $meanfps mean, app $appfps fps; GPU $gpu ms at $gpumhz MHz, game $game, worker $worker; big cores $big MHz; $t0 -> $tb C; cadence $hist${note:+ ($note)}"
done
adb shell rm -f "$files/config/env.txt" "$files/config/input_live.txt"

echo
echo "== medians per variant (valid runs): $out/results.tsv"
printf "%-12s %4s %9s %7s %9s %8s %7s %7s %7s %7s %7s\n" variant runs median_ms p90_ms median_fps mean_fps gpu_ms game_ms worker big_mhz gpu_mhz
for name in "${names[@]}"; do
  awk -F'\t' -v v="$name" '
    function med(col,   k, n, x) { n=0; for (k=1; k<=cnt; k++) if (val[k,col] != "-" && val[k,col] != "") x[++n]=val[k,col]+0; if (!n) return "-"; asort(x); return x[int((n+1)/2)] }
    NR>1 && $1==v && $3==1 { cnt++; for (c=4; c<=20; c++) val[cnt,c]=$c }
    END { printf "%-12s %4d %9s %7s %9s %8s %7s %7s %7s %7s %7s\n", v, cnt, med(5), med(6), med(7), med(8), med(9), med(10), med(11), med(13), med(15) }' "$out/results.tsv"
done | tee "$out/summary.txt"
