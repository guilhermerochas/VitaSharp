#!/usr/bin/env bash
#
# System apps sweep: boots the real shell headless once per screen script in tests/nav/sysapps (each opens an app from
# the home screen, walks its menus, PS button, closes it, twice) and reports per script whether it finished (the
# script's own quit), crashed (a guest/process crash in the log) or hung (no quit before the timeout, or a watchdog
# STALL). Shots and logs go to <out>/<script>/. Needs a data root with the firmware installed (os0, vs0, sa0).
#   build/sysapps-sweep.sh --data=<root> [--exe=<VitaSharp.Desktop>] [--out=<dir>] [--only=music,settings]
#     [--timeout=900]   (same as build/sysapps-sweep.ps1)
# Expected (no network, PSN, USB or device): see docs/notes/system-apps.md, "Sweep checklist".

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly USAGE="usage: $0 --data=<root> [--exe=...] [--out=...] [--only=a,b] [--timeout=900]"

#######################################
# Runs one screen script and prints its result line.
# Arguments:
#   Script, data root, executable, results folder, timeout in seconds.
# Outputs:
#   The result line to stdout.
# Returns:
#   0 when the script finished cleanly (OK), 1 otherwise.
#######################################
run_script() {
  local script="$1" data="$2" exe="$3" dir="$4" timeout_sec="$5"
  local name log pid finished=0 i
  name="$(basename "${script}" .nav)"
  mkdir -p "${dir}"
  log="${dir}/log.txt"
  nice -n 10 "${exe}" "--data=${data}" boot-shell --headless --vulkan --no-inspect "--nav=${script}" "--nav-out=${dir}" \
    "--log=${log}" >"${dir}/stdout.txt" 2>"${dir}/stderr.txt" &
  pid=$!
  for ((i = 0; i < timeout_sec; i++)); do
    if ! kill -0 "${pid}" 2>/dev/null; then
      finished=1
      break
    fi
    sleep 1
  done
  if ((finished == 0)); then
    kill "${pid}" 2>/dev/null
  fi
  wait "${pid}" 2>/dev/null
  local crash=0 stall=0 status shots
  if [[ -f "${log}" ]]; then
    crash=$(grep -cE "crashed in thread|\) crashed:|could not start" "${log}")
    stall=$(grep -c "\[watchdog\] STALL" "${log}")
  fi
  if ((finished == 0)); then
    status=HANG
  elif ((crash > 0)); then
    status=CRASH
  elif ((stall > 0)); then
    status=STALL
  else
    status=OK
  fi
  shots=$(find "${dir}" -maxdepth 1 -name '*.png' | wc -l)
  printf "%-20s %-6s crashes %s stalls %s shots %s\n" "${name}" "${status}" "${crash}" "${stall}" "${shots}"
  [[ "${status}" == OK ]]
}

main() {
  local data="" exe="" out="" only="" timeout_sec=900 a
  for a in "$@"; do
    case "${a}" in
      --data=*) data="${a#*=}" ;;
      --exe=*) exe="${a#*=}" ;;
      --out=*) out="${a#*=}" ;;
      --only=*) only="${a#*=}" ;;
      --timeout=*) timeout_sec="${a#*=}" ;;
      -h | --help)
        sed -n '3,9s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
        return 0
        ;;
      *)
        echo "unknown option ${a}" >&2
        return 2
        ;;
    esac
  done
  if [[ -z "${data}" ]]; then
    echo "${USAGE}" >&2
    return 2
  fi
  exe="${exe:-${ROOT}/src/VitaSharp.Desktop/bin/Release/net10.0/VitaSharp.Desktop}"
  if [[ ! -x "${exe}" ]]; then
    echo "VitaSharp.Desktop not found: ${exe} (build it or pass --exe=)" >&2
    return 2
  fi
  out="${out:-${ROOT}/tests/output/sysapps-sweep}"
  mkdir -p "${out}"
  local summary="${out}/summary.txt"
  : >"${summary}"
  local fail=0 s name line
  for s in "${ROOT}"/tests/nav/sysapps/*.nav; do
    name="$(basename "${s}" .nav)"
    if [[ -n "${only}" && ",${only}," != *",${name},"* ]]; then
      continue
    fi
    if ! line="$(run_script "${s}" "${data}" "${exe}" "${out}/${name}" "${timeout_sec}")"; then
      fail=1
    fi
    echo "${line}"
    echo "${line}" >>"${summary}"
  done
  return "${fail}"
}

main "$@"
