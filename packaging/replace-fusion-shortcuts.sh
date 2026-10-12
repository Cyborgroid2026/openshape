#!/bin/sh
# After installing OpenShape CAD: take the Autodesk Fusion (Wine) shortcuts off the desktop and the
# application menu, and put OpenShape CAD on the desktop instead. Nothing is deleted: the Fusion
# entries are moved to ~/.local/share/openshape/fusion-shortcuts-backup (restore with --restore).
# The Fusion installation itself (~/.fusion360-wine) and the cloud sync are left alone.
set -eu
BACKUP="$HOME/.local/share/openshape/fusion-shortcuts-backup"
DESKTOP="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
APPS="$HOME/.local/share/applications"
MENUS="$HOME/.config/menus/applications-merged"

move() {   # move <file> <backup-subdir>
    [ -e "$1" ] || return 0
    mkdir -p "$BACKUP/$2"
    mv -f "$1" "$BACKUP/$2/"
    echo "moved: $1"
}

if [ "${1:-}" = "--restore" ]; then
    [ -d "$BACKUP" ] || { echo "nothing to restore"; exit 0; }
    for f in "$BACKUP"/desktop/*; do [ -e "$f" ] && mv -f "$f" "$DESKTOP/"; done
    mkdir -p "$APPS/wine/Programs/Autodesk" "$MENUS"
    for f in "$BACKUP"/apps/*; do [ -e "$f" ] && mv -f "$f" "$APPS/wine/Programs/Autodesk/"; done
    for f in "$BACKUP"/menus/*; do [ -e "$f" ] && mv -f "$f" "$MENUS/"; done
    rm -f "$DESKTOP/openshape.desktop"
    echo "restored the Fusion shortcuts"
    exit 0
fi

move "$DESKTOP/Autodesk Fusion.desktop" desktop
move "$DESKTOP/Autodesk Fusion.lnk" desktop
move "$APPS/wine/Programs/Autodesk/Autodesk Fusion.desktop" apps
move "$MENUS/wine-Programs-Autodesk-Autodesk Fusion.menu" menus
rmdir "$APPS/wine/Programs/Autodesk" 2>/dev/null || true

if [ -f /usr/share/applications/openshape.desktop ]; then
    cp /usr/share/applications/openshape.desktop "$DESKTOP/openshape.desktop"
    chmod +x "$DESKTOP/openshape.desktop"
    gio set "$DESKTOP/openshape.desktop" metadata::trusted true 2>/dev/null || true
    echo "added: $DESKTOP/openshape.desktop"
fi
update-desktop-database -q "$APPS" 2>/dev/null || true
echo "done (backup: $BACKUP)"
