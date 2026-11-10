#!/usr/bin/env bash
# Code coverage of the test suite: runs tests/VitaSharp.Tests (Category!=Slow) with the coverlet collector and turns the
# Cobertura output into an HTML report plus Markdown/text summaries with ReportGenerator.
# Usage: build/coverage.sh [output folder]   (same as build/coverage.ps1)
#   output folder: where the raw results and the report go; default a temp folder (report in <out>/report).
#   env: REPORTGENERATOR (a reportgenerator executable to use; otherwise installed with --tool-path under <out>/tools).
# Only the src projects the test project references are measured (Core, Mobile.Core, Renderer.Vulkan); Desktop and
# Android are not referenced by the tests, so they are not in the report.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-${TMPDIR:-/tmp}/vitasharp-coverage}"
mkdir -p "$out"
out="$(cd "$out" && pwd)"
results="$out/results"
rm -rf "$results"

dotnet test "$root/tests/VitaSharp.Tests" -c Release --filter 'Category!=Slow' -m:2 \
  -p:UseSharedCompilation=false -nr:false --disable-build-servers \
  --collect:'XPlat Code Coverage' --results-directory "$results" \
  -- RunConfiguration.MaxCpuCount=2 \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura' \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include=[VitaSharp.*]*' \
  'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude=[VitaSharp.Tests]*'
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
head -n 20 "$out/report/Summary.txt"
if [ "$test_exit" -ne 0 ]; then echo "tests failed (exit $test_exit); the report covers the tests that ran"; exit "$test_exit"; fi
