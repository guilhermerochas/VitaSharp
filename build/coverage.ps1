# Code coverage of the test suite: runs tests/VitaSharp.Tests (Category!=Slow, without the Timing tests that the
# instrumentation slows below their floors) with the coverlet collector and turns the Cobertura output into an HTML
# report plus Markdown/text summaries with ReportGenerator.
# Usage: build/coverage.ps1 [output folder] [--gate]   (same as build/coverage.sh)
#   output folder: where the raw results and the report go; default a temp folder (report in <out>\report).
#   --gate: fail (exit 3) when the overall line coverage or an assembly's is below its minimum in build/coverage-gate.txt
#           (the CI coverage job). Raise the minimums when a measurement passes them; never lower them.
#   env: REPORTGENERATOR (a reportgenerator executable to use; otherwise installed with --tool-path under <out>\tools).
# Measured: the src projects the test project references (Core, Mobile.Core, Renderer.Vulkan). Desktop and Android
# are not referenced by the tests, so they are not in the report. The renderer's tests need a software Vulkan device
# (SwiftShader from Edge/Chrome, or Mesa lavapipe) and are skipped without one.
# 'Continue': Windows PowerShell 5.1 turns a native command's stderr lines into errors when the output is redirected
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$gate = $args -contains '--gate'
$positional = @($args | Where-Object { $_ -ne '--gate' })
$out = if ($positional.Count -gt 0) { $positional[0] } else { Join-Path ([IO.Path]::GetTempPath()) 'vitasharp-coverage' }
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path
$results = Join-Path $out 'results'
if (Test-Path $results) { Remove-Item -Recurse -Force $results }

dotnet test (Join-Path $root 'tests/VitaSharp.Tests') -c Release --filter 'Category!=Slow&Category!=Timing' -m:2 `
    -p:UseSharedCompilation=false -nr:false --disable-build-servers --blame-hang-timeout 10m `
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
if (-not $reports) { Write-Error "no coverage.cobertura.xml under $results" }
& $rg "-reports:$reports" "-targetdir:$(Join-Path $out 'report')" '-reporttypes:Html;MarkdownSummary;TextSummary'
$summary = Join-Path $out 'report/Summary.txt'
Get-Content $summary -TotalCount 20
Get-Content $summary | Where-Object { $_ -match '^VitaSharp\.\S+\s+[0-9.]+%$' }
if ($testExit -ne 0) { "tests failed (exit $testExit); the report covers the tests that ran"; exit $testExit }

if ($gate) {
    # measured values: "Line coverage: 60.1%" and the assembly lines "VitaSharp.Core   66.1%"
    $measured = @{}
    foreach ($line in Get-Content $summary) {
        if ($line -match '^\s*Line coverage:\s*([0-9.]+)%') { $measured['Overall'] = [double]$Matches[1] }
        elseif ($line -match '^(VitaSharp\.\S+)\s+([0-9.]+)%$') { $measured[$Matches[1]] = [double]$Matches[2] }
    }
    $failed = 0
    foreach ($line in Get-Content (Join-Path $root 'build/coverage-gate.txt')) {
        if ($line -match '^\s*(#|$)') { continue }
        $name, $min = -split $line
        $min = [double]::Parse($min, [Globalization.CultureInfo]::InvariantCulture)
        if (-not $measured.ContainsKey($name)) { "coverage gate: $name was not measured"; $failed = 1; continue }
        $value = $measured[$name]
        if ($value -lt $min) { "coverage gate: $name $value% is below its minimum $min%"; $failed = 1 }
        elseif ($value -ge $min + 1) { "coverage gate: $name $value% (minimum $min%: raise it in build/coverage-gate.txt)" }
        else { "coverage gate: $name $value% (minimum $min%)" }
    }
    if ($failed) { exit 3 }
}
