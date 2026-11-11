#!/usr/bin/env bash
#
# Checks the C# code style of .editorconfig (the Ryujinx/Ryubing style): `dotnet format whitespace` (layout) and
# `dotnet format style` (the rules set to :warning, through VitaSharp.sln), both with --verify-no-changes, over the
# repository's tracked .cs files only (git ls-files: never .claude/ with the agents' worktrees, nor untracked files).
# The folders listed in build/format-pending.txt are skipped until they are converted.
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
  --fix              rewrite the files instead of verifying (dotnet format + build/FormatTool: one-line blocks
                     split, accessor lists on one line); review the diff before committing
  --whitespace-only  only the layout check (no project load, no restore needed)
Only the tracked .cs files (git ls-files; never .claude/); folders in build/format-pending.txt are skipped.
Same as build/format-check.ps1.
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

#######################################
# Tells whether a repository-relative path lies in one of the given folders.
# Arguments:
#   The path, then the folders.
# Returns:
#   0 when it does, 1 otherwise.
#######################################
is_pending() {
  local path="$1"
  shift
  local dir
  for dir in "$@"; do
    if [[ "${path}" == "${dir}/"* ]]; then
      return 0
    fi
  done
  return 1
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

  local -a pending=()
  local folder
  while IFS= read -r folder; do
    pending+=("${folder%/}")
  done < <(pending_folders)
  # .claude/ holds the agents' worktrees (whole other checkouts): never part of this checkout's check
  pending+=(".claude")

  cd "${ROOT}"
  # the file list goes through a response file (one token per line), like build/format-check.ps1 does
  # global, not local: the EXIT trap runs after main has returned
  include_file="$(mktemp)"
  trap 'rm -f "${include_file}"' EXIT
  echo "--include" >"${include_file}"
  local file count=0
  while IFS= read -r file; do
    if ! is_pending "${file}" "${pending[@]}"; then
      echo "${file}" >>"${include_file}"
      count=$((count + 1))
    fi
  done < <(git ls-files -- '*.cs')
  if ((count == 0)); then
    echo "format-check: git ls-files found no .cs files (run it from a git checkout)" >&2
    return 2
  fi
  local include="@${include_file}"

  echo "format-check: whitespace (${count} tracked files, $((${#pending[@]} - 1)) folders pending)"
  # ${verify} is either empty or one option
  # shellcheck disable=SC2086
  dotnet format whitespace --folder ${verify} "${include}"
  if ((whitespace_only == 0)); then
    echo "format-check: style (severity warn)"
    # shellcheck disable=SC2086
    dotnet format style VitaSharp.sln --severity warn ${verify} "${include}"
  fi
  if [[ -z "${verify}" ]]; then
    # what dotnet format cannot do: split one-line statement blocks, keep { get; set; } on one line (the check is
    # tests/VitaSharp.Tests/OneLineBlockTests); then indent the split lines. FormatTool reads the same list.
    echo "format-check: layout (build/FormatTool)"
    dotnet run --project "${ROOT}/build/FormatTool" -c Release -v q -- "${ROOT}" "${include_file}"
    dotnet format whitespace --folder "${include}"
  fi
  echo "format-check: ok"
}

main "$@"
