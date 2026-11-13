#!/usr/bin/env bash
#
# Runs, on exactly what is committed (HEAD, in a clean temporary worktree), the checks the CI runs: script parity,
# scripts' --help, ShellCheck (when installed), the C# code style, a 0-warning Release build of the solution and the
# Category!=Slow test suite (on Windows also on Linux, through WSL, when it has a .NET SDK in ~/.dotnet). Push only
# when it ends with "prepush-check: ok". Same as build/prepush-check.ps1.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly TEST_PROJECT="tests/VitaSharp.Tests"
readonly TEST_FILTER="Category!=Slow"
readonly WSL_TREE="vitasharp-prepush"

#######################################
# Prints the usage.
# Outputs:
#   The usage text to stdout.
#######################################
usage() {
  cat <<'USAGE'
Usage: build/prepush-check.sh [--tests-only] [--help]
  Checks HEAD (committed files only, in a temporary worktree) the way the CI does:
  script parity, scripts' --help, ShellCheck (if installed), format-check, Release build with 0 warnings,
  dotnet test --filter "Category!=Slow"; on Windows (Git Bash) the suite also runs on Linux through WSL when WSL
  has a .NET SDK in ~/.dotnet. Exits 1 at the first failure.
  The suite fails when dotnet test fails, when the run was aborted (test host crash) or when it ran fewer tests
  than dotnet test --list-tests finds for the filter.
  --tests-only  only builds the test project and runs the suite, in the current tree (what the WSL leg runs)
USAGE
}

#######################################
# Prints an error and exits.
# Arguments:
#   Message.
#######################################
die() {
  echo "prepush-check: $*" >&2
  exit 1
}

#######################################
# No process of this check writes a core dump: Linux pipes crashes to a collector (WSL: wsl-capture-crash, which
# ignores the core size limit and wrote 8 GB per test host crash into %TEMP%\wsl-crashes), so the memory mappings are
# also left out of any dump (coredump_filter 0: a few kB of headers instead of the whole address space).
#######################################
no_core_dumps() {
  ulimit -c 0
  if [[ -w /proc/self/coredump_filter ]]; then
    echo 0 >/proc/self/coredump_filter
  fi
  export DOTNET_DbgEnableMiniDump=0
}

#######################################
# Fails unless a test run's output shows a complete run.
# Arguments:
#   Label of the run, path of its output, its exit code, the number of tests --list-tests found.
#######################################
check_suite_log() {
  local label="$1" log="$2" code="$3" expected="$4" total
  if ((code != 0)); then
    die "${label}: dotnet test exited with ${code}"
  fi
  if grep -Eq "Test Run Aborted|Test host process crashed|test run was aborted" "${log}"; then
    die "${label}: the test run was aborted (test host crash): not every test ran"
  fi
  total="$(sed -nE 's/^(Passed|Failed)!.*Total: *([0-9]+).*/\2/p' "${log}" | tail -n 1)"
  if [[ -z "${total}" ]]; then
    die "${label}: no test summary in the output of dotnet test"
  fi
  if ((total < expected)); then
    die "${label}: ${total} tests ran, ${expected} expected (dotnet test --list-tests)"
  fi
  echo "prepush-check: ${label}: ${total} tests ran (${expected} listed)"
}

#######################################
# Runs the Category!=Slow suite of the built test project and checks that it ran completely.
# Arguments:
#   Label of the run, folder for its output.
#######################################
run_suite() {
  local label="$1" out="$2" expected code
  expected="$(dotnet test "${TEST_PROJECT}" -c Release --no-build --list-tests --filter "${TEST_FILTER}" \
    | grep -c '^    [^ ]')" || die "${label}: dotnet test --list-tests failed"
  code=0
  dotnet test "${TEST_PROJECT}" -c Release --no-build --filter "${TEST_FILTER}" \
    --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type none \
    -- RunConfiguration.MaxCpuCount=2 2>&1 | tee "${out}/tests.log" || code="${PIPESTATUS[0]}"
  check_suite_log "${label}" "${out}/tests.log" "${code}" "${expected}"
}

