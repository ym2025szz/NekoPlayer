#!/usr/bin/env bash
set -euo pipefail

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
install_root="${NEKOPLAYER_INSTALL_ROOT:-$HOME/.local/opt/NekoPlayer}"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
desktop_file="$data_home/applications/nekoplayer.desktop"
icon_file="$data_home/icons/hicolor/256x256/apps/nekoplayer.png"
marker="$install_root/.nekoplayer-user-install"

if [[ -e "$install_root" && ! -f "$marker" ]]; then
  echo "Refusing to overwrite an unrecognized directory: $install_root" >&2
  exit 1
fi

echo "Installing NekoPlayer for the current user in: $install_root"
mkdir -p "$install_root" "$(dirname "$desktop_file")" "$(dirname "$icon_file")"
cp -a "$source_dir/." "$install_root/"
touch "$marker"
cp "$source_dir/Assets/AppIcon.png" "$icon_file"
cat > "$desktop_file" <<EOF
[Desktop Entry]
Type=Application
Name=猫娘播放器
Comment=NekoPlayer local music player
Exec="$install_root/NekoPlayer"
Icon=nekoplayer
Terminal=false
Categories=AudioVideo;Audio;Player;
StartupNotify=true
EOF
chmod 0755 "$install_root/NekoPlayer" "$install_root/install-linux.sh" "$install_root/uninstall-linux.sh"
echo "Installed desktop entry: $desktop_file"
echo "User music library and settings were not modified."
