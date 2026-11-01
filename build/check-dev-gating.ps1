# Checks that a packaged build has none of the developer tooling (-p:VitaSharpDeveloper=true): searches an APK (unpacked)
# or a desktop publish folder for the tooling's type names (AOT images keep them as symbols). -ExpectDev inverts the
# check (a developer APK must have them). Usage: build/check-dev-gating.ps1 APK|FOLDER [-ExpectDev]   (same as .sh)
param([Parameter(Mandatory = $true)][string]$Target, [switch]$ExpectDev)
$ErrorActionPreference = 'Stop'
$pattern = 'GpuBenchState|GpuBenchSpec|BenchActivity|DevSceneBegin|DevTrace'
$dir = $Target
$tmp = $null
if (Test-Path $Target -PathType Leaf) {
    $tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("devgate-" + [guid]::NewGuid())
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path $Target), $tmp)
    $dir = $tmp
}
$hits = @(Get-ChildItem $dir -Recurse -File | Where-Object {
    [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($_.FullName)) -match $pattern })
if ($tmp) { Remove-Item -Recurse -Force $tmp }
if ($ExpectDev) {
    if ($hits.Count -gt 0) { Write-Host "developer tooling present (expected): $($hits.Count) files"; exit 0 }
    Write-Error "developer tooling NOT found in $Target"
}
if ($hits.Count -eq 0) { Write-Host "no developer tooling in $Target"; exit 0 }
$hits | ForEach-Object { Write-Host "developer tooling found: $($_.FullName)" }
exit 1
