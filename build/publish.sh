#!/usr/bin/env bash
# Self-contained release builds of VitaSharp (launcher + emulator in one executable). No .NET install is needed to run them.
#   build/publish.sh                        all platforms, single-file ReadyToRun
#   build/publish.sh linux-x64 osx-arm64    only these
#   build/publish.sh --aot linux-x64        Native AOT (see below)
#   build/publish.sh --package linux-x64    also build the packages (see below)
# Output: artifacts/publish/<rid>/ (override with OUT=<folder>). ReadyToRun builds can be published for any platform
# from any host; FFmpeg is downloaded for the target platform (macOS uses the system FFmpeg: brew install ffmpeg).
#
# --aot compiles the whole app to native code ahead of time (faster start, less memory, no JIT at run time). Native
# AOT cannot cross-compile between operating systems, so it is used only where the host can build it (Linux: the
# host's own architecture, needs clang and zlib1g-dev; macOS: osx-x64 and osx-arm64, needs the Xcode command-line
# tools); the other platforms fall back to ReadyToRun with a warning. Debug symbols go to <out>/<rid>-symbols/.
#
# --package writes <out>/packages/ (VERSION=<v> names them, default dev):
#   win-*    VitaSharp-<v>-<rid>.zip          VitaSharp/ with the executable, native DLLs, ffmpeg/, LICENSE, README.md
#                                             (the Inno Setup installer is made by build/publish.ps1 -Package on Windows)
#   linux-*  VitaSharp-<v>-<rid>.tar.gz       VitaSharp/ as above plus vitasharp.desktop, icons/ and
#                                             install-desktop-entry.sh (adds it to the application menu)
#            VitaSharp-<v>-<rid>.AppImage     when appimagetool is on PATH or APPIMAGETOOL=<path> is set
#   osx-*    VitaSharp-<v>-<rid>.app.zip      VitaSharp.app (Info.plist, VitaSharp.icns, executable + dylibs in
#                                             Contents/MacOS), unsigned; on macOS also VitaSharp-<v>-<rid>.dmg
# A release VERSION (1.2.3 or 1.2.3-suffix, as cut by .github/workflows/release.yml) also becomes the app's version
# (-p:Version, shown as "VitaSharp 1.2.3+<commit>" in the log); dev and ci-<n> builds keep the default 1.0.0.
# --skip-publish packages an existing publish output without rebuilding.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
readonly ROOT
readonly ASSETS="${ROOT}/src/VitaSharp.Desktop/Assets"
readonly TMPL="${ROOT}/build/package"
readonly PROJECT="${ROOT}/src/VitaSharp.Desktop/VitaSharp.Desktop.csproj"
ICON_SIZES=(16 24 32 48 64 128 256 512)
readonly ICON_SIZES
DEFAULT_RIDS=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
readonly DEFAULT_RIDS
# package names; set by main from VERSION
PUBLISH_VERSION=dev

#######################################
# Can this host build Native AOT for a runtime identifier?
# Arguments:
#   Runtime identifier.
#######################################
aot_host() {
  local arch
  case "$(uname -m)" in
    x86_64 | amd64) arch=x64 ;;
    aarch64 | arm64) arch=arm64 ;;
    *) arch=unknown ;;
  esac
  case "$(uname -s)" in
    Linux) [[ "$1" == "linux-${arch}" ]] ;;
    Darwin) [[ "$1" == osx-* ]] ;;
    MINGW* | MSYS* | CYGWIN*) [[ "$1" == win-* ]] ;;
    *) false ;;
  esac
}

#######################################
# Size of a file or folder, human readable.
#######################################
size() {
  du -h "$1" | cut -f1
}

#######################################
# Regular permissions on a copied publish folder (a copy from a Windows drive marks every file executable), then +x on
# the executable.
# Arguments:
#   Folder, executable path.
#######################################
fix_modes() {
  find "$1" -type d -exec chmod 755 {} +
  find "$1" -type f -exec chmod 644 {} +
  chmod 755 "$2"
}

