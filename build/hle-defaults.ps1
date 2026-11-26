# Lists every HLE function that answers a default value (the [Hle(..., Default = true)] bindings, the "default" rows of
# docs/hle/data/vitasharp-implemented.tsv): library, NID, name, the value it returns and why, grouped by library with
# the counts, as Markdown and PDF (VitaSharp-funcoes-valor-padrao-<date>.md/.pdf).
# -Out: the output folder; default %USERPROFILE%\VitaSharpReports (outside the repository; the reports are not committed).
# -Date: the date in the file names (yyyy-MM-dd); default today. -NoPdf: only the .md.
# The PDF is made by a headless Edge/Chrome ($env:BROWSER picks one); without a browser only .md/.html.
# Run it after each batch of HLE changes; run build/hle-status first when the census changed.
# Linux/macOS: build/hle-defaults.sh (same steps).
param([string]$Out = '', [string]$Date = '', [switch]$NoPdf)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Out) { $Out = Join-Path $env:USERPROFILE 'VitaSharpReports' }
if (-not $Date) { $Date = (Get-Date).ToString('yyyy-MM-dd') }
New-Item -ItemType Directory -Force $Out | Out-Null
$prefix = Join-Path $Out "VitaSharp-funcoes-valor-padrao-$Date"
dotnet run --project (Join-Path $root 'build/HleDefaultsTool') -c Release -v q -p:UseSharedCompilation=false -- $root $prefix "--date=$Date"
if ($LASTEXITCODE -ne 0) { throw 'HleDefaultsTool failed' }
if (-not $NoPdf) {
    $browser = $env:BROWSER
    if (-not $browser) {
        $candidates = @(
            "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
            "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
            "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
            "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe")
        $browser = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    }
    if (-not $browser) {
        Write-Warning "hle-defaults: no Edge/Chrome found: only $prefix.md and .html were written"
    }
    else {
        $url = 'file:///' + ("$prefix.html" -replace '\\', '/')
        & $browser --headless --disable-gpu --no-pdf-header-footer "--print-to-pdf=$prefix.pdf" $url 2>$null | Out-Null
        if ((Test-Path "$prefix.pdf") -and (Get-Item "$prefix.pdf").Length -gt 0) { "hle-defaults: $prefix.pdf" }
        else { Write-Warning 'hle-defaults: the browser did not write the PDF' }
    }
}
Remove-Item "$prefix.html" -ErrorAction SilentlyContinue
"hle-defaults: $prefix.md"
