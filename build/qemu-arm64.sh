#!/usr/bin/env bash
#
# Runs the ARM64 code of VitaSharp (the native ARM64 JIT backend, the .NET ARM64 runtime) on an x64 Linux PC under
# qemu-user, without a phone. Usage: build/qemu-arm64.sh [options]   (same as build/qemu-arm64.ps1, which runs it in WSL)
#   --install           installs what is missing first: qemu-user-static, binfmt-support and the arm64 libraries
#                       (apt, needs sudo) and the arm64 .NET runtime in ~/.dotnet-arm64 (dotnet-install.sh)
#   --tests-dir DIR     test binaries already built for linux-arm64 (skips the build; build/qemu-arm64.ps1 builds them
#                       on Windows): dotnet build tests/VitaSharp.Tests -c Release -r linux-arm64 --self-contained false -o DIR
#   --filter F          dotnet test filter of the tests (default: the ARM64 backend, see DEFAULT_FILTER)
#   --no-tests          runs no tests
#   --testgame VPK      also boots the test game headless (VitaSharp.Desktop run) under qemu; results in --out
#   --desktop-dir DIR   VitaSharp.Desktop already built for linux-arm64 (skips its build)
#   --seconds N         how long the test game runs (default 300: under qemu the emulator is ~10x slower)
#   --out DIR           where logs, the test results and the test game's data folder go (default: a temp folder)
#   env: DOTNET (x64 dotnet with an SDK; default dotnet in PATH or ~/.dotnet/dotnet), DOTNET_ARM64_ROOT (arm64
#   runtime; default ~/.dotnet-arm64), QEMU_CPU (qemu -cpu model for the arm64 processes; default qemu's max).
# Needs Linux on x64 (Debian/Ubuntu; Windows: WSL 2). macOS has no qemu-user: on Apple silicon run the tests natively.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
# The ARM64 backend's tests: on an ARM64 process they run the generated code natively (Arm64HostRunner), not in the
# Unicorn sandbox they use on x64.
readonly DEFAULT_FILTER='FullyQualifiedName~NativeJit|FullyQualifiedName~Vfp|FullyQualifiedName~Neon|FullyQualifiedName~Arm64'
readonly APT_PACKAGES='qemu-user-static binfmt-support libc6:arm64 libstdc++6:arm64 libgcc-s1:arm64 libssl3:arm64 zlib1g:arm64'

die() {
  echo "qemu-arm64: $*" >&2
  exit 1
}

#######################################
# No process of this run writes a core dump (WSL's crash collector ignores the core size limit; see prepush-check).
#######################################
no_core_dumps() {
  ulimit -c 0
  if [[ -w /proc/self/coredump_filter ]]; then
    echo 0 >/proc/self/coredump_filter
  fi
  export DOTNET_DbgEnableMiniDump=0
}

#######################################
# Prints the x64 dotnet to use.
#######################################
host_dotnet() {
  if [[ -n "${DOTNET:-}" ]]; then
    echo "${DOTNET}"
  elif command -v dotnet >/dev/null 2>&1; then
    command -v dotnet
  elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
    echo "${HOME}/.dotnet/dotnet"
  else
    die "no x64 .NET SDK (dotnet-install.sh --channel 10.0 --install-dir ~/.dotnet, or set DOTNET)"
  fi
}

#######################################
# Installs qemu-user, the binfmt entry, the arm64 libraries and the arm64 .NET runtime.
# Arguments:
#   The arm64 runtime folder.
#######################################
install_requirements() {
  if ! dpkg --print-foreign-architectures | grep -qx arm64; then
    sudo dpkg --add-architecture arm64
  fi
  sudo apt-get update -qq
  # shellcheck disable=SC2086 # the package list is split on purpose
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends ${APT_PACKAGES} \
    "$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1; exit}'):arm64"
  if [[ ! -x "$1/dotnet" ]]; then
    local installer
    installer="$(mktemp)"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "${installer}"
    bash "${installer}" --channel 10.0 --runtime dotnet --architecture arm64 --install-dir "$1"
    rm -f "${installer}"
  fi
}

#######################################
# Fails unless arm64 ELF files start through qemu (the binfmt_misc entry) and the arm64 runtime is there.
# Arguments:
#   The arm64 runtime folder.
#######################################
check_requirements() {
  [[ "$(uname -s)" == "Linux" ]] || die "needs Linux (macOS has no qemu-user; Apple silicon runs the tests natively)"
  [[ "$(uname -m)" == "x86_64" ]] || die "the host is $(uname -m): run the tests natively there"
  if [[ ! -e /proc/sys/fs/binfmt_misc/qemu-aarch64 ]]; then
    # WSL without systemd does not register the entries at boot
    if command -v update-binfmts >/dev/null 2>&1; then
      sudo update-binfmts --enable qemu-aarch64 || true
    fi
    [[ -e /proc/sys/fs/binfmt_misc/qemu-aarch64 ]] || die "no qemu-aarch64 binfmt entry (run with --install)"
  fi
  [[ -x "$1/dotnet" ]] || die "no arm64 .NET runtime in $1 (run with --install)"
  "$1/dotnet" --list-runtimes >/dev/null || die "the arm64 runtime in $1 does not start under qemu"
}