#######################################
# Zips a folder of the current directory, keeping Unix permissions and symlinks.
# Arguments:
#   Archive, folder in the current directory.
#######################################
make_zip() {
  rm -f "$1"
  if [[ "$(uname -s)" == Darwin ]]; then
    ditto -c -k --sequesterRsrc --keepParent "$2" "$1"
  elif command -v zip >/dev/null; then
    zip -qry "$1" "$2"
  elif command -v powershell.exe >/dev/null; then
    powershell.exe -NoProfile -Command \
      "Compress-Archive -Path '$(cygpath -w "${PWD}/$2")' -DestinationPath '$(cygpath -w "$1")'"
  else
    echo "error: no zip tool (install zip)" >&2
    return 1
  fi
}

#######################################
# Windows package: VitaSharp-<v>-<rid>.zip.
# Arguments:
#   Runtime identifier, publish folder, packages folder.
#######################################
package_windows() {
  local rid="$1" src="$2" pkgs="$3"
  local stage="${pkgs}/stage/${rid}"
  rm -rf "${stage}"
  mkdir -p "${stage}/VitaSharp"
  cp -R "${src}/." "${stage}/VitaSharp/"
  cp "${ROOT}/LICENSE" "${ROOT}/README.md" "${stage}/VitaSharp/"
  (cd "${stage}" && make_zip "${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.zip" VitaSharp)
  echo "   ${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.zip ($(size "${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.zip"))"
  rm -rf "${stage}"
}

#######################################
# AppImage: AppRun + desktop file + icon at the AppDir root, the app in usr/lib/vitasharp.
# Arguments:
#   Runtime identifier, publish folder, packages folder, stage folder, appimagetool.
#######################################
package_appimage() {
  local rid="$1" src="$2" pkgs="$3" stage="$4" tool="$5"
  local appdir="${stage}/VitaSharp.AppDir" arch s
  case "${rid}" in
    linux-x64) arch=x86_64 ;;
    linux-arm64) arch=aarch64 ;;
    *) arch=unknown ;;
  esac
  mkdir -p "${appdir}/usr/lib/vitasharp" "${appdir}/usr/share/applications"
  cp -R "${src}/." "${appdir}/usr/lib/vitasharp/"
  cp "${ROOT}/LICENSE" "${appdir}/usr/lib/vitasharp/"
  cp "${TMPL}/AppRun" "${appdir}/AppRun"
  cp "${TMPL}/vitasharp.desktop" "${appdir}/vitasharp.desktop"
  cp "${TMPL}/vitasharp.desktop" "${appdir}/usr/share/applications/vitasharp.desktop"
  for s in "${ICON_SIZES[@]}"; do
    mkdir -p "${appdir}/usr/share/icons/hicolor/${s}x${s}/apps"
    cp "${ASSETS}/icon-${s}.png" "${appdir}/usr/share/icons/hicolor/${s}x${s}/apps/vitasharp.png"
  done
  cp "${ASSETS}/icon-256.png" "${appdir}/vitasharp.png"
  ln -sf vitasharp.png "${appdir}/.DirIcon"
  fix_modes "${appdir}" "${appdir}/usr/lib/vitasharp/VitaSharp.Desktop"
  chmod 755 "${appdir}/AppRun"
  local img="${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.AppImage"
  # APPIMAGE_EXTRACT_AND_RUN: appimagetool is itself an AppImage, this runs it without FUSE (containers, WSL, CI)
  local log
  if ! log="$(APPIMAGE_EXTRACT_AND_RUN=1 ARCH="${arch}" "${tool}" --no-appstream "${appdir}" "${img}" 2>&1)"; then
    echo "${log}" >&2
    echo "error: appimagetool failed (it needs the 'file' command)" >&2
    return 1
  fi
  echo "   ${img} ($(size "${img}"))"
}