#######################################
# --tests-only: builds the test project and runs the suite in the current tree.
#######################################
tests_only() {
  local out
  out="$(mktemp -d)"
  # shellcheck disable=SC2064 # the folder is fixed now
  trap "rm -rf '${out}'" EXIT
  cd "${ROOT}"
  echo "prepush-check: build of the tests"
  dotnet build "${TEST_PROJECT}" -c Release -m:2 -p:UseSharedCompilation=false -nologo -v q >"${out}/build.log" 2>&1 \
    || { tail -30 "${out}/build.log"; die "build of the tests"; }
  echo "prepush-check: tests"
  run_suite "tests" "${out}"
  echo "prepush-check: ok"
}

#######################################
# On Windows (Git Bash): runs the suite on Linux in WSL, on HEAD extracted into ~/vitasharp-prepush, when WSL has a
# .NET SDK in ~/.dotnet.
# Arguments:
#   Work folder.
#######################################
wsl_suite() {
  local work="$1" linux_archive
  if ! command -v wsl >/dev/null 2>&1; then
    return 0
  fi
  # shellcheck disable=SC2016 # $HOME expands inside WSL, not here
  if [[ "$(wsl -- bash -lc 'test -x "$HOME/.dotnet/dotnet" && echo yes' 2>/dev/null | tr -d '\r\0')" != "yes" ]]; then
    echo "prepush-check: no .NET SDK in WSL, Linux tests skipped (the CI runs them)"
    return 0
  fi
  echo "prepush-check: tests on Linux (WSL)"
  git archive -o "${work}/head.tar" HEAD
  linux_archive="$(wsl -- wslpath -a "$(cygpath -m "${work}/head.tar")" | tr -d '\r\0')"
  wsl -- bash -lc "export PATH=\"\$HOME/.dotnet:\$PATH\" DOTNET_ROOT=\"\$HOME/.dotnet\" \
    && rm -rf ~/${WSL_TREE} && mkdir ~/${WSL_TREE} && tar -xf '${linux_archive}' -C ~/${WSL_TREE} \
    && bash ~/${WSL_TREE}/build/prepush-check.sh --tests-only; code=\$?; rm -rf ~/${WSL_TREE}; exit \$code" \
    || die "tests on Linux (WSL)"
}

main() {
  if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
    usage
    return 0
  fi
  no_core_dumps
  # English output from dotnet: the run summary and the abort message are read from it
  export DOTNET_CLI_UI_LANGUAGE=en
  if [[ "${1:-}" == "--tests-only" ]]; then
    tests_only
    return 0
  fi

  local work tree build_log
  work="$(mktemp -d)"
  tree="${work}/tree"
  # shellcheck disable=SC2064 # the folders are fixed now
  trap "git -C '${ROOT}' worktree remove --force '${tree}' >/dev/null 2>&1 || true; rm -rf '${work}'" EXIT
  git -C "${ROOT}" worktree add --detach "${tree}" HEAD >/dev/null
  cd "${tree}"

  echo "prepush-check: script parity"
  bash build/check-script-parity.sh || die "script parity"
  echo "prepush-check: scripts --help"
  bash build/check-scripts-help.sh || die "scripts --help"
  if command -v shellcheck >/dev/null 2>&1; then
    echo "prepush-check: shellcheck"
    git ls-files -z '*.sh' | xargs -0 shellcheck || die "shellcheck"
  else
    echo "prepush-check: shellcheck not installed, skipped (the CI runs it)"
  fi
  echo "prepush-check: format-check"
  bash build/format-check.sh || die "format-check"
  echo "prepush-check: build"
  build_log="${work}/build.log"
  dotnet build VitaSharp.sln -c Release -m:2 -p:UseSharedCompilation=false -nologo -v q >"${build_log}" 2>&1 \
    || { tail -30 "${build_log}"; die "build"; }
  if grep -Eq "[1-9][0-9]* (Warning|Aviso)\(s\)" "${build_log}"; then
    grep -E "warning " "${build_log}" | sort -u | head -20
    die "build has warnings"
  fi
  echo "prepush-check: tests"
  run_suite "tests" "${work}"
  wsl_suite "${work}"
  echo "prepush-check: ok"
}

main "$@"
