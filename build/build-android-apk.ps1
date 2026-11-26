# Builds the Android app's Release APK for arm64 phones the way the CI does (.github/workflows/ci.yml, job
# android-arm64): -r android-arm64 with the Release default runtime, Native AOT. Builds libshaderc
# (build/build-android-shaderc.ps1 -Abi arm64-v8a) and libadrenotools first when third_party has not got them, then
# checks the APK (build/AndroidApkCheck: Native AOT, lib/arm64-v8a/libshaderc_shared.so, libadrenotools and its hooks,
# arm64-v8a only; and build/check-dev-gating.ps1: no developer tooling). Signed with ANDROID_KEYSTORE when it is set,
# else with the .NET debug keystore (made on the fly when missing): installable with adb, not a store release. Android
# updates an installed app only with an APK of the same key; another key means uninstalling, which deletes the app's
# data (firmware, games, saves, caches), so every build a phone gets must use one fixed keystore. Needs the .NET 10 SDK with the maui-android
# workload, a JDK 17, the Android SDK (platform android-36, in ANDROID_HOME or %LOCALAPPDATA%\Android\Sdk), the NDK
# (ANDROID_NDK_HOME or ANDROID_NDK_ROOT, else the newest under the SDK's ndk folder; r30 is the one used) and git.
# -Out: output folder (default artifacts/android): packages/VitaSharp-<Version>-android-arm64.apk and publish/;
# -Version: the name's version (default dev); -Jobs: parallel native compiles (default 2).
# ANDROID_KEYSTORE: keystore file to sign with; ANDROID_KEYSTORE_PASS: its password (default android);
# ANDROID_KEY_ALIAS: the key (default androiddebugkey); ANDROID_KEY_PASS: the key's password (default the store's).
# Usage: build/build-android-apk.ps1 [-Out DIR] [-Version V] [-Jobs N] [-Check APK] [-Help]   (same as .sh)
param([string]$Out = '', [string]$Version = 'dev', [int]$Jobs = 2, [string]$Check = '', [switch]$Help)
$ErrorActionPreference = 'Stop'
if ($Help) {
    Get-Content $PSCommandPath -TotalCount 15 | ForEach-Object { $_ -replace '^# ?', '' }
    exit 0
}
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$rid = 'android-arm64'
$abi = 'arm64-v8a'

# build/AndroidApkCheck, then no developer tooling (build/check-dev-gating.ps1)
function Test-Apk([string]$Apk) {
    dotnet run --project (Join-Path $root 'build/AndroidApkCheck') -c Release -v q -- $Apk
    if ($LASTEXITCODE -ne 0) {
        throw "APK check failed: $Apk"
    }
    & (Join-Path $root 'build/check-dev-gating.ps1') $Apk
    if ($LASTEXITCODE -ne 0) {
        throw "developer tooling in $Apk"
    }
}

if ($Check) {
    Test-Apk $Check
    exit 0
}

if (-not $Out) {
    $Out = Join-Path $root 'artifacts/android'
}
$ndk = if ($env:ANDROID_NDK_HOME) { $env:ANDROID_NDK_HOME } else { $env:ANDROID_NDK_ROOT }
if (-not $ndk) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } elseif ($env:ANDROID_SDK_ROOT) { $env:ANDROID_SDK_ROOT } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    $ndk = Get-ChildItem (Join-Path $sdk 'ndk') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $ndk -or -not (Test-Path (Join-Path $ndk 'toolchains'))) {
    throw 'Android NDK not found (install it with sdkmanager "ndk;30.0.16248370" or set ANDROID_NDK_HOME)'
}
"NDK: $ndk"
# the native library scripts and the app's Native AOT link use the same NDK
$env:ANDROID_NDK_HOME = $ndk

if (-not (Test-Path (Join-Path $root "third_party/android-shaderc/libs/$abi/libshaderc_shared.so"))) {
    & (Join-Path $root 'build/build-android-shaderc.ps1') -Jobs $Jobs -Abi $abi
}
if (-not (Test-Path (Join-Path $root "third_party/android-adrenotools/libs/$abi/libadrenotools.so"))) {
    & (Join-Path $root 'build/build-android-adrenotools.ps1') -Jobs $Jobs
}

$publish = Join-Path $Out 'publish'
$packages = Join-Path $Out 'packages'
foreach ($dir in @($publish, $packages)) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
}
New-Item -ItemType Directory -Force $packages | Out-Null
$signing = @()
if ($env:ANDROID_KEYSTORE) {
    if (-not (Test-Path $env:ANDROID_KEYSTORE -PathType Leaf)) {
        throw "ANDROID_KEYSTORE: no file $($env:ANDROID_KEYSTORE)"
    }
    # the passwords go through the environment (env:), not the command line
    if (-not $env:ANDROID_KEYSTORE_PASS) {
        $env:ANDROID_KEYSTORE_PASS = 'android'
    }
    if (-not $env:ANDROID_KEY_PASS) {
        $env:ANDROID_KEY_PASS = $env:ANDROID_KEYSTORE_PASS
    }
    $alias = 'androiddebugkey'
    if ($env:ANDROID_KEY_ALIAS) {
        $alias = $env:ANDROID_KEY_ALIAS
    }
    $signing = @('-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$($env:ANDROID_KEYSTORE)",
        "-p:AndroidSigningKeyAlias=$alias", '-p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASS',
        '-p:AndroidSigningKeyPass=env:ANDROID_KEY_PASS')
    Write-Host "Signing with $($env:ANDROID_KEYSTORE)"
}
else {
    Write-Host 'Signing with the .NET debug keystore (ANDROID_KEYSTORE not set)'
}
dotnet publish (Join-Path $root 'src/VitaSharp.Android') -c Release -r $rid -p:VitaSharpAndroidRuntime=nativeaot `
    "-p:AndroidNdkDirectory=$ndk" -p:UseSharedCompilation=false -m:2 -nologo -o $publish @signing
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed'
}

$apk = Get-ChildItem $publish -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $apk) {
    throw "no *-Signed.apk in $publish"
}
$package = Join-Path $packages "VitaSharp-$Version-$rid.apk"
Copy-Item $apk.FullName $package
Test-Apk $package
Get-Item $package | ForEach-Object { '{0,12:N0} {1}' -f $_.Length, $_.FullName }