#######################################
# Linux packages: VitaSharp-<v>-<rid>.tar.gz and, with appimagetool, the AppImage.
# Arguments:
#   Runtime identifier, publish folder, packages folder.
#######################################
package_linux() {
  local rid="$1" src="$2" pkgs="$3"
  local stage="${pkgs}/stage/${rid}" s
  rm -rf "${stage}"
  mkdir -p "${stage}/VitaSharp/icons"
  cp -R "${src}/." "${stage}/VitaSharp/"
  cp "${ROOT}/LICENSE" "${ROOT}/README.md" "${TMPL}/vitasharp.desktop" "${stage}/VitaSharp/"
  cp "${TMPL}/install-desktop-entry.sh" "${stage}/VitaSharp/"
  for s in "${ICON_SIZES[@]}"; do
    cp "${ASSETS}/icon-${s}.png" "${stage}/VitaSharp/icons/vitasharp-${s}.png"
  done
  fix_modes "${stage}/VitaSharp" "${stage}/VitaSharp/VitaSharp.Desktop"
  chmod 755 "${stage}/VitaSharp/install-desktop-entry.sh"
  local tgz="${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.tar.gz"
  tar -C "${stage}" -czf "${tgz}" VitaSharp
  echo "   ${tgz} ($(size "${tgz}"))"

  local tool="${APPIMAGETOOL:-$(command -v appimagetool || command -v appimagetool-x86_64.AppImage || true)}"
  if [[ -n "${tool}" ]]; then
    package_appimage "${rid}" "${src}" "${pkgs}" "${stage}" "${tool}"
  else
    echo "   (no AppImage: appimagetool not found; get it from https://github.com/AppImage/appimagetool/releases and" \
      "put it on PATH or set APPIMAGETOOL)"
  fi
  rm -rf "${stage}"
}

#######################################
# macOS packages: VitaSharp-<v>-<rid>.app.zip and, on macOS, the dmg.
# Arguments:
#   Runtime identifier, publish folder, packages folder.
#######################################
package_macos() {
  local rid="$1" src="$2" pkgs="$3"
  local stage="${pkgs}/stage/${rid}"
  local app="${stage}/VitaSharp.app"
  rm -rf "${stage}"
  mkdir -p "${app}/Contents/MacOS" "${app}/Contents/Resources"
  # executable and dylibs together in Contents/MacOS: .NET, Silk.NET and SkiaSharp look for native libraries in
  # AppContext.BaseDirectory (the executable's folder), and the bundled dylibs do not link against each other
  cp -R "${src}/." "${app}/Contents/MacOS/"
  cp "${ROOT}/LICENSE" "${ROOT}/README.md" "${app}/Contents/Resources/"
  cp "${ASSETS}/VitaSharp.icns" "${app}/Contents/Resources/VitaSharp.icns"
  local short
  short="$(printf '%s' "${PUBLISH_VERSION}" | grep -Eo '^[0-9]+(\.[0-9]+){0,2}' || true)"
  sed -e "s/@VERSION@/${PUBLISH_VERSION}/" -e "s/@SHORT_VERSION@/${short:-0.0.0}/" "${TMPL}/Info.plist" \
    >"${app}/Contents/Info.plist"
  printf 'APPL????' >"${app}/Contents/PkgInfo"
  fix_modes "${app}" "${app}/Contents/MacOS/VitaSharp.Desktop"
  local zipf="${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.app.zip"
  (cd "${stage}" && make_zip "${zipf}" VitaSharp.app)
  echo "   ${zipf} ($(size "${zipf}"))"
  if command -v hdiutil >/dev/null; then
    local dmg="${pkgs}/VitaSharp-${PUBLISH_VERSION}-${rid}.dmg"
    ln -s /Applications "${stage}/Applications"
    rm -f "${dmg}"
    hdiutil create -quiet -volname VitaSharp -srcfolder "${stage}" -ov -format UDZO "${dmg}"
    echo "   ${dmg} ($(size "${dmg}"))"
  fi
  rm -rf "${stage}"
}

