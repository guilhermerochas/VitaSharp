#!/usr/bin/env bash
#
# Runs, on exactly what is committed (HEAD, in a clean temporary worktree), the checks the CI runs: script parity,
# scripts' --help, ShellCheck (when installed), the C# code style, a 0-warning Release build of the solution and the
# Category!=Slow test suite. Push only when it ends with "prepush-check: ok".
# Same as build/prepush-check.ps1.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

#######################################
# Prints the usage.
# Outputs:
#   The usage text to stdout.
#######################################
usage() {
  cat <<'USAGE'
Usage: build/prepush-check.sh [--help]
  Checks HEAD (committed files only, in a temporary worktree) the way the CI does:
  script parity, scripts' --help, ShellCheck (if installed), format-check, Release build with 0 warnings,
  dotnet test --filter "Category!=Slow". Exits 1 at the first failure.
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

if [[ "${1:-}" == "--help" || "${1:-}" == "-h" ]]; then
  usage
  exit 0
fi

work="$(mktemp -d)"
cleanup() {
  git -C "${ROOT}" worktree remove --force "${work}/tree" >/dev/null 2>&1 || true
  rm -rf "${work}"
}
trap cleanup EXIT

git -C "${ROOT}" worktree add --detach "${work}/tree" HEAD >/dev/null
cd "${work}/tree"

echo "prepush-check: script parity"
bash build/check-script-parity.sh || die "script parity"
echo "prepush-check: scripts --help"
bash build/check-scripts-help.sh || die "scripts --help"
if command -v shellcheck >/dev/null 2>&1; then
  echo "prepush-check: shellcheck"
  git ls-files '*.sh' | xargs shellcheck || die "shellcheck"
else
  echo "prepush-check: shellcheck not installed, skipped (the CI runs it)"
fi
echo "prepush-check: format-check"
bash build/format-check.sh || die "format-check"
echo "prepush-check: build"
build_log="${work}/build.log"
dotnet build VitaSharp.sln -c Release -m:2 -nologo -v q >"${build_log}" 2>&1 || { tail -30 "${build_log}"; die "build"; }
if grep -Eq "[1-9][0-9]* (Warning|Aviso)\(s\)" "${build_log}"; then
  grep -E "warning " "${build_log}" | sort -u | head -20
  die "build has warnings"
fi
echo "prepush-check: tests"
dotnet test tests/VitaSharp.Tests -c Release --no-build --filter "Category!=Slow" --blame-hang-timeout 10m \
  -- RunConfiguration.MaxCpuCount=2 || die "tests"
# On Windows (Git Bash) the Linux leg runs in WSL when it has a .NET SDK in ~/.dotnet.
# shellcheck disable=SC2016 # $HOME expands inside WSL, not here
if command -v wsl >/dev/null 2>&1 && [[ "$(wsl -- bash -lc 'test -x $HOME/.dotnet/dotnet && echo yes' 2>/dev/null | tr -d '\r\0')" == "yes" ]]; then
  echo "prepush-check: tests on Linux (WSL)"
  git archive -o "${work}/head.tar" HEAD
  linux_archive="$(wsl -- wslpath -a "$(cygpath -m "${work}/head.tar")" | tr -d '\r\0')"
  wsl -- bash -lc "rm -rf ~/vitasharp-prepush && mkdir ~/vitasharp-prepush && tar -xf '${linux_archive}' -C ~/vitasharp-prepush && cd ~/vitasharp-prepush && \$HOME/.dotnet/dotnet test tests/VitaSharp.Tests -c Release --filter 'Category!=Slow' --blame-hang-timeout 10m -- RunConfiguration.MaxCpuCount=2" \
    || die "tests on Linux (WSL)"
elif command -v wsl >/dev/null 2>&1; then
  echo "prepush-check: no .NET SDK in WSL, Linux tests skipped (the CI runs them)"
fi
echo "prepush-check: ok"
