#!/usr/bin/env bash
# Builds libadrenotools (github.com/bylaws/libadrenotools, BSD-2-Clause) for the Android app, arm64-v8a only:
# libadrenotools.so and its hook libraries into third_party/android-adrenotools/libs/arm64-v8a/ (not committed).
# src/VitaSharp.Android packages them when they are there; without them the app uses the system Vulkan driver and
# "Driver da GPU" cannot load custom drivers. The sources are fetched at a pinned commit into
# third_party/android-adrenotools/src (git needed once), then built with ndk-build (build/android-adrenotools/jni).
# Needs the NDK (sdkmanager "ndk;<version>"); ANDROID_NDK_HOME picks one, else the newest under the SDK's ndk folder.
# JOBS sets the parallel compiles (default 2).
# Usage: build/build-android-adrenotools.sh   (same as build/build-android-adrenotools.ps1)
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
jobs="${JOBS:-2}"
repo="https://github.com/bylaws/libadrenotools.git"
commit="8fae8ce254dfc1344527e05301e43f37dea2df80"
nsrepo="https://github.com/bylaws/liblinkernsbypass.git"
nscommit="aa3975893d83ef1bc84c321ec60c65fbf1287887"
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
out="$root/third_party/android-adrenotools"
src="$out/src"

# a shallow checkout of one commit (GitHub serves fetches by commit id)
fetch() { # dir url commit
  if [ "$(git -C "$1" rev-parse HEAD 2>/dev/null || true)" = "$3" ]; then return; fi
  rm -rf "$1"
  mkdir -p "$1"
  git -C "$1" init -q
  git -C "$1" fetch -q --depth 1 "$2" "$3"
  git -C "$1" -c advice.detachedHead=false checkout -q FETCH_HEAD
}
fetch "$src" "$repo" "$commit"
fetch "$src/lib/linkernsbypass" "$nsrepo" "$nscommit"

echo "NDK: $ndk"
"$ndk/ndk-build" -C "$root/build/android-adrenotools" "NDK_PROJECT_PATH=$root/build/android-adrenotools" \
  "NDK_OUT=$out/obj" "NDK_LIBS_OUT=$out/libs" "ADRENOTOOLS_SRC=$src" "-j$jobs"
cp "$src/LICENSE" "$out/LICENSE.libadrenotools"
cp "$src/lib/linkernsbypass/LICENSE" "$out/LICENSE.liblinkernsbypass"
find "$out/libs" -name '*.so' -exec ls -l {} \;
