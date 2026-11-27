#!/usr/bin/env bash
#
# Runs the ARM64 code of VitaSharp (the native ARM64 JIT backend, the .NET ARM64 runtime) on an x64 Linux PC under
# qemu-user, without a phone. Usage: build/qemu-arm64.sh [options]   (same as build/qemu-arm64.ps1, which runs it in WSL)
#   --install           installs what is missing first: qemu-user-static, binfmt-support and the arm64 libraries
#                       (apt, needs sudo), qemu-user 10 in ~/.qemu-arm64 (--install-qemu), the arm64 .NET runtime in
#                       ~/.dotnet-arm64 (dotnet-install.sh) and the portable test platform in ~/.vstest-portable (NuGet)
#   --install-qemu      only installs qemu-user 10 (Debian trixie's static qemu-aarch64) in ~/.qemu-arm64; no sudo
#   --register-binfmt   only registers ~/.qemu-arm64's qemu-aarch64 as the arm64 binfmt interpreter (as root)
#   --tests-dir DIR     test binaries already built for linux-arm64 (skips the build; build/qemu-arm64.ps1 builds them
#                       on Windows): dotnet build tests/VitaSharp.Tests -c Release -r linux-arm64 --self-contained false -o DIR
#   --filter F          dotnet test filter of the tests (default: the ARM64 backend, see DEFAULT_FILTER)
#   --no-tests          runs no tests
#   --testgame VPK      also boots the test game headless (VitaSharp.Desktop run) under qemu; results in --out
#   --desktop-dir DIR   VitaSharp.Desktop already built for linux-arm64 (skips its build)
#   --seconds N         how long the test game runs (default 300: under qemu the emulator is ~10x slower)
#   --out DIR           where logs, the test results and the test game's data folder go (default: a temp folder)
#   env: DOTNET (x64 dotnet with an SDK; default dotnet in PATH or ~/.dotnet/dotnet), DOTNET_ARM64_ROOT (arm64
#   runtime; default ~/.dotnet-arm64), VSTEST_PORTABLE (portable test platform; default ~/.vstest-portable), QEMU_CPU
#   (qemu -cpu model for the arm64 processes; default qemu's max), QEMU_ARM64_DIR (qemu-user 10; default ~/.qemu-arm64).
#   qemu-user 7.2 (Debian bookworm's) runs stale code after the JIT rewrites it through its W^X write mapping: the
#   ARM64 host tests then skip themselves (VITASHARP_QEMU_USER, HostCodeCoherence); qemu 10 runs them.
# Needs Linux on x64 (Debian/Ubuntu; Windows: WSL 2). macOS has no qemu-user: on Apple silicon run the tests natively.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
# The ARM64 backend's tests: on an ARM64 process they run the generated code natively (Arm64HostRunner), not in the
# Unicorn sandbox they use on x64.
readonly DEFAULT_FILTER='FullyQualifiedName~NativeJit|FullyQualifiedName~Vfp|FullyQualifiedName~Neon|FullyQualifiedName~Arm64|FullyQualifiedName~HostCodeCoherence'
# the portable test platform (Microsoft.TestPlatform.Portable), the version of the test project's Microsoft.NET.Test.Sdk
readonly VSTEST_VERSION='17.14.1'
# qemu-user 10 or newer runs code rewritten through a second mapping (7.2 does not); Debian trixie's is static
readonly QEMU_MIN_MAJOR=10
readonly QEMU_POOL='https://deb.debian.org/debian/pool/main/q/qemu/'
readonly BINFMT='/proc/sys/fs/binfmt_misc'
readonly BINFMT_MAGIC='\x7f\x45\x4c\x46\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00\x02\x00\xb7\x00'
readonly BINFMT_MASK='\xff\xff\xff\xff\xff\xff\xff\x00\xff\xff\xff\xff\xff\xff\xff\xff\xfe\xff\xff\xff'
readonly APT_PACKAGES='unzip qemu-user-static binfmt-support libc6:arm64 libstdc++6:arm64 libgcc-s1:arm64 libssl3:arm64 zlib1g:arm64'

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
# Prints the version of a qemu-aarch64 binary ("10.0.13"), or nothing.
# Arguments:
#   The binary.
#######################################
qemu_version() {
  { "$1" --version 2>/dev/null || true; } | awk 'NR == 1 {print $3}'
}

