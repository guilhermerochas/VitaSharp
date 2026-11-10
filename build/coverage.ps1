# Code coverage of the test suite: runs tests/VitaSharp.Tests (Category!=Slow) with the coverlet collector and turns the
# Cobertura output into an HTML report plus Markdown/text summaries with ReportGenerator.
# Usage: build/coverage.ps1 [output folder]   (same as build/coverage.sh)
#   output folder: where the raw results and the report go; default a temp folder (report in <out>\report).
#   env: REPORTGENERATOR (a reportgenerator executable to use; otherwise installed with --tool-path under <out>\tools).
# Only the src projects the test project references are measured (Core, Mobile.Core, Renderer.Vulkan); Desktop and
# Android are not referenced by the tests, so they are not in the report.
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$out = if ($args.Count -gt 0) { $args[0] } else { Join-Path ([IO.Path]::GetTempPath()) 'vitasharp-coverage' }
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path
$results = Join-Path $out 'results'
if (Test-Path $results) { Remove-Item -Recurse -Force $results }

dotnet test (Join-Path $root 'tests/VitaSharp.Tests') -c Release --filter 'Category!=Slow' -m:2 `
    -p:UseSharedCompilation=false -nr:false --disable-build-servers `
    --collect:'XPlat Code Coverage' --results-directory $results `
    -- RunConfiguration.MaxCpuCount=2 `
    'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura' `
    'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Include=[VitaSharp.*]*' `
    'DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Exclude=[VitaSharp.Tests]*'
$testExit = $LASTEXITCODE

$rg = $env:REPORTGENERATOR
if (-not $rg) {
    $tools = Join-Path $out 'tools'
    $rg = Join-Path $tools 'reportgenerator.exe'
    if (-not (Test-Path $rg)) { dotnet tool install dotnet-reportgenerator-globaltool --tool-path $tools | Out-Null }
}
$reports = (Get-ChildItem $results -Recurse -Filter 'coverage.cobertura.xml' | ForEach-Object FullName) -join ';'
if (-not $reports) { throw "no coverage.cobertura.xml under $results" }
& $rg "-reports:$reports" "-targetdir:$(Join-Path $out 'report')" '-reporttypes:Html;MarkdownSummary;TextSummary'
Get-Content (Join-Path $out 'report/Summary.txt') -TotalCount 20
if ($testExit -ne 0) { "tests failed (exit $testExit); the report covers the tests that ran"; exit $testExit }
