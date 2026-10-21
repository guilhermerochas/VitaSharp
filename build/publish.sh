#!/usr/bin/env bash
# Self-contained, single-file ReadyToRun builds of VitaSharp (launcher + emulator in one executable).
#   build/publish.sh                      all platforms
#   build/publish.sh linux-x64 osx-arm64  only these
# Output: artifacts/publish/<rid>/ (override with OUT=<folder>). Any host can publish for any platform; FFmpeg is
# downloaded for the target platform (macOS uses the system FFmpeg: brew install ffmpeg).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${OUT:-$root/artifacts/publish}"
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
for rid in "${rids[@]}"; do
  echo "== $rid"
  dotnet publish "$root/src/VitaSharp.Desktop/VitaSharp.Desktop.csproj" -c Release -r "$rid" \
    -p:VitaSharpRelease=true -p:DebugType=embedded -o "$out/$rid"
done