main() {
  local out="${OUT:-${ROOT}/artifacts/publish}"
  PUBLISH_VERSION="${VERSION:-dev}"
  local -a version_props=()
  if [[ "${PUBLISH_VERSION}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
    version_props=("-p:Version=${PUBLISH_VERSION}")
  fi
  # MSBuild turns every environment variable into a property, and property names ignore case: VERSION=ci-12 would
  # become $(Version) and fail the restore (NU1105 "'ci-12' is not a valid version string", which NuGet does not even
  # print: only "RestoreTask returned false but did not log an error"). Both are read above, keep them away from dotnet.
  unset VERSION OUT
  local aot=0 package=0 skip_publish=0 a
  local -a rids=()
  for a in "$@"; do
    case "${a}" in
      --aot) aot=1 ;;
      --package) package=1 ;;
      --skip-publish) skip_publish=1 ;;
      -h | --help)
        sed -n '2,25s/^# \{0,1\}//p' "${BASH_SOURCE[0]}"
        return 0
        ;;
      *) rids+=("${a}") ;;
    esac
  done
  if ((${#rids[@]} == 0)); then
    rids=("${DEFAULT_RIDS[@]}")
  fi
  mkdir -p "${out}"
  out="$(cd "${out}" && pwd)"

  case "$(uname -s)" in
    MINGW* | MSYS* | CYGWIN*) # Git Bash: the ILCompiler's vcvarsall.bat runs vswhere.exe by name
      local vswhere_dir="/c/Program Files (x86)/Microsoft Visual Studio/Installer"
      if [[ -d "${vswhere_dir}" ]]; then
        export PATH="${vswhere_dir}:${PATH}"
      fi
      ;;
  esac

  local rid
  local -a extra
  for rid in "${rids[@]}"; do
    if ((skip_publish == 1)); then
      if [[ -z "$(ls -A "${out}/${rid}" 2>/dev/null)" ]]; then
        echo "error: --skip-publish: nothing published in ${out}/${rid}" >&2
        return 1
      fi
    elif ((aot == 1)) && aot_host "${rid}"; then
      echo "== ${rid} (Native AOT)"
      extra=()
      # the ILCompiler links with clang by default; gcc works too when clang is not installed
      if [[ "$(uname -s)" == Linux ]] && ! command -v clang >/dev/null && command -v gcc >/dev/null; then
        echo "clang not found, linking with gcc"
        extra+=(-p:CppCompilerAndLinker=gcc)
      fi
      dotnet publish "${PROJECT}" -c Release -r "${rid}" -p:VitaSharpRelease=true -p:VitaSharpAot=true \
        ${extra[@]+"${extra[@]}"} ${version_props[@]+"${version_props[@]}"} -o "${out}/${rid}"
      mkdir -p "${out}/${rid}-symbols"
      find "${out}/${rid}" -maxdepth 1 \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) \
        -exec mv -f {} "${out}/${rid}-symbols/" \;
    else
      if ((aot == 1)); then
        echo "warning: ${rid}: Native AOT cannot be cross-compiled from this OS, publishing ReadyToRun instead" \
          "(build it on ${rid} for AOT)" >&2
      fi
      echo "== ${rid} (ReadyToRun)"
      dotnet publish "${PROJECT}" -c Release -r "${rid}" -p:VitaSharpRelease=true -p:DebugType=embedded \
        ${version_props[@]+"${version_props[@]}"} -o "${out}/${rid}"
    fi

    if ((package == 1)); then
      echo "== ${rid} package"
      mkdir -p "${out}/packages"
      case "${rid}" in
        win-*) package_windows "${rid}" "${out}/${rid}" "${out}/packages" ;;
        linux-*) package_linux "${rid}" "${out}/${rid}" "${out}/packages" ;;
        osx-*) package_macos "${rid}" "${out}/${rid}" "${out}/packages" ;;
      esac
      rm -rf "${out}/packages/stage"
    fi
  done
}

main "$@"
