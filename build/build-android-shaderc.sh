#!/usr/bin/env bash
# Builds libshaderc_shared.so for the Android app (arm64-v8a and x86_64) from the shaderc sources in the Android NDK,
# into third_party/android-shaderc/libs/<abi>/ (not committed). src/VitaSharp.Android packages whatever is there.
# Needs the NDK (sdkmanager "ndk;<version>"); ANDROID_NDK_HOME picks one, else the newest under the SDK's ndk folder.
# The compile is long (glslang + SPIRV-Tools, ~460 files per ABI): JOBS sets the parallel compiles (default 2).
# Usage: build/build-android-shaderc.sh [abi]   (same as build/build-android-shaderc.ps1)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
jobs="${JOBS:-2}"
abi="${1:-}"
ndk="${ANDROID_NDK_HOME:-}"
if [ -z "$ndk" ]; then
  sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
  if [ -z "$sdk" ]; then
    case "$(uname -s)" in
      Darwin) sdk="$HOME/Library/Android/sdk" ;;
      *) sdk="$HOME/Android/Sdk" ;;
    esac
  fi
  ndk="$(ls -d "$sdk"/ndk/*/ 2>/dev/null | sort -V | tail -n 1 || true)"
  ndk="${ndk%/}"
fi
if [ -z "$ndk" ] || [ ! -x "$ndk/ndk-build" ]; then
  echo 'Android NDK not found (install it with sdkmanager "ndk;<version>" or set ANDROID_NDK_HOME)' >&2
  exit 1
fi
out="$root/third_party/android-shaderc"
args=("NDK_PROJECT_PATH=$root/build/android-shaderc" "NDK_OUT=$out/obj" "NDK_LIBS_OUT=$out/libs" "-j$jobs")
[ -n "$abi" ] && args+=("APP_ABI=$abi")
echo "NDK: $ndk"
"$ndk/ndk-build" -C "$root/build/android-shaderc" "${args[@]}"
find "$out/libs" -name libshaderc_shared.so -exec ls -l {} \;