#######################################
# Prints the file a path's symbolic links lead to (Debian's binfmt interpreter, aarch64-binfmt-P, is a link to
# qemu-aarch64-static: run by that name, qemu takes its first argument as the program, so --version needs the target).
# Arguments:
#   The path.
#######################################
resolve_links() {
  local path="$1" target
  while [[ -L "${path}" ]]; do
    target="$(readlink "${path}")"
    if [[ "${target}" == /* ]]; then
      path="${target}"
    else
      path="$(dirname "${path}")/${target}"
    fi
  done
  echo "${path}"
}

#######################################
# Prints the interpreter of the qemu-aarch64 binfmt entry, or nothing.
#######################################
binfmt_interpreter() {
  if [[ -e "${BINFMT}/qemu-aarch64" ]]; then
    awk '$1 == "interpreter" {print $2}' "${BINFMT}/qemu-aarch64"
  fi
}

#######################################
# Installs Debian trixie's qemu-user (the newest stable 10.x; its binaries are static) into a folder of its own: only
# qemu-aarch64, and aarch64-binfmt-P, the name the binfmt entry runs it by (argv[0] preserved).
# Arguments:
#   The folder.
#######################################
install_qemu() {
  local version deb work
  version="$(qemu_version "$1/qemu-aarch64")"
  # a qemu-aarch64 already there of another version is replaced
  if [[ -n "${version}" && "${version%%.*}" -ge "${QEMU_MIN_MAJOR}" ]]; then
    return 0
  fi
  deb="$(curl -sSL "${QEMU_POOL}" | grep -o 'qemu-user_1[0-9]\.[0-9.]*+ds-[0-9]*+deb13u[0-9]*_amd64\.deb' | sort -u -V |
    tail -n 1)"
  [[ -n "${deb}" ]] || die "no qemu-user 10 package found in ${QEMU_POOL}"
  work="$(mktemp -d)"
  curl -sSL -o "${work}/qemu.deb" "${QEMU_POOL}${deb}"
  dpkg-deb -x "${work}/qemu.deb" "${work}/x"
  mkdir -p "$1"
  cp "${work}/x/usr/bin/qemu-aarch64" "$1/qemu-aarch64"
  rm -rf "${work:?}"
  ln -sf qemu-aarch64 "$1/aarch64-binfmt-P"
  echo "qemu-arm64: installed $("$1/qemu-aarch64" --version | head -n 1) in $1"
}

#######################################
# Registers a folder's qemu-aarch64 as the arm64 binfmt interpreter (root), replacing the entry there was. Flags:
# P keeps argv[0], F opens the interpreter now (it runs in other mount namespaces too).
# Arguments:
#   The folder.
#######################################
register_binfmt() {
  local interpreter="$1/aarch64-binfmt-P"
  [[ -x "${interpreter}" ]] || die "no qemu-aarch64 in $1 (run with --install-qemu)"
  if [[ "$(binfmt_interpreter)" == "${interpreter}" ]]; then
    return 0
  fi
  if [[ -e "${BINFMT}/qemu-aarch64" ]]; then
    echo -1 >"${BINFMT}/qemu-aarch64"
  fi
  printf ':qemu-aarch64:M::%s:%s:%s:PF' "${BINFMT_MAGIC}" "${BINFMT_MASK}" "${interpreter}" >"${BINFMT}/register"
}

#######################################
# Installs qemu-user, the binfmt entry, the arm64 libraries, the arm64 .NET runtime and the portable test platform.
# Arguments:
#   The arm64 runtime folder, the portable test platform folder, the qemu-user 10 folder.
#######################################
install_requirements() {
  if ! dpkg --print-foreign-architectures | grep -qx arm64; then
    sudo dpkg --add-architecture arm64
  fi
  sudo apt-get update -qq
  # shellcheck disable=SC2086 # the package list is split on purpose
  sudo DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends ${APT_PACKAGES} \
    "$(apt-cache search --names-only '^libicu[0-9]+$' | awk '{print $1; exit}'):arm64"
  install_qemu "$3"
  if [[ ! -x "$1/dotnet" ]]; then
    local installer
    installer="$(mktemp)"
    curl -sSL https://dot.net/v1/dotnet-install.sh -o "${installer}"
    bash "${installer}" --channel 10.0 --runtime dotnet --architecture arm64 --install-dir "$1"
    rm -f "${installer}"
  fi
  if [[ ! -f "$2/tools/net8.0/vstest.console.dll" ]]; then
    mkdir -p "$2"
    curl -sSL -o "$2/platform.zip" "https://www.nuget.org/api/v2/package/Microsoft.TestPlatform.Portable/${VSTEST_VERSION}"
    (cd "$2" && unzip -q -o platform.zip && rm -f platform.zip)
  fi
}

#######################################
# Fails unless arm64 ELF files start through qemu (the binfmt_misc entry) and the arm64 runtime is there; the entry
# runs the qemu-user 10 of the qemu folder when it is installed. Exports VITASHARP_QEMU_USER (the qemu version the
# tests run under).
# Arguments:
#   The arm64 runtime folder, the qemu-user 10 folder.
#######################################
check_requirements() {
  [[ "$(uname -s)" == "Linux" ]] || die "needs Linux (macOS has no qemu-user; Apple silicon runs the tests natively)"
  [[ "$(uname -m)" == "x86_64" ]] || die "the host is $(uname -m): run the tests natively there"
  if [[ -x "$2/aarch64-binfmt-P" && "$(binfmt_interpreter)" != "$2/aarch64-binfmt-P" ]]; then
    sudo -n env QEMU_ARM64_DIR="$2" bash "${BASH_SOURCE[0]}" --register-binfmt ||
      echo "qemu-arm64: could not register $2's qemu (sudo env QEMU_ARM64_DIR=$2 bash ${BASH_SOURCE[0]} --register-binfmt)" >&2
  fi
  if [[ ! -e "${BINFMT}/qemu-aarch64" ]]; then
    # WSL without systemd does not register the entries at boot
    if command -v update-binfmts >/dev/null 2>&1; then
      sudo -n update-binfmts --enable qemu-aarch64 || true
    fi
    [[ -e "${BINFMT}/qemu-aarch64" ]] ||
      die "no qemu-aarch64 binfmt entry (sudo update-binfmts --enable qemu-aarch64; WSL: wsl -u root -- update-binfmts --enable qemu-aarch64)"
  fi
  [[ -x "$1/dotnet" ]] || die "no arm64 .NET runtime in $1 (run with --install)"
  "$1/dotnet" --list-runtimes >/dev/null || die "the arm64 runtime in $1 does not start under qemu"
  # never empty under qemu: the tests read it as "this run is emulated"
  VITASHARP_QEMU_USER="$(qemu_version "$(resolve_links "$(binfmt_interpreter)")")"
  VITASHARP_QEMU_USER="${VITASHARP_QEMU_USER:-unknown}"
  export VITASHARP_QEMU_USER
  echo "qemu-arm64: arm64 processes run under qemu-user ${VITASHARP_QEMU_USER} ($(binfmt_interpreter))"
  if [[ "${VITASHARP_QEMU_USER}" == unknown || "${VITASHARP_QEMU_USER%%.*}" -lt "${QEMU_MIN_MAJOR}" ]]; then
    echo "qemu-arm64: qemu ${VITASHARP_QEMU_USER} runs stale rewritten code: the ARM64 host tests skip (--install-qemu)" >&2
  fi
}

#######################################
# Runs the tests of a linux-arm64 build with the portable test platform on the arm64 runtime (under qemu).
# Arguments:
#   Test binaries folder, filter, output folder, arm64 runtime folder, portable test platform folder.
#######################################
run_tests() {
  local vstest="$5/tools/net8.0/vstest.console.dll" start code
  # the portable test platform on the arm64 runtime: the whole run (test host included) is arm64. The x64 vstest
  # finds no arm64 host on Linux ("Could not find 'dotnet' host for the 'ARM64' architecture") and the SDK's
  # vstest.console.dll is built for x64 (BadImageFormatException on arm64). /Blame names the test a crash stopped in
  # (Sequence.xml in the results folder; no dump).
  [[ -f "${vstest}" ]] || die "no portable test platform in $5 (run with --install)"
  start="$(date +%s)"
  set +e
  DOTNET_ROOT="$4" "$4/dotnet" "${vstest}" "$1/VitaSharp.Tests.dll" \
    /TestCaseFilter:"$2" /ResultsDirectory:"$3/tests" /logger:'trx;LogFileName=arm64.trx' \
    /logger:'console;verbosity=normal' /Blame -- RunConfiguration.MaxCpuCount=1 2>&1 | tee "$3/tests.log"
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
  # the log says the run was under qemu-user (the arm64 code was emulated, its timings are not the phone's)
  {
    echo "# run under qemu-user: ${VITASHARP_QEMU_USER} ($(binfmt_interpreter)), cpu ${QEMU_CPU:-max}"
    echo "# test game exit ${code} after $(($(date +%s) - start)) s"
  } >>"$4/testgame.log"
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
  local vstest_dir="${VSTEST_PORTABLE:-${HOME}/.vstest-portable}"
  local qemu_dir="${QEMU_ARM64_DIR:-${HOME}/.qemu-arm64}"
  while (($# > 0)); do
    case "$1" in
      -h | --help)
        sed -n '3,27s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
        return 0
        ;;
      --install) install=1 ;;
      --install-qemu)
        install_qemu "${qemu_dir}"
        return 0
        ;;
      --register-binfmt)
        register_binfmt "${qemu_dir}"
        return 0
        ;;
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
  # qemu-user does not see code the .NET JIT writes through the writable alias of its dual-mapped (W^X) code heap: the
  # stale translation runs instead (NullReferenceException in reflection stubs, SIGSEGV). Single-mapped RWX code is
  # written and run at one address, which qemu tracks.
  export DOTNET_EnableWriteXorExecute=0
  if ((install == 1)); then
    install_requirements "${runtime}" "${vstest_dir}" "${qemu_dir}"
  fi
  check_requirements "${runtime}" "${qemu_dir}"
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
    run_tests "${tests_dir}" "${filter}" "${out}" "${runtime}" "${vstest_dir}" || code=$?
  fi
  if [[ -n "${vpk}" ]]; then
    if [[ -z "${desktop_dir}" ]]; then
      desktop_dir="${out}/desktop-bin"
      "${dotnet}" publish "${ROOT}/src/VitaSharp.Desktop" -c Release -r linux-arm64 --self-contained true \
        -o "${desktop_dir}" -m:2 -p:UseSharedCompilation=false -nologo -v:q
    fi
    run_testgame "${desktop_dir}" "${vpk}" "${seconds}" "${out}" || code=$?
  fi
  echo "qemu-arm64: output in ${out}"
  return "${code}"
}

main "$@"
