#!/usr/bin/env bash
# Multiplatform rule: every script ships for Windows (name.ps1) and Linux/macOS (name.sh), doing the same thing.
# Fails when a tracked .ps1 has no .sh twin or a .sh has no .ps1 twin, except the single-platform scripts listed below.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

# inherently single-platform: run by users of that platform's package only
exempt=(
  build/package/install-desktop-entry.sh   # Linux desktop entry, shipped in the Linux tar.gz/AppImage
)

is_exempt() { local f; for f in "${exempt[@]}"; do [ "$f" = "$1" ] && return 0; done; return 1; }

missing=0
while IFS= read -r f; do
  twin="${f%.ps1}.sh"
  if ! is_exempt "$f" && [ ! -f "$twin" ]; then echo "missing: $twin (twin of $f)"; missing=1; fi
done < <(git ls-files '*.ps1')
while IFS= read -r f; do
  twin="${f%.sh}.ps1"
  if ! is_exempt "$f" && [ ! -f "$twin" ]; then echo "missing: $twin (twin of $f)"; missing=1; fi
  if [ "$(git ls-files -s "$f" | cut -c1-6)" != "100755" ]; then echo "not executable in git: $f"; missing=1; fi
done < <(git ls-files '*.sh')

[ "$missing" -eq 0 ] && echo "script parity: ok"
exit "$missing"
