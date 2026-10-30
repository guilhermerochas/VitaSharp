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
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${OUT:-$root/artifacts/publish}"
version="${VERSION:-dev}"
version_props=()
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] && version_props=("-p:Version=$version")
aot=0
package=0
skip_publish=0
rids=()
for a in "$@"; do
  case "$a" in
    --aot) aot=1 ;;
    --package) package=1 ;;
    --skip-publish) skip_publish=1 ;;
    *) rids+=("$a") ;;
  esac
done
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
mkdir -p "$out"
out="$(cd "$out" && pwd)"

aot_host() { # can this host build Native AOT for $1?
  local arch
  case "$(uname -m)" in x86_64|amd64) arch=x64 ;; aarch64|arm64) arch=arm64 ;; *) arch=unknown ;; esac
  case "$(uname -s)" in
    Linux) [ "$1" = "linux-$arch" ] ;;
    Darwin) [[ "$1" == osx-* ]] ;;
    MINGW*|MSYS*|CYGWIN*) [[ "$1" == win-* ]] ;;
    *) false ;;
  esac
}

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) # Git Bash: the ILCompiler's vcvarsall.bat runs vswhere.exe by name
    vswhere_dir="/c/Program Files (x86)/Microsoft Visual Studio/Installer"
    [ -d "$vswhere_dir" ] && export PATH="$vswhere_dir:$PATH" ;;
esac

size() { du -h "$1" | cut -f1; }
# regular permissions on a copied publish folder (a copy from a Windows drive marks every file executable), then +x on
# the executable: $1 folder, $2 executable path
fix_modes() {
  find "$1" -type d -exec chmod 755 {} + ; find "$1" -type f -exec chmod 644 {} +
  chmod 755 "$2"
}
assets="$root/src/VitaSharp.Desktop/Assets"
tmpl="$root/build/package"

# zip <archive> <folder in the current directory>: keeps Unix permissions and symlinks
make_zip() {
  rm -f "$1"
  if [ "$(uname -s)" = Darwin ]; then ditto -c -k --sequesterRsrc --keepParent "$2" "$1"
  elif command -v zip >/dev/null; then zip -qry "$1" "$2"
  elif command -v powershell.exe >/dev/null; then
    powershell.exe -NoProfile -Command "Compress-Archive -Path '$(cygpath -w "$PWD/$2")' -DestinationPath '$(cygpath -w "$1")'"
  else echo "error: no zip tool (install zip)" >&2; return 1
  fi
}

package_windows() { # $1 rid, $2 publish folder, $3 packages folder
  local stage="$3/stage/$1"
  rm -rf "$stage"; mkdir -p "$stage/VitaSharp"
  cp -R "$2/." "$stage/VitaSharp/"
  cp "$root/LICENSE" "$root/README.md" "$stage/VitaSharp/"
  (cd "$stage" && make_zip "$3/VitaSharp-$version-$1.zip" VitaSharp)
  echo "   $3/VitaSharp-$version-$1.zip ($(size "$3/VitaSharp-$version-$1.zip"))"
  rm -rf "$stage"
}

package_linux() {
  local rid="$1" src="$2" pkgs="$3" stage="$3/stage/$1"
  rm -rf "$stage"; mkdir -p "$stage/VitaSharp/icons"
  cp -R "$src/." "$stage/VitaSharp/"
  cp "$root/LICENSE" "$root/README.md" "$tmpl/vitasharp.desktop" "$stage/VitaSharp/"
  cp "$tmpl/install-desktop-entry.sh" "$stage/VitaSharp/"
  for s in 16 24 32 48 64 128 256 512; do cp "$assets/icon-$s.png" "$stage/VitaSharp/icons/vitasharp-$s.png"; done
  fix_modes "$stage/VitaSharp" "$stage/VitaSharp/VitaSharp.Desktop"
  chmod 755 "$stage/VitaSharp/install-desktop-entry.sh"
  local tgz="$pkgs/VitaSharp-$version-$rid.tar.gz"
  tar -C "$stage" -czf "$tgz" VitaSharp
  echo "   $tgz ($(size "$tgz"))"

  # AppImage: AppRun + desktop file + icon at the AppDir root, the app in usr/lib/vitasharp
  local tool="${APPIMAGETOOL:-$(command -v appimagetool || command -v appimagetool-x86_64.AppImage || true)}"
  if [ -n "$tool" ]; then
    local appdir="$stage/VitaSharp.AppDir" arch
    case "$rid" in linux-x64) arch=x86_64 ;; linux-arm64) arch=aarch64 ;; *) arch=unknown ;; esac
    mkdir -p "$appdir/usr/lib/vitasharp" "$appdir/usr/share/applications"
    cp -R "$src/." "$appdir/usr/lib/vitasharp/"
    cp "$root/LICENSE" "$appdir/usr/lib/vitasharp/"
    cp "$tmpl/AppRun" "$appdir/AppRun"
    cp "$tmpl/vitasharp.desktop" "$appdir/vitasharp.desktop"
    cp "$tmpl/vitasharp.desktop" "$appdir/usr/share/applications/vitasharp.desktop"
    for s in 16 24 32 48 64 128 256 512; do
      mkdir -p "$appdir/usr/share/icons/hicolor/${s}x${s}/apps"
      cp "$assets/icon-$s.png" "$appdir/usr/share/icons/hicolor/${s}x${s}/apps/vitasharp.png"
    done
    cp "$assets/icon-256.png" "$appdir/vitasharp.png"
    ln -sf vitasharp.png "$appdir/.DirIcon"
    fix_modes "$appdir" "$appdir/usr/lib/vitasharp/VitaSharp.Desktop"
    chmod 755 "$appdir/AppRun"
    local img="$pkgs/VitaSharp-$version-$rid.AppImage"
    # APPIMAGE_EXTRACT_AND_RUN: appimagetool is itself an AppImage, this runs it without FUSE (containers, WSL, CI)
    local log
    if ! log="$(APPIMAGE_EXTRACT_AND_RUN=1 ARCH="$arch" "$tool" --no-appstream "$appdir" "$img" 2>&1)"; then
      echo "$log" >&2; echo "error: appimagetool failed (it needs the 'file' command)" >&2; return 1
    fi
    echo "   $img ($(size "$img"))"
  else
    echo "   (no AppImage: appimagetool not found; get it from https://github.com/AppImage/appimagetool/releases and put it on PATH or set APPIMAGETOOL)"
  fi
  rm -rf "$stage"
}

