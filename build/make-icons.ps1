# Regenerates the application icons (src\VitaSharp.Desktop\Assets: icon-*.png, VitaSharp.ico, VitaSharp.icns; the Android
# adaptive launcher icon in src\VitaSharp.Android\Resources) from docs\assets\logo.svg. Run it after changing the logo and
# commit the result; normal builds use the committed icons.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
dotnet run --project (Join-Path $root 'build\IconGen\IconGen.csproj') -c Release -- (Join-Path $root 'docs\assets\logo.svg') (Join-Path $root 'src\VitaSharp.Desktop\Assets') (Join-Path $root 'src\VitaSharp.Android\Resources')
if ($LASTEXITCODE -ne 0) { throw 'IconGen failed' }
