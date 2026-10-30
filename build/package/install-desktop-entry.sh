#!/bin/sh
# Adds VitaSharp to the desktop's application menu for the current user (icon + .desktop file pointing at this
# folder). Run it again after moving the folder; remove with: install-desktop-entry.sh --uninstall
set -eu
here="$(cd "$(dirname "$0")" && pwd)"
data="${XDG_DATA_HOME:-$HOME/.local/share}"
if [ "${1:-}" = "--uninstall" ]; then
  rm -f "$data/applications/vitasharp.desktop"
  for s in 16 24 32 48 64 128 256 512; do rm -f "$data/icons/hicolor/${s}x${s}/apps/vitasharp.png"; done
  echo "VitaSharp removed from the application menu"
  exit 0
fi
for s in 16 24 32 48 64 128 256 512; do
  mkdir -p "$data/icons/hicolor/${s}x${s}/apps"
  cp "$here/icons/vitasharp-$s.png" "$data/icons/hicolor/${s}x${s}/apps/vitasharp.png"
done
mkdir -p "$data/applications"
sed "s|^Exec=.*|Exec=\"$here/VitaSharp.Desktop\" %f|" "$here/vitasharp.desktop" > "$data/applications/vitasharp.desktop"
command -v update-desktop-database >/dev/null && update-desktop-database "$data/applications" || true
command -v gtk-update-icon-cache >/dev/null && gtk-update-icon-cache -q "$data/icons/hicolor" || true
echo "VitaSharp added to the application menu ($data/applications/vitasharp.desktop)"
