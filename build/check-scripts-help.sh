#!/usr/bin/env bash
#
# Smoke test of the shell scripts on this platform (the CI runs it on macOS: bash 3.2 and the BSD tools): every
# tracked .sh must parse (bash -n, sh -n for the POSIX sh ones) and answer --help with exit code 0 without doing any
# work. Usage: build/check-scripts-help.sh   (same as build/check-scripts-help.ps1)

set -euo pipefail

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,5s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  cd "$(dirname "${BASH_SOURCE[0]}")/.."
  local failed=0 f shell out
  while IFS= read -r f; do
    shell="${BASH}" # the bash running this check (/bin/bash 3.2 on macOS when started with it)
    if [[ "$(head -n 1 "${f}")" == "#!/bin/sh" ]]; then
      shell="sh"
    fi
    if ! "${shell}" -n "${f}"; then
      echo "does not parse: ${f}"
      failed=1
      continue
    fi
    if ! out="$("${shell}" "${f}" --help 2>&1)"; then
      echo "--help failed: ${f}"
      printf '%s\n' "${out}" | head -n 5
      failed=1
    elif [[ -z "${out}" ]]; then
      echo "--help printed nothing: ${f}"
      failed=1
    fi
  done < <(git ls-files '*.sh')
  if ((failed == 0)); then
    echo "scripts --help: ok"
  fi
  return "${failed}"
}

main "$@"
