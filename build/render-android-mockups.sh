#!/usr/bin/env bash
# Renders the Android UI mockups (docs/android/mockups/<name>.html) to docs/android/screens/<name>.png at 2x scale:
# headless Chromium/Chrome/Edge in a 1000x1000 window, then the centred 390x844 phone frame (844x390 for the landscape
# screens) is cropped out with ImageMagick. Pass names to render only some screens: build/render-android-mockups.sh jogo sistema
# Needs a Chromium-based browser (CHROME=/path/to/browser to pick one) and ImageMagick (magick or convert).
# The mockups load Sora/Inter from Google Fonts, so render with network access.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/docs/android"

browser="${CHROME:-}"
if [ -z "$browser" ]; then
  for b in chromium chromium-browser google-chrome google-chrome-stable microsoft-edge \
           "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
           "/Applications/Chromium.app/Contents/MacOS/Chromium" \
           "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"; do
    if command -v "$b" >/dev/null 2>&1 || [ -x "$b" ]; then browser="$b"; break; fi
  done
fi
[ -n "$browser" ] || { echo "No Chromium-based browser found (set CHROME=)" >&2; exit 1; }
if command -v magick >/dev/null 2>&1; then im=(magick); elif command -v convert >/dev/null 2>&1; then im=(convert); else
  echo "ImageMagick (magick or convert) not found" >&2; exit 1; fi

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

if [ "$#" -gt 0 ]; then names=("$@"); else
  names=(); for f in "$root"/mockups/*.html; do n="$(basename "$f")"; names+=("${n%.html}"); done
fi

for n in "${names[@]}"; do
  raw="$work/$n.png"
  "$browser" --headless=new --disable-gpu --hide-scrollbars --user-data-dir="$work/profile" \
    --force-device-scale-factor=2 --window-size=1000,1000 --virtual-time-budget=8000 \
    --screenshot="$raw" "file://$root/mockups/$n.html" >/dev/null 2>&1
  case "$n" in
    jogo|sistema) crop="1688x780+156+610" ;;  # landscape
    *)            crop="780x1688+610+156" ;;
  esac
  "${im[@]}" "$raw" -crop "$crop" +repage -alpha off "$root/screens/$n.png"
  echo "screens/$n.png"
done
