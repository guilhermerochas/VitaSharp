# Builds libshaderc_shared.so for the Android app (arm64-v8a and x86_64) from the shaderc sources in the Android NDK,
# into third_party/android-shaderc/libs/<abi>/ (not committed). src/VitaSharp.Android packages whatever is there.
# Needs the NDK (sdkmanager "ndk;<version>"); ANDROID_NDK_HOME picks one, else the newest under the SDK's ndk folder.
# The compile is long (glslang + SPIRV-Tools, ~460 files per ABI): -Jobs sets the parallel compiles (default 2).
# Linux/macOS: build/build-android-shaderc.sh (same steps).
param([int]$Jobs = 2, [string]$Abi = '')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$ndk = $env:ANDROID_NDK_HOME
if (-not $ndk) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    $ndk = Get-ChildItem (Join-Path $sdk 'ndk') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $ndk -or -not (Test-Path (Join-Path $ndk 'ndk-build.cmd'))) { throw 'Android NDK not found (install it with sdkmanager "ndk;<version>" or set ANDROID_NDK_HOME)' }
$out = Join-Path $root 'third_party\android-shaderc'
$project = Join-Path $root 'build\android-shaderc'
$args = @("NDK_PROJECT_PATH=$project", "NDK_OUT=$out\obj", "NDK_LIBS_OUT=$out\libs", "-j$Jobs")
if ($Abi) { $args += "APP_ABI=$Abi" }
Write-Output "NDK: $ndk"
# make prints warnings on stderr (shaderc's Android.mk overrides a header recipe): not errors, only the exit code counts
$ErrorActionPreference = 'Continue'
& (Join-Path $ndk 'ndk-build.cmd') -C $project @args 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw 'ndk-build failed' }
Get-ChildItem (Join-Path $out 'libs') -Recurse -Filter libshaderc_shared.so | ForEach-Object { '{0,12:N0} {1}' -f $_.Length, $_.FullName }
