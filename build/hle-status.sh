#!/usr/bin/env bash
#
# Regenerates the README's HLE status from the installed firmware and games: docs/hle/data/hle-status.tsv (per-library
# summary), the badges and progress map in docs/assets/status, and docs/hle/progress.md
# (VitaSharp.Desktop hle-coverage --status; HleStatusReportTests checks the committed files match the summary).
# Usage: build/hle-status.sh [data folder]   (same as build/hle-status.ps1)
#   data folder: the firmware (os0/vs0) and the games (ux0:app); default VitaSharp's own (only read).
#   env: EXE (a built VitaSharp.Desktop to use), OUT (build folder when EXE is not set; default a temp folder).

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,8s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local data="${1:-}"
  local exe="${EXE:-}"
  if [[ -z "${exe}" ]]; then
    local out="${OUT:-${TMPDIR:-/tmp}/vitasharp-hle-status}"
    dotnet build "${ROOT}/src/VitaSharp.Desktop" -c Release -m:2 -p:UseSharedCompilation=false -v q -o "${out}"
    exe="${out}/VitaSharp.Desktop"
  fi
  local -a cmd=(hle-coverage "--vitasdk=${ROOT}/docs/hle/data/vitasdk-user-functions.tsv" "--status=${ROOT}")
  if [[ -n "${data}" ]]; then
    cmd=("--data=${data}" "${cmd[@]}")
  fi
  "${exe}" "${cmd[@]}"
}

main "$@"
