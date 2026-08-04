#!/usr/bin/env bash
set -euo pipefail

install_root="${NEKOPLAYER_INSTALL_ROOT:-$HOME/.local/opt/NekoPlayer}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
desktop_file="$data_home/applications/nekoplayer.desktop"
icon_file="$data_home/icons/hicolor/256x256/apps/nekoplayer.png"
marker="$install_root/.nekoplayer-user-install"

rm -f "$desktop_file" "$icon_file"
if [[ -d "$install_root" ]]; then
  if [[ ! -f "$marker" ]]; then
    echo "Refusing to remove an unrecognized directory: $install_root" >&2
    exit 1
  fi
  rm -rf -- "$install_root"
fi

echo "NekoPlayer program files and desktop entry were removed."
echo "User data under XDG data/config/cache directories was preserved."
