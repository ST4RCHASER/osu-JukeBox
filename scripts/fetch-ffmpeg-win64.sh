#!/usr/bin/env bash
# Downloads a static win64 ffmpeg for bundling into the single-file Windows publish
# (see JukeBox.Desktop.csproj, "Bundled ffmpeg"). The binary stays OUT of git — this script puts it
# where the csproj expects it, and the publish fails with a pointer here when it is missing.
#
# Source: gyan.dev's "release essentials" build — a tagged ffmpeg release (the version is recorded
# next to the binary in VERSION.txt). NOTE: this is a GPL build; fine for personal use, but
# distributing the resulting exe carries GPL obligations — swap in an LGPL build before shipping.
set -euo pipefail

cd "$(dirname "$0")/.."

url="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip"
dest="JukeBox.Desktop/ffmpeg/win-x64"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "downloading $url"
curl -fL --retry 3 "$url" -o "$tmp/ffmpeg.zip"
unzip -q "$tmp/ffmpeg.zip" -d "$tmp"

exe="$(find "$tmp" -name ffmpeg.exe -path '*/bin/*' | head -1)"
[ -n "$exe" ] || { echo "ffmpeg.exe not found in the archive" >&2; exit 1; }

mkdir -p "$dest"
cp "$exe" "$dest/ffmpeg.exe"

# The archive's top-level folder carries the release version (e.g. ffmpeg-8.0-essentials_build).
basename "$(dirname "$(dirname "$exe")")" > "$dest/VERSION.txt"

echo "bundled $(cat "$dest/VERSION.txt") -> $dest/ffmpeg.exe ($(du -h "$dest/ffmpeg.exe" | cut -f1))"
