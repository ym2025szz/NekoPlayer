#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
solution="$root/NekoPlayer.sln"
project="$root/src/NekoPlayer.App/NekoPlayer.App.csproj"
publish_dir="$root/artifacts/publish/linux-x64"
release_dir="$root/artifacts/release"
archive="$release_dir/NekoPlayer-v1.0.0-linux-x64.tar.gz"
verification_dir="$root/artifacts/verification/linux-x64-archive"
skip_tests=false

if [[ "${1:-}" == "--skip-tests" ]]; then skip_tests=true; fi

command -v dotnet >/dev/null || { echo "dotnet SDK is required" >&2; exit 1; }
command -v ffmpeg >/dev/null || { echo "system ffmpeg is required" >&2; exit 1; }
command -v ffprobe >/dev/null || { echo "system ffprobe is required" >&2; exit 1; }
command -v file >/dev/null || { echo "file utility is required" >&2; exit 1; }

assert_under_artifacts() {
  local artifacts target
  artifacts="$(readlink -m "$root/artifacts")"
  target="$(readlink -m "$1")"
  [[ "$target" == "$artifacts/"* ]] || { echo "Output target escaped artifacts: $target" >&2; exit 1; }
}

assert_under_artifacts "$publish_dir"
assert_under_artifacts "$release_dir"
assert_under_artifacts "$archive"
assert_under_artifacts "$verification_dir"

dotnet restore "$solution" -p:Configuration=Release
dotnet build "$solution" -c Release --no-restore
if [[ "$skip_tests" != true ]]; then dotnet test "$solution" -c Release --no-build --logger "console;verbosity=minimal"; fi

rm -rf "$publish_dir"
rm -f "$archive"
rm -rf "$verification_dir"
mkdir -p "$publish_dir" "$release_dir"
dotnet publish "$project" -c Release -r linux-x64 --self-contained true --no-restore -o "$publish_dir" -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false

cp "$root/README.md" "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$publish_dir/"
mkdir -p "$publish_dir/Assets"
cp "$root/src/NekoPlayer.App/Assets/AppIcon.png" "$publish_dir/Assets/"
cp "$root/src/NekoPlayer.App/Assets/NekoPlayer.ico" "$publish_dir/Assets/"
cp "$root/packaging/linux/install-linux.sh" "$root/packaging/linux/uninstall-linux.sh" "$publish_dir/"
cp "$root/docs/LINUX.md" "$publish_dir/LINUX.md"
chmod 0755 "$publish_dir/NekoPlayer" "$publish_dir/install-linux.sh" "$publish_dir/uninstall-linux.sh"

[[ -x "$publish_dir/NekoPlayer" ]] || { echo "Linux app is not executable" >&2; exit 1; }
file "$publish_dir/NekoPlayer" | grep -Eq 'ELF 64-bit.*x86-64'
if ldd "$publish_dir/NekoPlayer" | grep -q 'not found'; then ldd "$publish_dir/NekoPlayer"; exit 1; fi
[[ ! -e "$publish_dir/NekoPlayer.exe" ]] || { echo "Windows executable leaked into Linux package" >&2; exit 1; }
if find "$publish_dir" -type f \( -iname 'ffmpeg.exe' -o -iname 'ffprobe.exe' -o -iname 'avcodec-*.dll' -o -iname 'avformat-*.dll' -o -iname 'avutil-*.dll' -o -iname 'swresample-*.dll' \) | grep -q .; then
  echo "Windows FFmpeg runtime leaked into Linux package" >&2; exit 1
fi
if find "$publish_dir" -type f \( -iname '*.pdb' -o -iname '*.db' -o -iname '*.db-wal' -o -iname '*.db-shm' -o -iname '*.sqlite' -o -iname '*.sqlite3' -o -iname '*.sqlite-wal' -o -iname '*.sqlite-shm' -o -iname '*.mp3' -o -iname '*.flac' -o -iname '*.wav' -o -iname '*.m4a' -o -iname '*.aac' -o -iname '*.ogg' -o -iname '*.opus' -o -iname '*.wma' -o -iname '*.ape' -o -iname '*.log' -o -iname '*.dmp' \) | grep -q .; then
  echo "Forbidden user, test, or debug file found in Linux package" >&2; exit 1
fi
private_pattern='C:\\Users\\|C:/Users/|C:\\codex'
private_pattern+='_tmp|C:/codex'
private_pattern+='_tmp|O:'
private_pattern+='\\|O:'
private_pattern+='/'
if grep -RIlE "$private_pattern" "$publish_dir" --include='*.json' --include='*.config' --include='*.md' --include='*.txt' --include='*.xml' --include='*.sh' | grep -q .; then
  echo "Local absolute path found in Linux package" >&2; exit 1
fi
if grep -q 'Avalonia.Diagnostics' "$publish_dir/NekoPlayer.deps.json"; then echo "Avalonia.Diagnostics found in Linux release" >&2; exit 1; fi

tar -czf "$archive" -C "$publish_dir" .
mkdir -p "$verification_dir"
tar -xzf "$archive" -C "$verification_dir"
[[ -x "$verification_dir/NekoPlayer" ]] || { echo "Extracted Linux app is not executable" >&2; exit 1; }
for required in README.md LINUX.md LICENSE THIRD-PARTY-NOTICES.md Assets/AppIcon.png install-linux.sh uninstall-linux.sh; do
  [[ -f "$verification_dir/$required" ]] || { echo "Archive extraction is missing: $required" >&2; exit 1; }
done
file "$verification_dir/NekoPlayer" | grep -Eq 'ELF 64-bit.*x86-64'
hash="$(sha256sum "$archive" | awk '{print $1}')"
count="$(find "$publish_dir" -type f | wc -l | tr -d ' ')"
bytes="$(find "$publish_dir" -type f -printf '%s\n' | awk '{sum+=$1} END {print sum+0}')"
echo "Linux archive: $archive"
echo "SHA-256: $hash"
echo "Files: $count; bytes: $bytes"
