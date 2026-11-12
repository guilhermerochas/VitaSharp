#!/usr/bin/env bash
# Runs benchmarks of tests/VitaSharp.Benchmarks (BenchmarkDotNet; not part of the solution build or the test suite)
# and keeps the joined GitHub Markdown table as <out>/<name>.md, to compare runs before and after a change.
# Usage: build/bench.sh [--help] [--filter '*Uniform*'] [--name label] [--out folder] [--dump file.vsgd]
#                       [--at9 file.at9] [--corpus file] [--variants 'K=V,K2=V2|K3=V3']
#                       [--job short|medium|long|default] [--affinity 15] [--compare base.md]
#   (same as build/bench.ps1 -Help -Filter -Name -Out -Dump -At9 -Corpus -Variants -Job -Affinity
#   -Compare)
#   --filter: BenchmarkDotNet's globs over Type.Method, comma-separated (default '*'; --filter list
#     prints the benchmarks).
#   --name: result file name (default bench_<date>_<time>); --out: results folder
#     (default ${TMPDIR:-/tmp}/vitasharp-bench).
#   --dump / --at9 / --corpus: fixtures with game data (never in git): a GPU dump for GpuWorkerBenchmarks
#     and ShaderTranslationBenchmarks, an .at9 file for Atrac9Benchmarks, a block corpus for
#     CpuTranslatorBenchmarks.
#   --variants: GpuWorkerBenchmarks runs 'base' and each '|'-separated set of switches (A/B on the
#     same build).
#   --affinity: CPU mask of the benchmark processes (default 15 = the first four logical CPUs).
#   --compare: after the run, a table of this run against an earlier result file (ratio new/base).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly PROJECT="${ROOT}/tests/VitaSharp.Benchmarks"

#######################################
# Prints the usage (the header comment of this script).
# Outputs:
#   The usage text to stdout.
#######################################
usage() {
  sed -n '2,/^set -euo pipefail$/{/^#/s/^# \{0,1\}//p;}' "${BASH_SOURCE[0]}"
}

#######################################
# Prints an error and exits.
# Arguments:
#   Message.
#######################################
die() {
  echo "bench.sh: $*" >&2
  exit 1
}

#######################################
# An absolute path for an existing file.
# Arguments:
#   Path.
# Outputs:
#   The absolute path.
#######################################
abs_file() {
  [[ -f "$1" ]] || die "not a file: $1"
  echo "$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
}

main() {
  local filter='*' name='' out='' dump='' at9='' corpus='' variants='' job='short' affinity=15 compare=''
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --filter) filter="$2"; shift 2 ;;
      --name) name="$2"; shift 2 ;;
      --out) out="$2"; shift 2 ;;
      --dump) dump="$2"; shift 2 ;;
      --at9) at9="$2"; shift 2 ;;
      --corpus) corpus="$2"; shift 2 ;;
      --variants) variants="$2"; shift 2 ;;
      --job) job="$2"; shift 2 ;;
      --affinity) affinity="$2"; shift 2 ;;
      --compare) compare="$2"; shift 2 ;;
      -h | --help)
        usage
        return 0
        ;;
      *) die "unknown argument: $1" ;;
    esac
  done
  [[ -n "${out}" ]] || out="${TMPDIR:-/tmp}/vitasharp-bench"
  [[ -n "${name}" ]] || name="bench_$(date +%Y%m%d_%H%M%S)"
  mkdir -p "${out}"
  out="$(cd "${out}" && pwd)"
  local artifacts="${out}/${name}.artifacts"

  if [[ -n "${dump}" ]]; then export VITASHARP_BENCH_DUMP; VITASHARP_BENCH_DUMP="$(abs_file "${dump}")"; fi
  if [[ -n "${at9}" ]]; then export VITASHARP_BENCH_AT9; VITASHARP_BENCH_AT9="$(abs_file "${at9}")"; fi
  if [[ -n "${corpus}" ]]; then export VITASHARP_BENCH_CORPUS; VITASHARP_BENCH_CORPUS="$(abs_file "${corpus}")"; fi
  if [[ -n "${compare}" ]]; then compare="$(abs_file "${compare}")"; fi
  if [[ -n "${variants}" ]]; then export VITASHARP_BENCH_VARIANTS="${variants}"; fi
  # the generated benchmark projects are built by BenchmarkDotNet: no compiler server, no node reuse
  export UseSharedCompilation=false
  export MSBUILDDISABLENODEREUSE=1

  dotnet build "${PROJECT}" -c Release -m:2 -p:UseSharedCompilation=false -nr:false \
    --disable-build-servers -v:q -nologo || die "build failed"
  local -a run=(run --project "${PROJECT}" -c Release --no-build --)
  # BenchmarkDotNet looks for the project from the current folder up and then below the solution folder
  cd "${PROJECT}"
  if [[ "${filter}" == "list" ]]; then
    dotnet "${run[@]}" --list flat
    return
  fi
  local code=0
  local -a filters
  IFS=',' read -r -a filters <<< "${filter}"
  dotnet "${run[@]}" --filter "${filters[@]}" --job "${job}" --affinity "${affinity}" \
    --artifacts "${artifacts}" || code=$?

  local md='' f
  for f in "${artifacts}/results/"*-report-github.md; do
    [[ -f "${f}" ]] || continue
    if [[ -z "${md}" || "${f}" -nt "${md}" ]]; then md="${f}"; fi
  done
  [[ -n "${md}" ]] || die "no results in ${artifacts} (exit ${code})"
  local commit
  commit="$(git -C "${ROOT}" rev-parse --short HEAD 2>/dev/null || echo unknown)"
  local result="${out}/${name}.md"
  {
    echo "<!-- vitasharp bench: filter ${filter}, job ${job}, commit ${commit}, $(date +%Y-%m-%dT%H:%M:%S) -->"
    cat "${md}"
  } > "${result}"
  echo "results: ${result}"
  if [[ -n "${compare}" ]]; then
    dotnet "${run[@]}" compare "${compare}" "${result}"
  fi
  return "${code}"
}

main "$@"
