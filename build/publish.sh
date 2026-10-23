#!/usr/bin/env bash
# Self-contained release builds of VitaSharp (launcher + emulator in one executable).
#   build/publish.sh                        all platforms, single-file ReadyToRun
#   build/publish.sh linux-x64 osx-arm64    only these
#   build/publish.sh --aot linux-x64        Native AOT (see below)
# Output: artifacts/publish/<rid>/ (override with OUT=<folder>). ReadyToRun builds can be published for any platform
# from any host; FFmpeg is downloaded for the target platform (macOS uses the system FFmpeg: brew install ffmpeg).
#
# --aot compiles the whole app to native code ahead of time (faster start, less memory, no JIT at run time). Native
# AOT cannot cross-compile between operating systems, so it is used only where the host can build it (Linux: the
# host's own architecture, needs clang and zlib1g-dev; macOS: osx-x64 and osx-arm64, needs the Xcode command-line
# tools); the other platforms fall back to ReadyToRun with a warning. Debug symbols go to <out>/<rid>-symbols/.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${OUT:-$root/artifacts/publish}"
aot=0
rids=()
for a in "$@"; do
  case "$a" in
    --aot) aot=1 ;;
    *) rids+=("$a") ;;
  esac
done
[ ${#rids[@]} -eq 0 ] && rids=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)

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

project="$root/src/VitaSharp.Desktop/VitaSharp.Desktop.csproj"
for rid in "${rids[@]}"; do
  if [ $aot -eq 1 ] && aot_host "$rid"; then
    echo "== $rid (Native AOT)"
    extra=()
    # the ILCompiler links with clang by default; gcc works too when clang is not installed
    if [ "$(uname -s)" = Linux ] && ! command -v clang >/dev/null && command -v gcc >/dev/null; then
      echo "clang not found, linking with gcc"
      extra+=(-p:CppCompilerAndLinker=gcc)
    fi
    dotnet publish "$project" -c Release -r "$rid" -p:VitaSharpRelease=true -p:VitaSharpAot=true ${extra[@]+"${extra[@]}"} -o "$out/$rid"
    mkdir -p "$out/$rid-symbols"
    find "$out/$rid" -maxdepth 1 \( -name '*.pdb' -o -name '*.dbg' -o -name '*.dSYM' \) -exec mv -f {} "$out/$rid-symbols/" \;
  else
    [ $aot -eq 1 ] && echo "warning: $rid: Native AOT cannot be cross-compiled from this OS, publishing ReadyToRun instead (build it on $rid for AOT)" >&2
    echo "== $rid (ReadyToRun)"
    dotnet publish "$project" -c Release -r "$rid" -p:VitaSharpRelease=true -p:DebugType=embedded -o "$out/$rid"
  fi
done