#######################################
# Runs the tests of a linux-arm64 build with the x64 test platform and an arm64 test host (under qemu).
# Arguments:
#   Test binaries folder, filter, output folder, arm64 runtime folder.
#######################################
run_tests() {
  local dotnet start code
  dotnet="$(host_dotnet)"
  start="$(date +%s)"
  set +e
  DOTNET_ROOT_ARM64="$4" "${dotnet}" vstest "$1/VitaSharp.Tests.dll" --Platform:arm64 \
    --TestCaseFilter:"$2" --ResultsDirectory:"$3/tests" --logger:'trx;LogFileName=arm64.trx' \
    --logger:'console;verbosity=normal' -- RunConfiguration.MaxCpuCount=1 2>&1 | tee "$3/tests.log"
  code="${PIPESTATUS[0]}"
  set -e
  echo "qemu-arm64: tests exit ${code} in $(($(date +%s) - start)) s (log $3/tests.log)"
  return "${code}"
}

#######################################
# Boots the test game headless with an arm64 VitaSharp.Desktop under qemu.
# Arguments:
#   VitaSharp.Desktop folder, VPK, seconds, output folder.
#######################################
run_testgame() {
  local data="$4/testgame-data" start code
  mkdir -p "${data}/fs/ux0/data/VSTEST"
  : >"${data}/fs/ux0/data/VSTEST/exit_after_run"
  start="$(date +%s)"
  set +e
  "$1/VitaSharp.Desktop" run "$2" --reinstall --data="${data}" --seconds="$3" --dialogs=auto \
    --ime-text=VitaSharp --log="$4/testgame.log" >"$4/testgame.out" 2>&1
  code=$?
  set -e
  echo "qemu-arm64: test game exit ${code} in $(($(date +%s) - start)) s (log $4/testgame.log)"
  if [[ -f "${data}/fs/ux0/data/VSTEST/results.txt" ]]; then
    cp "${data}/fs/ux0/data/VSTEST/results.txt" "$4/testgame-results.txt"
    grep '^# done' "$4/testgame-results.txt" || echo "qemu-arm64: the test game run did not finish"
  else
    echo "qemu-arm64: no results.txt (see $4/testgame.log)"
  fi
  return "${code}"
}

main() {
  local install=0 tests_dir="" filter="${DEFAULT_FILTER}" tests=1 vpk="" desktop_dir="" seconds=300 out=""
  local runtime="${DOTNET_ARM64_ROOT:-${HOME}/.dotnet-arm64}"
  while (($# > 0)); do
    case "$1" in
      -h | --help)
        sed -n '3,19s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
        return 0
        ;;
      --install) install=1 ;;
      --tests-dir) tests_dir="$2"; shift ;;
      --filter) filter="$2"; shift ;;
      --no-tests) tests=0 ;;
      --testgame) vpk="$2"; shift ;;
      --desktop-dir) desktop_dir="$2"; shift ;;
      --seconds) seconds="$2"; shift ;;
      --out) out="$2"; shift ;;
      *) die "unknown option $1 (see --help)" ;;
    esac
    shift
  done
  no_core_dumps
  if ((install == 1)); then
    install_requirements "${runtime}"
  fi
  check_requirements "${runtime}"
  if [[ -n "${QEMU_CPU:-}" ]]; then
    export QEMU_CPU
  fi
  [[ -n "${out}" ]] || out="$(mktemp -d)"
  mkdir -p "${out}"
  local dotnet code=0
  dotnet="$(host_dotnet)"
  if ((tests == 1)); then
    if [[ -z "${tests_dir}" ]]; then
      tests_dir="${out}/tests-bin"
      "${dotnet}" build "${ROOT}/tests/VitaSharp.Tests" -c Release -r linux-arm64 --self-contained false \
        -o "${tests_dir}" -m:2 -p:UseSharedCompilation=false -nologo -v:q
    fi
    run_tests "${tests_dir}" "${filter}" "${out}" "${runtime}" || code=$?
  fi
  if [[ -n "${vpk}" ]]; then
    if [[ -z "${desktop_dir}" ]]; then
      desktop_dir="${out}/desktop-bin"
      "${dotnet}" publish "${ROOT}/src/VitaSharp.Desktop" -c Release -r linux-arm64 --self-contained true \
        -o "${desktop_dir}" -m:2 -p:UseSharedCompilation=false -nologo -v:q
    fi
    VITASHARP_QEMU_USER=1 run_testgame "${desktop_dir}" "${vpk}" "${seconds}" "${out}" || code=$?
  fi
  echo "qemu-arm64: output in ${out}"
  return "${code}"
}

main "$@"
