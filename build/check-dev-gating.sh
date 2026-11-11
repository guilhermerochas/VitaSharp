#!/usr/bin/env bash
#
# Checks that a packaged build has none of the developer tooling (-p:VitaSharpDeveloper=true): searches an APK
# (unpacked) or a desktop publish folder for the tooling's type names (AOT images keep them as symbols). --expect-dev
# inverts the check (a developer APK must have them).
# Usage: build/check-dev-gating.sh APK|FOLDER [--expect-dev]   (same as build/check-dev-gating.ps1)

set -euo pipefail

readonly PATTERN='GpuBenchState|GpuBenchSpec|BenchActivity|DevSceneBegin|DevTrace'
readonly USAGE="usage: check-dev-gating.sh APK|FOLDER [--expect-dev]"

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,6s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local target="${1:?${USAGE}}"
  local expect="${2:-}"
  local dir="${target}"
  local tmp=""
  if [[ -f "${target}" ]]; then
    tmp="$(mktemp -d)"
    unzip -q -o "${target}" -d "${tmp}"
    dir="${tmp}"
  fi
  # .pdb files only name the source files (Developer/*.cs is compiled empty without the property)
  local hits
  hits="$(grep -rlaE --exclude='*.pdb' "${PATTERN}" "${dir}" 2>/dev/null || true)"
  if [[ -n "${tmp}" ]]; then
    rm -rf "${tmp}"
  fi
  if [[ "${expect}" == "--expect-dev" ]]; then
    if [[ -n "${hits}" ]]; then
      echo "developer tooling present (expected): $(echo "${hits}" | wc -l) files"
      return 0
    fi
    echo "developer tooling NOT found in ${target}" >&2
    return 1
  fi
  if [[ -z "${hits}" ]]; then
    echo "no developer tooling in ${target}"
    return 0
  fi
  echo "developer tooling found in ${target}:" >&2
  printf '%s\n' "${hits//"${dir}"\//}" >&2
  return 1
}

main "$@"
