#!/usr/bin/env bash
#
# Builds the Android app's Release APK for arm64 phones the way the CI does (.github/workflows/ci.yml, job
# android-arm64): -r android-arm64 with the Release default runtime, Native AOT. Builds libshaderc
# (build/build-android-shaderc.sh arm64-v8a) and libadrenotools first when third_party has not got them, then checks
# the APK (build/AndroidApkCheck: Native AOT, lib/arm64-v8a/libshaderc_shared.so, libadrenotools and its hooks,
# arm64-v8a only; and build/check-dev-gating.sh: no developer tooling). Signed with ANDROID_KEYSTORE when it is set,
# else with the .NET debug keystore (made on the fly when missing): installable with adb, not a store release. Android
# updates an installed app only with an APK of the same key; another key means uninstalling, which deletes the app's
# data (firmware, games, saves, caches), so every build a phone gets must use one fixed keystore. Needs the .NET 10 SDK with the maui-android
# workload, a JDK 17, the Android SDK (platform android-36, in ANDROID_HOME), the NDK (ANDROID_NDK_HOME or
# ANDROID_NDK_ROOT, else the newest under the SDK's ndk folder; r30 is the one used) and git.
# OUT: output folder (default artifacts/android): packages/VitaSharp-<VERSION>-android-arm64.apk and publish/;
# VERSION: the name's version (default dev); JOBS: parallel native compiles (default 2).
# ANDROID_KEYSTORE: keystore file to sign with; ANDROID_KEYSTORE_PASS: its password (default android);
# ANDROID_KEY_ALIAS: the key (default androiddebugkey); ANDROID_KEY_PASS: the key's password (default the store's).
# Usage: build/build-android-apk.sh [--check APK]   (same as build/build-android-apk.ps1; --check only checks an APK)

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly RID="android-arm64"
readonly ABI="arm64-v8a"

#######################################
# Finds the Android NDK: ANDROID_NDK_HOME, ANDROID_NDK_ROOT, else the newest under the SDK's ndk folder.
# Outputs:
#   The NDK folder to stdout (empty when none is found).
#######################################
find_ndk() {
  local ndk="${ANDROID_NDK_HOME:-${ANDROID_NDK_ROOT:-}}"
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
# Checks an APK: build/AndroidApkCheck, then no developer tooling (build/check-dev-gating.sh).
# Arguments:
#   APK path.
# Returns:
#   0 when it passes.
#######################################
check_apk() {
  local apk="$1"
  dotnet run --project "${ROOT}/build/AndroidApkCheck" -c Release -v q -- "${apk}"
  bash "${ROOT}/build/check-dev-gating.sh" "${apk}"
}

main() {
  if [[ "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
    sed -n '3,13s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
    return 0
  fi
  if [[ "${1:-}" == "--check" ]]; then
    check_apk "${2:?usage: build-android-apk.sh --check APK}"
    return
  fi

  local out="${OUT:-${ROOT}/artifacts/android}"
  local version="${VERSION:-dev}"
  local jobs="${JOBS:-2}"
  # MSBuild reads environment variables as properties: VERSION would become $(Version) (ci-<n> is no valid package
  # version, restore fails)
  unset VERSION
  local ndk
  ndk="$(find_ndk)"
  if [[ -z "${ndk}" || ! -d "${ndk}/toolchains" ]]; then
    echo 'Android NDK not found (install it with sdkmanager "ndk;30.0.16248370" or set ANDROID_NDK_HOME)' >&2
    return 1
  fi
  echo "NDK: ${ndk}"
  # the native library scripts and the app's Native AOT link use the same NDK
  export ANDROID_NDK_HOME="${ndk}"

  if [[ ! -f "${ROOT}/third_party/android-shaderc/libs/${ABI}/libshaderc_shared.so" ]]; then
    JOBS="${jobs}" bash "${ROOT}/build/build-android-shaderc.sh" "${ABI}"
  fi
  if [[ ! -f "${ROOT}/third_party/android-adrenotools/libs/${ABI}/libadrenotools.so" ]]; then
    JOBS="${jobs}" bash "${ROOT}/build/build-android-adrenotools.sh"
  fi

  local -a signing=()
  if [[ -n "${ANDROID_KEYSTORE:-}" ]]; then
    if [[ ! -f "${ANDROID_KEYSTORE}" ]]; then
      echo "ANDROID_KEYSTORE: no file ${ANDROID_KEYSTORE}" >&2
      return 1
    fi
    # the passwords go through the environment (env:), not the command line
    export ANDROID_KEYSTORE_PASS="${ANDROID_KEYSTORE_PASS:-android}"
    export ANDROID_KEY_PASS="${ANDROID_KEY_PASS:-${ANDROID_KEYSTORE_PASS}}"
    signing=(-p:AndroidKeyStore=true "-p:AndroidSigningKeyStore=${ANDROID_KEYSTORE}"
      "-p:AndroidSigningKeyAlias=${ANDROID_KEY_ALIAS:-androiddebugkey}"
      -p:AndroidSigningStorePass=env:ANDROID_KEYSTORE_PASS -p:AndroidSigningKeyPass=env:ANDROID_KEY_PASS)
    echo "Signing with ${ANDROID_KEYSTORE}"
  else
    echo "Signing with the .NET debug keystore (ANDROID_KEYSTORE not set)"
  fi

  rm -rf "${out:?}/publish" "${out:?}/packages"
  mkdir -p "${out}/packages"
  dotnet publish "${ROOT}/src/VitaSharp.Android" -c Release -r "${RID}" -p:VitaSharpAndroidRuntime=nativeaot \
    "-p:AndroidNdkDirectory=${ndk}" -p:UseSharedCompilation=false -m:2 -nologo -o "${out}/publish" \
    ${signing[@]+"${signing[@]}"}

  local apk
  apk="$(find "${out}/publish" -maxdepth 1 -name '*-Signed.apk' | head -n 1)"
  if [[ -z "${apk}" ]]; then
    echo "no *-Signed.apk in ${out}/publish" >&2
    return 1
  fi
  local package="${out}/packages/VitaSharp-${version}-${RID}.apk"
  cp "${apk}" "${package}"
  check_apk "${package}"
  ls -l "${package}"
}

main "$@"
