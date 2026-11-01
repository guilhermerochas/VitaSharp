#!/usr/bin/env bash
# Checks that a packaged build has none of the developer tooling (-p:VitaSharpDeveloper=true): searches an APK (unpacked)
# or a desktop publish folder for the tooling's type names (AOT images keep them as symbols). --expect-dev inverts the
# check (a developer APK must have them). Usage: build/check-dev-gating.sh APK|FOLDER [--expect-dev]   (same as .ps1)
set -euo pipefail
target="${1:?usage: check-dev-gating.sh APK|FOLDER [--expect-dev]}"
expect="${2:-}"
pattern='GpuBenchState|GpuBenchSpec|BenchActivity|DevSceneBegin|DevTrace'
dir="$target"
tmp=""
if [ -f "$target" ]; then tmp="$(mktemp -d)"; unzip -q -o "$target" -d "$tmp"; dir="$tmp"; fi
# .pdb files only name the source files (Developer/*.cs is compiled empty without the property)
hits="$(grep -rlaE --exclude=*.pdb "$pattern" "$dir" 2>/dev/null || true)"
[ -n "$tmp" ] && rm -rf "$tmp"
if [ "$expect" = "--expect-dev" ]; then
  [ -n "$hits" ] && { echo "developer tooling present (expected): $(echo "$hits" | wc -l) files"; exit 0; }
  echo "developer tooling NOT found in $target" >&2; exit 1
fi
[ -z "$hits" ] && { echo "no developer tooling in $target"; exit 0; }
echo "developer tooling found in $target:" >&2; echo "$hits" | sed "s|$dir/||" >&2; exit 1
