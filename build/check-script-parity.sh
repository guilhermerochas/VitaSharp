#!/usr/bin/env bash
#
# Multiplatform rule: every script ships for Windows (name.ps1) and Linux/macOS (name.sh), doing the same thing.
# Fails when a tracked .ps1 has no .sh twin or a .sh has no .ps1 twin, except the single-platform scripts listed below.
# Usage: build/check-script-parity.sh   (same as build/check-script-parity.ps1)

set -euo pipefail

# inherently single-platform: run by users of that platform's package only
EXEMPT=(
  build/package/install-desktop-entry.sh # Linux desktop entry, shipped in the Linux tar.gz/AppImage
)
readonly EXEMPT

#######################################
# Tells whether a script is exempt from the parity rule.
# Arguments:
#   Repository-relative script path.
# Returns:
#   0 when exempt, 1 otherwise.
#######################################
is_exempt() {
  local f
  for f in "${EXEMPT[@]}"; do
    if [[ "${f}" == "$1" ]]; then
      return 0
    fi
  done
  return 1
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,5s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  cd "$(dirname "${BASH_SOURCE[0]}")/.."

  local missing=0 f twin
  while IFS= read -r f; do
    twin="${f%.ps1}.sh"
    if ! is_exempt "${f}" && [[ ! -f "${twin}" ]]; then
      echo "missing: ${twin} (twin of ${f})"
      missing=1
    fi
  done < <(git ls-files '*.ps1')
  while IFS= read -r f; do
    twin="${f%.sh}.ps1"
    if ! is_exempt "${f}" && [[ ! -f "${twin}" ]]; then
      echo "missing: ${twin} (twin of ${f})"
      missing=1
    fi
    if [[ "$(git ls-files -s "${f}" | cut -c1-6)" != "100755" ]]; then
      echo "not executable in git: ${f}"
      missing=1
    fi
  done < <(git ls-files '*.sh')

  if ((missing == 0)); then
    echo "script parity: ok"
  fi
  return "${missing}"
}

main "$@"
