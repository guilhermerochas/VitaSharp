# Builds libadrenotools (github.com/bylaws/libadrenotools, BSD-2-Clause) for the Android app, arm64-v8a only:
# libadrenotools.so and its hook libraries into third_party/android-adrenotools/libs/arm64-v8a/ (not committed).
# src/VitaSharp.Android packages them when they are there; without them the app uses the system Vulkan driver and
# "Driver da GPU" cannot load custom drivers. The sources are fetched at a pinned commit into
# third_party/android-adrenotools/src (git needed once), then built with ndk-build (build/android-adrenotools/jni).
# Needs the NDK (sdkmanager "ndk;<version>"); ANDROID_NDK_HOME picks one, else the newest under the SDK's ndk folder.
# -Jobs sets the parallel compiles (default 2).
# Linux/macOS: build/build-android-adrenotools.sh (same steps).
param([int]$Jobs = 2)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$repo = 'https://github.com/bylaws/libadrenotools.git'
$commit = '8fae8ce254dfc1344527e05301e43f37dea2df80'
$nsRepo = 'https://github.com/bylaws/liblinkernsbypass.git'
$nsCommit = 'aa3975893d83ef1bc84c321ec60c65fbf1287887'
$ndk = $env:ANDROID_NDK_HOME
if (-not $ndk) {
    $sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
    $ndk = Get-ChildItem (Join-Path $sdk 'ndk') -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } | Select-Object -Last 1 -ExpandProperty FullName
}
if (-not $ndk -or -not (Test-Path (Join-Path $ndk 'ndk-build.cmd'))) { throw 'Android NDK not found (install it with sdkmanager "ndk;<version>" or set ANDROID_NDK_HOME)' }
$out = Join-Path $root 'third_party\android-adrenotools'
$src = Join-Path $out 'src'

# a shallow checkout of one commit (GitHub serves fetches by commit id)
function Fetch([string]$Dir, [string]$Url, [string]$Commit) {
    if ((Test-Path (Join-Path $Dir '.git')) -and ((git -C $Dir rev-parse HEAD 2>$null) -eq $Commit)) { return }
    if (Test-Path $Dir) { Remove-Item -Recurse -Force $Dir }
    New-Item -ItemType Directory -Force $Dir | Out-Null
    git -C $Dir init -q
    git -C $Dir fetch -q --depth 1 $Url $Commit
    if ($LASTEXITCODE -ne 0) { throw "git fetch $Url $Commit failed" }
    git -C $Dir -c advice.detachedHead=false checkout -q FETCH_HEAD
    if ($LASTEXITCODE -ne 0) { throw "git checkout $Commit failed" }
}
Fetch $src $repo $commit
Fetch (Join-Path $src 'lib\linkernsbypass') $nsRepo $nsCommit

$project = Join-Path $root 'build\android-adrenotools'
# make wants forward slashes in the source paths
$args = @("NDK_PROJECT_PATH=$project", "NDK_OUT=$out\obj", "NDK_LIBS_OUT=$out\libs", "ADRENOTOOLS_SRC=$($src.Replace([char]92, [char]47))", "-j$Jobs")
Write-Output "NDK: $ndk"
$ErrorActionPreference = 'Continue'
& (Join-Path $ndk 'ndk-build.cmd') -C $project @args 2>&1 | ForEach-Object { "$_" }
if ($LASTEXITCODE -ne 0) { throw 'ndk-build failed' }
$ErrorActionPreference = 'Stop'
Copy-Item (Join-Path $src 'LICENSE') (Join-Path $out 'LICENSE.libadrenotools')
Copy-Item (Join-Path $src 'lib\linkernsbypass\LICENSE') (Join-Path $out 'LICENSE.liblinkernsbypass')
Get-ChildItem (Join-Path $out 'libs') -Recurse -Filter *.so | ForEach-Object { '{0,12:N0} {1}' -f $_.Length, $_.FullName }
