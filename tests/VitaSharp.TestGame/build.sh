#!/usr/bin/env bash
#
# Builds build/VitaSharpTest.vpk with VitaSDK (Linux and macOS; on Windows build.ps1 runs this inside WSL).
#
# Usage: ./build.sh [--install-sdk] [--clean]
#   --install-sdk  installs VitaSDK into ${VITASDK:-$HOME/vitasdk} first (official vdpm bootstrap)
#   --clean        removes build/ before building
#
# Requirements: bash, git, curl and make (VitaSDK brings its own arm-vita-eabi-gcc).

set -euo pipefail
ulimit -c 0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly SCRIPT_DIR
readonly VDPM_REPO="https://github.com/vitasdk/vdpm"

#######################################
# Prints an error and exits.
# Arguments:
#   Message.
#######################################
die() {
  echo "build.sh: $*" >&2
  exit 1
}

#######################################
# Installs VitaSDK with the official vdpm bootstrap into ${VITASDK}.
#######################################
install_sdk() {
  local vdpm_dir="${HOME}/vdpm"
  if [[ ! -d "${vdpm_dir}" ]]; then
    git clone --depth 1 "${VDPM_REPO}" "${vdpm_dir}"
  fi
  (cd "${vdpm_dir}" && ./bootstrap-vitasdk.sh)
}

#######################################
# Number of parallel make jobs (half the CPUs, at least 1).
#######################################
job_count() {
  local cpus
  case "$(uname -s)" in
    Darwin) cpus="$(sysctl -n hw.ncpu)" ;;
    *) cpus="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 2)" ;;
  esac
  local jobs=$((cpus / 2))
  if [[ "${jobs}" -lt 1 ]]; then
    jobs=1
  fi
  echo "${jobs}"
}

main() {
  local install=0
  local clean=0
  local argument
  for argument in "$@"; do
    case "${argument}" in
      --install-sdk) install=1 ;;
      --clean) clean=1 ;;
      -h | --help)
        sed -n '2,10p' "${BASH_SOURCE[0]}"
        exit 0
        ;;
      *) die "unknown option ${argument}" ;;
    esac
  done

  export VITASDK="${VITASDK:-${HOME}/vitasdk}"
  export PATH="${VITASDK}/bin:${PATH}"

  if [[ "${install}" -eq 1 ]]; then
    install_sdk
  fi
  if [[ ! -x "${VITASDK}/bin/arm-vita-eabi-gcc" ]]; then
    die "VitaSDK not found in ${VITASDK} (run ./build.sh --install-sdk or set VITASDK)"
  fi

  cd "${SCRIPT_DIR}"
  if [[ "${clean}" -eq 1 ]]; then
    make clean
  fi
  make -j "$(job_count)"
  echo "Built ${SCRIPT_DIR}/build/VitaSharpTest.vpk"
}

main "$@"
