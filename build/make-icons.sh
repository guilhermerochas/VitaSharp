#!/usr/bin/env bash
# Regenerates the application icons (src/VitaSharp.Desktop/Assets: icon-*.png, VitaSharp.ico, VitaSharp.icns) from
# docs/assets/logo.svg. Run it after changing the logo and commit the result; normal builds use the committed icons.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet run --project "$root/build/IconGen/IconGen.csproj" -c Release -- "$root/docs/assets/logo.svg" "$root/src/VitaSharp.Desktop/Assets"