package_macos() {
  local rid="$1" src="$2" pkgs="$3" stage="$3/stage/$1"
  local app="$stage/VitaSharp.app"
  rm -rf "$stage"; mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  # executable and dylibs together in Contents/MacOS: .NET, Silk.NET and SkiaSharp look for native libraries in
  # AppContext.BaseDirectory (the executable's folder), and the bundled dylibs do not link against each other
  cp -R "$src/." "$app/Contents/MacOS/"
  cp "$root/LICENSE" "$root/README.md" "$app/Contents/Resources/"
  cp "$assets/VitaSharp.icns" "$app/Contents/Resources/VitaSharp.icns"
  local short
  short="$(printf '%s' "$version" | grep -Eo '^[0-9]+(\.[0-9]+){0,2}' || true)"
  sed -e "s/@VERSION@/$version/" -e "s/@SHORT_VERSION@/${short:-0.0.0}/" "$tmpl/Info.plist" > "$app/Contents/Info.plist"
  printf 'APPL????' > "$app/Contents/PkgInfo"
  fix_modes "$app" "$app/Contents/MacOS/VitaSharp.Desktop"
  local zipf="$pkgs/VitaSharp-$version-$rid.app.zip"
  (cd "$stage" && make_zip "$zipf" VitaSharp.app)
  echo "   $zipf ($(size "$zipf"))"
  if command -v hdiutil >/dev/null; then
    local dmg="$pkgs/VitaSharp-$version-$rid.dmg"
    ln -s /Applications "$stage/Applications"
    rm -f "$dmg"
    hdiutil create -quiet -volname VitaSharp -srcfolder "$stage" -ov -format UDZO "$dmg"
    echo "   $dmg ($(size "$dmg"))"
  fi
  rm -rf "$stage"
}

project="$root/src/VitaSharp.Desktop/VitaSharp.Desktop.csproj"
for rid in "${rids[@]}"; do
  if [ $skip_publish -eq 1 ]; then
    [ -n "$(ls -A "$out/$rid" 2>/dev/null)" ] || { echo "error: --skip-publish: nothing published in $out/$rid" >&2; exit 1; }
  elif [ $aot -eq 1 ] && aot_host "$rid"; then
    echo "== $rid (Native AOT)"
    extra=()
    # the ILCompiler links with clang by default; gcc works too when clang is not installed
    if [ "$(uname -s)" = Linux ] && ! command -v clang >/dev/null && command -v gcc >/dev/null; then
      echo "clang not found, linking with gcc"
      extra+=(-p:CppCompilerAndLinker=gcc)
    fi
    dotnet publish "$project" -c Release -r "$rid" -p:VitaSharpRelease=true -p:VitaSharpAot=true ${extra[@]+"${extra[@]}"} ${version_props[@]+"${version_props[@]}"} -o "$out/$rid"
    mkdir -p "$out/$rid-symbols"
    find "$out/$rid" -maxdepth 1 \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) -exec mv -f {} "$out/$rid-symbols/" \;
  else
    [ $aot -eq 1 ] && echo "warning: $rid: Native AOT cannot be cross-compiled from this OS, publishing ReadyToRun instead (build it on $rid for AOT)" >&2
    echo "== $rid (ReadyToRun)"
    dotnet publish "$project" -c Release -r "$rid" -p:VitaSharpRelease=true -p:DebugType=embedded ${version_props[@]+"${version_props[@]}"} -o "$out/$rid"
  fi

  if [ $package -eq 1 ]; then
    echo "== $rid package"
    mkdir -p "$out/packages"
    case "$rid" in
      win-*) package_windows "$rid" "$out/$rid" "$out/packages" ;;
      linux-*) package_linux "$rid" "$out/$rid" "$out/packages" ;;
      osx-*) package_macos "$rid" "$out/$rid" "$out/packages" ;;
    esac
    rm -rf "$out/packages/stage"
  fi
done
