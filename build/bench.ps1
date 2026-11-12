# Runs benchmarks of tests/VitaSharp.Benchmarks (BenchmarkDotNet; not part of the solution build or the test suite)
# and keeps the joined GitHub Markdown table as <out>/<name>.md, to compare runs before and after a change.
# Usage: build/bench.ps1 [-Help] [-Filter '*Uniform*'] [-Name label] [-Out folder] [-Dump file.vsgd] [-At9 file.at9]
#                        [-Corpus file] [-Variants 'K=V,K2=V2|K3=V3'] [-Job short|medium|long|default] [-Affinity 15]
#                        [-Compare base.md]
#   (same as build/bench.sh --help --filter --name --out --dump --at9 --corpus --variants --job --affinity --compare)
#   -Filter: BenchmarkDotNet's globs over Type.Method, comma-separated (default '*'; -Filter list prints the
#     benchmarks and exits).
#   -Name: result file name (default bench_<date>_<time>); -Out: results folder (default <temp>/vitasharp-bench).
#   -Dump / -At9 / -Corpus: fixtures with game data (never in git): a GPU dump for GpuWorkerBenchmarks and
#     ShaderTranslationBenchmarks, an .at9 file for Atrac9Benchmarks, a block corpus for CpuTranslatorBenchmarks.
#   -Variants: GpuWorkerBenchmarks runs 'base' and each '|'-separated set of switches (A/B on the same build).
#   -Affinity: CPU mask of the benchmark processes (default 15 = the first four logical CPUs).
#   -Compare: after the run, a table of this run against an earlier result file (ratio new/base per benchmark).
param(
    [string]$Filter = '*',
    [string]$Name = '',
    [string]$Out = '',
    [string]$Dump = '',
    [string]$At9 = '',
    [string]$Corpus = '',
    [string]$Variants = '',
    [string]$Job = 'short',
    [long]$Affinity = 15,
    [string]$Compare = '',
    [switch]$Help
)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Get-Content $PSCommandPath -TotalCount 14 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/VitaSharp.Benchmarks'
if (-not $Out) { $Out = Join-Path ([System.IO.Path]::GetTempPath()) 'vitasharp-bench' }
if (-not $Name) { $Name = 'bench_' + (Get-Date -Format 'yyyyMMdd_HHmmss') }
New-Item -ItemType Directory -Force $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$artifacts = Join-Path $Out "$Name.artifacts"

if ($Dump) { $env:VITASHARP_BENCH_DUMP = (Resolve-Path $Dump).Path }
if ($At9) { $env:VITASHARP_BENCH_AT9 = (Resolve-Path $At9).Path }
if ($Corpus) { $env:VITASHARP_BENCH_CORPUS = (Resolve-Path $Corpus).Path }
if ($Variants) { $env:VITASHARP_BENCH_VARIANTS = $Variants }
# the generated benchmark projects are built by BenchmarkDotNet: no compiler server, no node reuse
$env:UseSharedCompilation = 'false'
$env:MSBUILDDISABLENODEREUSE = '1'

$build = @('build', $project, '-c', 'Release', '-m:2', '-p:UseSharedCompilation=false', '-nr:false',
    '--disable-build-servers', '-v:q', '-nologo')
& dotnet @build
if ($LASTEXITCODE -ne 0) { Write-Error "build failed ($LASTEXITCODE)" }
$run = @('run', '--project', $project, '-c', 'Release', '--no-build', '--')
# BenchmarkDotNet looks for the project from the current folder up and then below the solution folder
Push-Location $project
if ($Filter -eq 'list') { & dotnet @run --list flat; exit $LASTEXITCODE }
$filters = @($Filter -split ',' | Where-Object { $_ })
& dotnet @run --filter @filters --job $Job --affinity $Affinity --artifacts $artifacts
$code = $LASTEXITCODE
Pop-Location

$md = Get-ChildItem (Join-Path $artifacts 'results') -Filter '*-report-github.md' -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $md) { Write-Error "no results in $artifacts (exit $code)" }
$commit = (& git -C $root rev-parse --short HEAD 2>$null)
$header = "<!-- vitasharp bench: filter $Filter, job $Job, commit $commit, $(Get-Date -Format s) -->"
$result = Join-Path $Out "$Name.md"
Set-Content -Path $result -Value $header -Encoding utf8
Get-Content $md.FullName -Encoding utf8 | Add-Content -Path $result -Encoding utf8
Write-Host "results: $result"
if ($Compare) { & dotnet @run compare (Resolve-Path $Compare).Path $result }
exit $code
