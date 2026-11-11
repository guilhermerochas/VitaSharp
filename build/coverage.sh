#!/usr/bin/env bash
#
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

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

#######################################
# Runs the suite with the coverlet collector.
# Arguments:
#   Results folder.
# Returns:
#   The dotnet test exit code.
#######################################
run_tests() {
  local collector='DataCollectionRunSettings.DataCollectors.DataCollector.Configuration'
  dotnet test "${ROOT}/tests/VitaSharp.Tests" -c Release --filter 'Category!=Slow&Category!=Timing' -m:2 \
    -p:UseSharedCompilation=false -nr:false --disable-build-servers --blame-hang-timeout 10m \
    --collect:'XPlat Code Coverage' --results-directory "$1" \
    -- RunConfiguration.MaxCpuCount=2 \
    "${collector}.Format=cobertura" \
    "${collector}.Include=[VitaSharp.*]*" \
    "${collector}.Exclude=[VitaSharp.Tests]*" \
    "${collector}.ExcludeByFile=**/System.Text.Json.SourceGeneration/**/*.cs,**/Microsoft.Interop.LibraryImportGenerator/**/*.cs"
}

#######################################
# A measured value of the summary: "Line coverage: 60.1%" (Overall) or the assembly line "VitaSharp.Core   66.1%".
# Arguments:
#   Summary file, "Overall" or an assembly name.
# Outputs:
#   The percentage to stdout (empty when not measured).
#######################################
measured() {
  local summary="$1" name="$2"
  if [[ "${name}" == "Overall" ]]; then
    sed -nE 's/^ *Line coverage: *([0-9.]+)%.*/\1/p' "${summary}" | head -n 1
  else
    sed -nE "s/^$(printf '%s' "${name}" | sed 's/\./\\./g') +([0-9.]+)%$/\1/p" "${summary}" | head -n 1
  fi
}

#######################################
# Compares the summary with the minimums of build/coverage-gate.txt.
# Arguments:
#   Summary file.
# Returns:
#   0 when every value reaches its minimum, 1 otherwise.
#######################################
check_gate() {
  local summary="$1" failed=0 name min value
  while read -r name min; do
    case "${name}" in '' | '#'*) continue ;; esac
    value="$(measured "${summary}" "${name}")"
    if [[ -z "${value}" ]]; then
      echo "coverage gate: ${name} was not measured"
      failed=1
      continue
    fi
    if awk -v v="${value}" -v m="${min}" 'BEGIN { exit !(v < m) }'; then
      echo "coverage gate: ${name} ${value}% is below its minimum ${min}%"
      failed=1
    elif awk -v v="${value}" -v m="${min}" 'BEGIN { exit !(v >= m + 1) }'; then
      echo "coverage gate: ${name} ${value}% (minimum ${min}%: raise it in build/coverage-gate.txt)"
    else
      echo "coverage gate: ${name} ${value}% (minimum ${min}%)"
    fi
  done <"${ROOT}/build/coverage-gate.txt"
  return "${failed}"
}

main() {
  local gate=0 a
  local -a positional=()
  for a in "$@"; do
    case "${a}" in
      --gate) gate=1 ;;
      -h | --help)
        sed -n '3,14s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
        return 0
        ;;
      *) positional+=("${a}") ;;
    esac
  done
  local out="${positional[0]:-${TMPDIR:-/tmp}/vitasharp-coverage}"
  mkdir -p "${out}"
  out="$(cd "${out}" && pwd)"
  local results="${out}/results"
  rm -rf "${results}"

  local test_exit=0
  run_tests "${results}" || test_exit=$?

  set -e
  local rg="${REPORTGENERATOR:-}"
  if [[ -z "${rg}" ]]; then
    local tools="${out}/tools"
    rg="${tools}/reportgenerator"
    if [[ ! -x "${rg}" ]]; then
      dotnet tool install dotnet-reportgenerator-globaltool --tool-path "${tools}" >/dev/null
    fi
  fi
  local reports
  reports="$(find "${results}" -name coverage.cobertura.xml | paste -sd ';' -)"
  if [[ -z "${reports}" ]]; then
    echo "no coverage.cobertura.xml under ${results}" >&2
    return 1
  fi
  "${rg}" "-reports:${reports}" "-targetdir:${out}/report" '-reporttypes:Html;MarkdownSummary;TextSummary'
  local summary="${out}/report/Summary.txt"
  head -n 20 "${summary}"
  grep -E '^VitaSharp\.[^ ]+ +[0-9.]+%$' "${summary}" || true
  if ((test_exit != 0)); then
    echo "tests failed (exit ${test_exit}); the report covers the tests that ran"
    return "${test_exit}"
  fi

  if ((gate == 1)); then
    check_gate "${summary}" || return 3
  fi
}

main "$@"
