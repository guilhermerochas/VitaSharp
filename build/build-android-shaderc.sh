#!/usr/bin/env bash
#
# Builds libshaderc_shared.so for the Android app (arm64-v8a and x86_64) from the shaderc sources in the Android NDK,
# into third_party/android-shaderc/libs/<abi>/ (not committed). src/VitaSharp.Android packages whatever is there.
# Needs the NDK (sdkmanager "ndk;<version>"); ANDROID_NDK_HOME picks one, else the newest under the SDK's ndk folder.
# The compile is long (glslang + SPIRV-Tools, ~460 files per ABI): JOBS sets the parallel compiles (default 2).
# Usage: build/build-android-shaderc.sh [abi]   (same as build/build-android-shaderc.ps1)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT

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

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,7s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  local jobs="${JOBS:-2}"
  local abi="${1:-}"
  local ndk
  ndk="$(find_ndk)"
  if [[ -z "${ndk}" || ! -x "${ndk}/ndk-build" ]]; then
    echo 'Android NDK not found (install it with sdkmanager "ndk;<version>" or set ANDROID_NDK_HOME)' >&2
    return 1
  fi
  local out="${ROOT}/third_party/android-shaderc"
  local -a args=("NDK_PROJECT_PATH=${ROOT}/build/android-shaderc" "NDK_OUT=${out}/obj" "NDK_LIBS_OUT=${out}/libs"
    "-j${jobs}")
  if [[ -n "${abi}" ]]; then
    args+=("APP_ABI=${abi}")
  fi
  echo "NDK: ${ndk}"
  "${ndk}/ndk-build" -C "${ROOT}/build/android-shaderc" "${args[@]}"
  find "${out}/libs" -name libshaderc_shared.so -exec ls -l {} \;
}

main "$@"
