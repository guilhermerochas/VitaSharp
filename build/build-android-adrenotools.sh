#!/usr/bin/env bash
#
# Builds libadrenotools (github.com/bylaws/libadrenotools, BSD-2-Clause) for the Android app, arm64-v8a only:
# libadrenotools.so and its hook libraries into third_party/android-adrenotools/libs/arm64-v8a/ (not committed).
# src/VitaSharp.Android packages them when they are there; without them the app uses the system Vulkan driver and
# "Driver da GPU" cannot load custom drivers. The sources are fetched at a pinned commit into
# third_party/android-adrenotools/src (git needed once), then built with ndk-build (build/android-adrenotools/jni).
# Needs the NDK (sdkmanager "ndk;<version>"); ANDROID_NDK_HOME picks one, else the newest under the SDK's ndk folder.
# JOBS sets the parallel compiles (default 2).
# Usage: build/build-android-adrenotools.sh   (same as build/build-android-adrenotools.ps1)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly REPO="https://github.com/bylaws/libadrenotools.git"
readonly COMMIT="8fae8ce254dfc1344527e05301e43f37dea2df80"
readonly NS_REPO="https://github.com/bylaws/liblinkernsbypass.git"
readonly NS_COMMIT="aa3975893d83ef1bc84c321ec60c65fbf1287887"

#######################################
# Finds the Android NDK: ANDROID_NDK_HOME, else the newest under the SDK's ndk folder.
# Outputs:
#   The NDK folder to stdout (empty when none is found).
#######################################
find_ndk() {
  local ndk="${ANDROID_NDK_HOME:-}"
  if [[ -z "${ndk}" ]]; then
    local sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
    if [[ -z "${sdk}" ]]; then
      case "$(uname -s)" in
        Darwin) sdk="${HOME}/Library/Android/sdk" ;;
        *) sdk="${HOME}/Android/Sdk" ;;
      esac
    fi
    # shellcheck disable=SC2012  # folder names are NDK versions (no spaces); ls -d keeps the trailing slash
    ndk="$(ls -d "${sdk}"/ndk/*/ 2>/dev/null | sort -V | tail -n 1 || true)"
    ndk="${ndk%/}"
  fi
  printf '%s' "${ndk}"
}

#######################################
# A shallow checkout of one commit (GitHub serves fetches by commit id); nothing when it is already there.
# Arguments:
#   Folder, repository URL, commit id.
#######################################
fetch() {
  local dir="$1" url="$2" commit="$3"
  if [[ "$(git -C "${dir}" rev-parse HEAD 2>/dev/null || true)" == "${commit}" ]]; then
    return 0
  fi
  rm -rf "${dir}"
  mkdir -p "${dir}"
  git -C "${dir}" init -q
  git -C "${dir}" fetch -q --depth 1 "${url}" "${commit}"
  git -C "${dir}" -c advice.detachedHead=false checkout -q FETCH_HEAD
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,10s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local jobs="${JOBS:-2}"
  local ndk
  ndk="$(find_ndk)"
  if [[ -z "${ndk}" || ! -x "${ndk}/ndk-build" ]]; then
    echo 'Android NDK not found (install it with sdkmanager "ndk;<version>" or set ANDROID_NDK_HOME)' >&2
    return 1
  fi
  local out="${ROOT}/third_party/android-adrenotools"
  local src="${out}/src"

  fetch "${src}" "${REPO}" "${COMMIT}"
  fetch "${src}/lib/linkernsbypass" "${NS_REPO}" "${NS_COMMIT}"

  echo "NDK: ${ndk}"
  "${ndk}/ndk-build" -C "${ROOT}/build/android-adrenotools" "NDK_PROJECT_PATH=${ROOT}/build/android-adrenotools" \
    "NDK_OUT=${out}/obj" "NDK_LIBS_OUT=${out}/libs" "ADRENOTOOLS_SRC=${src}" "-j${jobs}"
  cp "${src}/LICENSE" "${out}/LICENSE.libadrenotools"
  cp "${src}/lib/linkernsbypass/LICENSE" "${out}/LICENSE.liblinkernsbypass"
  find "${out}/libs" -name '*.so' -exec ls -l {} \;
}

main "$@"
