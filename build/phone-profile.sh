#!/usr/bin/env bash
# Runs the desktop emulator as the Android build runs (docs/notes/cpu-phases.md, "Reproducing the phone's timing on
# the PC"): the Native AOT build (no JIT), --phone-profile (waits without spinning, Mali GPU profile, 333 MHz, two host
# cores, 4 MB GC gen0 budget), a case-sensitive data folder and the CPU phase profile on, then summarises the log.
# Usage: build/phone-profile.sh [--help] [--out folder] [--publish] [--data folder] [--log file]
#                               [--affinity mask] [--no-run] [-- <VitaSharp.Desktop arguments>]
#   (same as build/phone-profile.ps1 -Help -Out -Publish -Data -Log -Affinity -NoRun, arguments after them)
#   --out: where the Native AOT build is published once (default ${TMPDIR:-/tmp}/vitasharp-phone-profile);
#     --publish publishes it again (after a code change).
#   --data: the data folder of the run; a new one is made case-sensitive (Windows: fsutil setCaseSensitiveInfo,
#     needs an empty folder; Linux: always; macOS: only on a case-sensitive volume, a warning otherwise).
#   --log: the run's log (default <data>/logs/phone-profile.log); --affinity: VITASHARP_PHONE_AFFINITY (host cores
#     mask, default two cores of the upper half); --no-run: publish and prepare only.
#   Example: build/phone-profile.sh --data /tmp/pp -- run tests/VitaSharp.TestGame/build/VitaSharpTest.vpk
#     --reinstall --vulkan --seconds=150 --dialogs=auto
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

#######################################
# Prints the usage (the header comment of this script).
# Outputs:
#   The usage text to stdout.
#######################################
usage() {
  sed -n '2,/^set -euo pipefail$/{/^#/s/^# \{0,1\}//p;}' "${BASH_SOURCE[0]}"
}

#######################################
# Prints an error and exits.
# Arguments:
#   Message.
#######################################
die() {
  echo "phone-profile.sh: $*" >&2
  exit 1
}

#######################################
# The .NET runtime identifier of this machine.
# Outputs:
#   The RID to stdout.
#######################################
host_rid() {
  local arch
  arch="$(uname -m)"
  case "${arch}" in
    x86_64 | amd64) arch="x64" ;;
    arm64 | aarch64) arch="arm64" ;;
    *) die "unsupported CPU ${arch}" ;;
  esac
  case "$(uname -s)" in
    Darwin) echo "osx-${arch}" ;;
    Linux) echo "linux-${arch}" ;;
    MINGW* | MSYS* | CYGWIN*) echo "win-${arch}" ;;
    *) die "unsupported system $(uname -s)" ;;
  esac
}

#######################################
# Makes a new, empty data folder case-sensitive where the system allows it.
# Arguments:
#   Folder.
#######################################
make_case_sensitive() {
  local folder="$1"
  if [[ -d "${folder}" ]] && [[ -n "$(ls -A "${folder}")" ]]; then
    echo "phone-profile.sh: ${folder} exists: its case sensitivity is kept (the run log says which it is)"
    return 0
  fi
  mkdir -p "${folder}"
  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*)
      fsutil.exe file setCaseSensitiveInfo "$(cygpath -w "${folder}")" enable >/dev/null \
        || echo "phone-profile.sh: fsutil could not make ${folder} case-sensitive" >&2
      ;;
    Darwin)
      echo "phone-profile.sh: macOS volumes are usually case-insensitive; use a case-sensitive APFS volume for --data" >&2
      ;;
    *) ;;
  esac
}

#######################################
# Prints the phone-profile lines of a run log: the profile, fps, PERF phases and phase alerts.
# Arguments:
#   Log file.
#######################################
summarise() {
  local log="$1"
  [[ -f "${log}" ]] || {
    echo "phone-profile.sh: no log at ${log}" >&2
    return 0
  }
  echo "== ${log}"
  grep -m 1 "Phone profile:" "${log}" || echo "(no 'Phone profile:' line)"
  grep -E "fps over|FPS frame" "${log}" | tail -n 5 || true
  grep "PERF phases" "${log}" | tail -n 6 | cut -c 1-400 || true
  echo "phase alerts: $(grep -c "\[phase-alert\]" "${log}" || true)"
  grep "\[phase-alert\]" "${log}" | tail -n 3 | cut -c 1-300 || true
}

main() {
  local out="${TMPDIR:-/tmp}/vitasharp-phone-profile" publish=0 data="" log="" affinity="" run=1
  while (($# > 0)); do
    case "$1" in
      -h | --help)
        usage
        return 0
        ;;
      --out) out="${2:?--out needs a folder}"; shift ;;
      --publish) publish=1 ;;
      --data) data="${2:?--data needs a folder}"; shift ;;
      --log) log="${2:?--log needs a file}"; shift ;;
      --affinity) affinity="${2:?--affinity needs a mask}"; shift ;;
      --no-run) run=0 ;;
      --) shift; break ;;
      *) die "unknown option $1 (see --help)" ;;
    esac
    shift
  done

  local rid exe
  rid="$(host_rid)"
  exe="${out}/VitaSharp.Desktop"
  if [[ "${rid}" == win-* ]]; then
    exe="${exe}.exe"
  fi
  if ((publish == 1)) || [[ ! -x "${exe}" ]]; then
    echo "phone-profile.sh: publishing the Native AOT build (${rid}) to ${out}"
    mkdir -p "${out}"
    # the Native AOT linker step finds the MSVC tools with vswhere, which the Visual Studio installer does not put on PATH
    local installer="/c/Program Files (x86)/Microsoft Visual Studio/Installer"
    if [[ "${rid}" == win-* ]] && ! command -v vswhere.exe >/dev/null && [[ -x "${installer}/vswhere.exe" ]]; then
      PATH="${installer}:${PATH}"
    fi
    dotnet publish "${ROOT}/src/VitaSharp.Desktop" -c Release -r "${rid}" -p:VitaSharpRelease=true \
      -p:VitaSharpAot=true -p:UseSharedCompilation=false -m:2 -o "${out}" >"${out}.publish.log" 2>&1 \
      || die "the publish failed: ${out}.publish.log"
  fi

  local args=()
  if [[ -n "${data}" ]]; then
    make_case_sensitive "${data}"
    args+=("--data=${data}")
    if [[ -z "${log}" ]]; then
      log="${data}/logs/phone-profile.log"
    fi
  fi
  if [[ -n "${log}" ]]; then
    mkdir -p "$(dirname "${log}")"
    args+=("--log=${log}")
  fi
  ((run == 1)) || return 0
  (($# > 0)) || die "no VitaSharp.Desktop arguments after -- (see --help)"

  local rc=0
  VITASHARP_PHONE_PROFILE=1 DOTNET_GCgen0size="${DOTNET_GCgen0size:-0x400000}" \
    VITASHARP_PHASE_PROF="${VITASHARP_PHASE_PROF:-1}" VITASHARP_PHONE_AFFINITY="${affinity:-${VITASHARP_PHONE_AFFINITY:-}}" \
    "${exe}" "$@" ${args[@]+"${args[@]}"} || rc=$?
  if [[ -n "${log}" ]]; then
    summarise "${log}"
  fi
  return "${rc}"
}

main "$@"
