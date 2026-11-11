#!/usr/bin/env bash
# Code coverage of the test suite: runs tests/VitaSharp.Tests (Category!=Slow, without the Timing tests that the
# instrumentation slows below their floors) with the coverlet collector and turns the Cobertura output into an HTML
# report plus Markdown/text summaries with ReportGenerator.
# Usage: build/coverage.sh [output folder] [--gate]   (same as build/coverage.ps1)
#   output folder: where the raw results and the report go; default a temp folder (report in <out>/report).
#   --gate: fail (exit 3) when the overall line coverage or an assembly's is below its minimum in build/coverage-gate.txt
#           (the CI coverage job). Raise the minimums when a measurement passes them; never lower them.
#   env: REPORTGENERATOR (a reportgenerator executable to use; otherwise installed with --tool-path under <out>/tools).
# Measured: the src projects the test project references (Core, Mobile.Core, Renderer.Vulkan, Desktop), without the
# code third-party source generators write (System.Text.Json, LibraryImport). Android needs its workload and is not
# built by the tests, so it is not in the report. The renderer's tests need a software Vulkan device
# (SwiftShader from Edge/Chrome, or Mesa lavapipe) and are skipped without one.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
gate=0
positional=()
for a in "$@"; do if [ "$a" = "--gate" ]; then gate=1; else positional+=("$a"); fi; done
out="${positional[0]:-${TMPDIR:-/tmp}/vitasharp-coverage}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
results="$out/results"
rm -rf "$results"

dotnet test "$root/tests/VitaSharp.Tests" -c Release --filter 'Category!=Slow&Category!=Timing' -m:2 \
  -p:UseSharedCompilation=false -nr:false --disable-build-servers --blame-hang-timeout 10m \
  --collect:'XPlat Code Coverage' --results-directory "$results" \
  -- RunConfiguration.MaxCpuCount=2 \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura' \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include=[VitaSharp.*]*' \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude=[VitaSharp.Tests]*' \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.ExcludeByFile=**/System.Text.Json.SourceGeneration/**/*.cs,**/Microsoft.Interop.LibraryImportGenerator/**/*.cs'
test_exit=$?

set -e
rg="${REPORTGENERATOR:-}"
if [ -z "$rg" ]; then
  tools="$out/tools"
  rg="$tools/reportgenerator"
  [ -x "$rg" ] || dotnet tool install dotnet-reportgenerator-globaltool --tool-path "$tools" >/dev/null
fi
reports="$(find "$results" -name coverage.cobertura.xml | paste -sd ';' -)"
[ -n "$reports" ] || { echo "no coverage.cobertura.xml under $results" >&2; exit 1; }
"$rg" "-reports:$reports" "-targetdir:$out/report" '-reporttypes:Html;MarkdownSummary;TextSummary'
summary="$out/report/Summary.txt"
head -n 20 "$summary"
grep -E '^VitaSharp\.[^ ]+ +[0-9.]+%$' "$summary" || true
if [ "$test_exit" -ne 0 ]; then echo "tests failed (exit $test_exit); the report covers the tests that ran"; exit "$test_exit"; fi

if [ "$gate" -eq 1 ]; then
  # measured values: "Line coverage: 60.1%" and the assembly lines "VitaSharp.Core   66.1%"
  measured() {
    if [ "$1" = "Overall" ]; then sed -nE 's/^ *Line coverage: *([0-9.]+)%.*/\1/p' "$summary" | head -n 1
    else sed -nE "s/^$(printf '%s' "$1" | sed 's/\./\\./g') +([0-9.]+)%$/\1/p" "$summary" | head -n 1; fi
  }
  failed=0
  while read -r name min; do
    case "$name" in ''|'#'*) continue ;; esac
    value="$(measured "$name")"
    if [ -z "$value" ]; then echo "coverage gate: $name was not measured"; failed=1; continue; fi
    if awk -v v="$value" -v m="$min" 'BEGIN { exit !(v < m) }'; then echo "coverage gate: $name $value% is below its minimum $min%"; failed=1
    elif awk -v v="$value" -v m="$min" 'BEGIN { exit !(v >= m + 1) }'; then echo "coverage gate: $name $value% (minimum $min%: raise it in build/coverage-gate.txt)"
    else echo "coverage gate: $name $value% (minimum $min%)"; fi
  done < "$root/build/coverage-gate.txt"
  [ "$failed" -eq 0 ] || exit 3
fi
