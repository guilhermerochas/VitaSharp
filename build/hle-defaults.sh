#!/usr/bin/env bash
#
# Lists every HLE function that answers a default value (the [Hle(..., Default = true)] bindings, the "default" rows of
# docs/hle/data/vitasharp-implemented.tsv): library, NID, name, the value it returns and why, grouped by library with
# the counts, as Markdown and PDF (VitaSharp-funcoes-valor-padrao-<date>.md/.pdf).
# Usage: build/hle-defaults.sh [out folder] [--date=yyyy-mm-dd] [--no-pdf]   (same as build/hle-defaults.ps1)
#   out folder: default $HOME/VitaSharpReports (outside the repository; the reports are not committed).
#   The PDF is made by a headless Edge/Chrome/Chromium (BROWSER env var to pick one); without a browser only .md/.html.
#   Run it after each batch of HLE changes; run build/hle-status first when the census changed.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

#######################################
# Finds a browser that can print a page to PDF.
# Outputs:
#   The browser's path to stdout (empty when none).
#######################################
find_browser() {
  local candidate
  if [[ -n "${BROWSER:-}" ]]; then
    printf '%s\n' "${BROWSER}"
    return 0
  fi
  for candidate in microsoft-edge google-chrome chromium chromium-browser chrome msedge \
    "/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe" \
    "/c/Program Files/Google/Chrome/Application/chrome.exe" \
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome" \
    "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge"; do
    if command -v "${candidate}" >/dev/null 2>&1 || [[ -x "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done
}

main() {
  local out="${HOME}/VitaSharpReports"
  local date_arg="$(date +%Y-%m-%d)"
  local pdf=1
  local arg
  for arg in "$@"; do
    case "${arg}" in
      -h | --help)
        sed -n '3,9s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
        return 0
        ;;
      --date=*) date_arg="${arg#--date=}" ;;
      --no-pdf) pdf=0 ;;
      *) out="${arg}" ;;
    esac
  done
  mkdir -p "${out}"
  local prefix="${out}/VitaSharp-funcoes-valor-padrao-${date_arg}"
  dotnet run --project "${ROOT}/build/HleDefaultsTool" -c Release -v q -p:UseSharedCompilation=false -- "${ROOT}" "${prefix}" "--date=${date_arg}"
  if [[ "${pdf}" == 1 ]]; then
    local browser
    browser="$(find_browser)"
    if [[ -z "${browser}" ]]; then
      echo "hle-defaults: no Edge/Chrome/Chromium found: only ${prefix}.md and .html were written" >&2
    else
      local html_url="${prefix}.html"
      if command -v cygpath >/dev/null 2>&1; then
        html_url="$(cygpath -m "${html_url}")"
      fi
      "${browser}" --headless --disable-gpu --no-pdf-header-footer "--print-to-pdf=${prefix}.pdf" "file:///${html_url#/}" >/dev/null 2>&1 || true
      if [[ -s "${prefix}.pdf" ]]; then
        echo "hle-defaults: ${prefix}.pdf"
      else
        echo "hle-defaults: the browser did not write the PDF" >&2
      fi
    fi
  fi
  rm -f "${prefix}.html"
  echo "hle-defaults: ${prefix}.md"
}

main "$@"
