#!/usr/bin/env bash
#
# Checks the C# code style of .editorconfig (the Ryujinx/Ryubing style): `dotnet format whitespace` (layout) over
# every .cs file and `dotnet format style` (the rules set to :warning) over VitaSharp.sln, both with
# --verify-no-changes. The folders listed in build/format-pending.txt are skipped until they are converted.
# Same as build/format-check.ps1.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly PENDING_FILE="${ROOT}/build/format-pending.txt"

#######################################
# Prints the usage.
# Outputs:
#   The usage text to stdout.
#######################################
usage() {
  cat <<'EOF'
Usage: build/format-check.sh [--fix] [--whitespace-only] [--help]
  (no option)        verify: exits 1 and lists the files that differ from .editorconfig
  --fix              rewrite the files instead of verifying (review the diff before committing)
  --whitespace-only  only the layout check (no project load, no restore needed)
Folders in build/format-pending.txt are skipped. Same as build/format-check.ps1.
EOF
}

#######################################
# Reads build/format-pending.txt.
# Outputs:
#   One folder per line to stdout (comments and blank lines dropped).
#######################################
pending_folders() {
  local line
  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%%#*}"
    line="${line%$'\r'}"
    line="${line%"${line##*[![:space:]]}"}"
    [[ -n "${line}" ]] && printf '%s\n' "${line}"
  done <"${PENDING_FILE}"
}

main() {
  local verify="--verify-no-changes"
  local whitespace_only=0
  local arg
  for arg in "$@"; do
    case "${arg}" in
      --fix) verify="" ;;
      --whitespace-only) whitespace_only=1 ;;
      -h | --help)
        usage
        return 0
        ;;
      *)
        echo "unknown option: ${arg}" >&2
        usage >&2
        return 2
        ;;
    esac
  done

  local -a exclude=()
  local folder
  while IFS= read -r folder; do
    exclude+=("${folder}")
  done < <(pending_folders)

  cd "${ROOT}"
  local -a exclude_args=()
  if ((${#exclude[@]} > 0)); then
    exclude_args=(--exclude "${exclude[@]}")
  fi

  echo "format-check: whitespace (${#exclude[@]} folders pending)"
  # ${verify} is either empty or one option; the array expansion is bash 3.2-safe when empty
  # shellcheck disable=SC2086
  dotnet format whitespace --folder ${verify} ${exclude_args[@]+"${exclude_args[@]}"}
  if ((whitespace_only == 0)); then
    echo "format-check: style (severity warn)"
    # shellcheck disable=SC2086
    dotnet format style VitaSharp.sln --severity warn ${verify} ${exclude_args[@]+"${exclude_args[@]}"}
  fi
  echo "format-check: ok"
}

main "$@"
