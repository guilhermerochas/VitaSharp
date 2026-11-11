#!/usr/bin/env bash
#
# Regenerates the application icons (src/VitaSharp.Desktop/Assets: icon-*.png, VitaSharp.ico, VitaSharp.icns; the
# Android adaptive icon in src/VitaSharp.Android/Platforms/Android/Resources) from docs/assets/logo.svg. Run it after
# changing the logo and commit the result; normal builds use the committed icons.
# Usage: build/make-icons.sh   (same as build/make-icons.ps1)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    echo "Usage: build/make-icons.sh   (regenerates the icons from docs/assets/logo.svg)"
    return 0
  fi
  dotnet run --project "${ROOT}/build/IconGen/IconGen.csproj" -c Release -- "${ROOT}/docs/assets/logo.svg" \
    "${ROOT}/src/VitaSharp.Desktop/Assets" "${ROOT}/src/VitaSharp.Android/Platforms/Android/Resources"
}

main "$@"
