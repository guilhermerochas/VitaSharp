#!/bin/sh
#
# Adds VitaSharp to the desktop's application menu for the current user (icon + .desktop file pointing at this
# folder). Run it again after moving the folder; remove with: install-desktop-entry.sh --uninstall
# POSIX sh on purpose: shipped in the Linux packages, run by users on any distribution.

set -eu

readonly SIZES="16 24 32 48 64 128 256 512"

# Removes the menu entry and the icons.
uninstall() {
  rm -f "${DATA}/applications/vitasharp.desktop"
  # shellcheck disable=SC2086  # word splitting of the size list is intended (POSIX sh has no arrays)
  for s in ${SIZES}; do
    rm -f "${DATA}/icons/hicolor/${s}x${s}/apps/vitasharp.png"
  done
  echo "VitaSharp removed from the application menu"
}

# Installs the icons and the menu entry for the executable in this folder.
install_entry() {
  # shellcheck disable=SC2086  # word splitting of the size list is intended (POSIX sh has no arrays)
  for s in ${SIZES}; do
    mkdir -p "${DATA}/icons/hicolor/${s}x${s}/apps"
    cp "${HERE}/icons/vitasharp-${s}.png" "${DATA}/icons/hicolor/${s}x${s}/apps/vitasharp.png"
  done
  mkdir -p "${DATA}/applications"
  sed "s|^Exec=.*|Exec=\"${HERE}/VitaSharp.Desktop\" %f|" "${HERE}/vitasharp.desktop" \
    >"${DATA}/applications/vitasharp.desktop"
  if command -v update-desktop-database >/dev/null; then
    update-desktop-database "${DATA}/applications" || true
  fi
  if command -v gtk-update-icon-cache >/dev/null; then
    gtk-update-icon-cache -q "${DATA}/icons/hicolor" || true
  fi
  echo "VitaSharp added to the application menu (${DATA}/applications/vitasharp.desktop)"
}

main() {
  case "${1:-}" in
    --uninstall) uninstall ;;
    -h | --help) echo "Usage: install-desktop-entry.sh [--uninstall]" ;;
    *) install_entry ;;
  esac
}

HERE="$(cd "$(dirname "$0")" && pwd)"
DATA="${XDG_DATA_HOME:-${HOME}/.local/share}"
readonly HERE DATA

main "$@"
