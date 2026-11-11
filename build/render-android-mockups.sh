#!/usr/bin/env bash
#
# Renders the Android UI mockups (docs/android/mockups/<name>.html) to docs/android/screens/<name>.png at 2x scale:
# headless Chromium/Chrome/Edge in a 1000x1000 window, then the centred 390x844 phone frame (844x390 for the landscape
# screens) is cropped out with ImageMagick. Pass names to render only some screens:
#   build/render-android-mockups.sh jogo sistema   (same as build/render-android-mockups.ps1)
# Needs a Chromium-based browser (CHROME=/path/to/browser to pick one) and ImageMagick (magick or convert).
# The mockups load Sora/Inter from Google Fonts, so render with network access.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/docs/android"
readonly ROOT

#######################################
# Finds a Chromium-based browser (CHROME, else the usual names and macOS app paths).
# Outputs:
#   The browser command to stdout (empty when none is found).
#######################################
find_browser() {
  if [[ -n "${CHROME:-}" ]]; then
    printf '%s' "${CHROME}"
    return 0
  fi
  local b
  for b in chromium chromium-browser google-chrome google-chrome-stable microsoft-edge \
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
    "/Applications/Chromium.app/Contents/MacOS/Chromium" \
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"; do
    if command -v "${b}" >/dev/null 2>&1 || [[ -x "${b}" ]]; then
      printf '%s' "${b}"
      return 0
    fi
  done
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,8s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local browser
  browser="$(find_browser)"
  if [[ -z "${browser}" ]]; then
    echo "No Chromium-based browser found (set CHROME=)" >&2
    return 1
  fi
  local im
  if command -v magick >/dev/null 2>&1; then
    im=magick
  elif command -v convert >/dev/null 2>&1; then
    im=convert
  else
    echo "ImageMagick (magick or convert) not found" >&2
    return 1
  fi

  local work
  work="$(mktemp -d)"
  # shellcheck disable=SC2064  # expand now: ${work} is local to main
  trap "rm -rf '${work}'" EXIT

  local -a names=()
  local f n
  if (($# > 0)); then
    names=("$@")
  else
    for f in "${ROOT}"/mockups/*.html; do
      n="$(basename "${f}")"
      names+=("${n%.html}")
    done
  fi

  local raw crop
  for n in "${names[@]}"; do
    raw="${work}/${n}.png"
    "${browser}" --headless=new --disable-gpu --hide-scrollbars --user-data-dir="${work}/profile" \
      --force-device-scale-factor=2 --window-size=1000,1000 --virtual-time-budget=8000 \
      --screenshot="${raw}" "file://${ROOT}/mockups/${n}.html" >/dev/null 2>&1
    case "${n}" in
      jogo | sistema) crop="1688x780+156+610" ;; # landscape
      *) crop="780x1688+610+156" ;;
    esac
    "${im}" "${raw}" -crop "${crop}" +repage -alpha off "${ROOT}/screens/${n}.png"
    echo "screens/${n}.png"
  done
}

main "$@"
