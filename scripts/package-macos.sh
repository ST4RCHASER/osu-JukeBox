#!/usr/bin/env bash
#
# Wraps a self-contained single-file macOS publish into a drag-to-Applications .dmg.
#
# Two layers, and the second is the one people get wrong:
#
#   1. A real .app bundle. macOS will happily launch a bare executable from a Terminal, but
#      double-clicking one in Finder opens a Terminal window instead of the app, and a bare
#      executable has no icon, no name and no place in the Dock. The bundle is what makes it an
#      application rather than a file that happens to be runnable.
#   2. A .dmg whose window contains the bundle next to a symlink to /Applications, so the drag
#      target the user is being asked to aim at actually exists.
#
# The single-file executable inside the bundle carries every native dependency osu!framework
# needs (BASS/BASS_FX/BASSmix, SDL2, FFmpeg, MoltenVK) as embedded resources, extracted next to
# each other on first launch. That is why nothing here copies dylibs around: if the publish was
# made without -p:IncludeNativeLibrariesForSelfExtract=true, the bundle will build fine and then
# fail at runtime, which is exactly the failure mode the CI smoke step exists to catch.
#
# Usage: package-macos.sh <publish-dir> <staging-dir> <version> [numeric-version]
set -euo pipefail

PUBLISH_DIR="${1:?publish directory}"
STAGING_DIR="${2:?staging directory}"
# What the tag said — "1.0.0-rc1" — shown to the user as the app's version.
VERSION="${3:-0.0.0}"
# CFBundleVersion is a numeric-only build number: Apple's tooling rejects a prerelease suffix
# there, even though CFBundleShortVersionString carries it happily. Defaults to the version with
# any suffix cut off, so a caller that passes only one argument still produces a valid bundle.
NUMERIC_VERSION="${4:-${VERSION%%-*}}"

APP_NAME="osu!JukeBox"
# The bundle DIRECTORY name, which is what Finder shows and what ends up in /Applications. Kept
# free of the "!" that the display name carries: a shell-hostile character in a path that users
# will type into terminals and that CI globs over is not worth the cosmetics.
BUNDLE_NAME="osu-JukeBox"
BUNDLE_ID="dev.starchaser.osujukebox"
EXECUTABLE="JukeBox"

APP="$STAGING_DIR/$BUNDLE_NAME.app"
DMG="$STAGING_DIR/$BUNDLE_NAME-$VERSION-macos-arm64.dmg"

rm -rf "$APP" "$DMG"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# ---- payload -----------------------------------------------------------------------------

if [[ ! -f "$PUBLISH_DIR/$EXECUTABLE" ]]; then
    echo "no '$EXECUTABLE' in '$PUBLISH_DIR' — was this published single-file?" >&2
    exit 1
fi

cp "$PUBLISH_DIR/$EXECUTABLE" "$APP/Contents/MacOS/$EXECUTABLE"
chmod +x "$APP/Contents/MacOS/$EXECUTABLE"

# ---- icon --------------------------------------------------------------------------------
#
# iconutil wants an .iconset of individually-sized PNGs; the shipped game.ico is a single 256px
# image, so every size is resampled from it. Missing sizes are not fatal to iconutil, but a bundle
# without the small ones shows a blurry icon in the Dock and in list views.

ICONSET="$STAGING_DIR/icon.iconset"
rm -rf "$ICONSET"
mkdir -p "$ICONSET"

SOURCE_ICON="$(dirname "$0")/../JukeBox.Desktop/game.ico"
BASE_PNG="$STAGING_DIR/icon-base.png"
sips -s format png "$SOURCE_ICON" --out "$BASE_PNG" >/dev/null

for size in 16 32 128 256 512; do
    sips -z $size $size "$BASE_PNG" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    sips -z $((size * 2)) $((size * 2)) "$BASE_PNG" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done

iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/$BUNDLE_NAME.icns"

# ---- Info.plist --------------------------------------------------------------------------
#
# LSMinimumSystemVersion 11.0 is the floor for the osx-arm64 runtime we publish against.
# NSHighResolutionCapable matters visibly: without it the whole window renders at 1x and is
# noticeably soft on any Retina display.

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>$APP_NAME</string>
    <key>CFBundleDisplayName</key>
    <string>$APP_NAME</string>
    <key>CFBundleIdentifier</key>
    <string>$BUNDLE_ID</string>
    <key>CFBundleVersion</key>
    <string>$NUMERIC_VERSION</string>
    <key>CFBundleShortVersionString</key>
    <string>$VERSION</string>
    <key>CFBundleExecutable</key>
    <string>$EXECUTABLE</string>
    <key>CFBundleIconFile</key>
    <string>$BUNDLE_NAME</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
PLIST

# An ad-hoc signature. This is NOT notarisation and does not get past Gatekeeper on its own — the
# user still has to clear the quarantine flag on first launch (see the README the workflow ships
# in the artifact). It is here because arm64 macOS refuses to execute a binary with NO signature
# at all, so without this the app dies instantly and silently instead of showing Apple's dialog.
codesign --force --deep --sign - "$APP" 2>/dev/null || echo "ad-hoc codesign failed — the bundle may not launch on arm64" >&2

# ---- dmg ---------------------------------------------------------------------------------

DMG_ROOT="$STAGING_DIR/dmg-root"
rm -rf "$DMG_ROOT"
mkdir -p "$DMG_ROOT"

cp -R "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/Applications"

# UDZO (zlib) rather than the default: the payload is a ~300MB single-file bundle whose managed
# assemblies compress well, and the download size is what the user actually pays.
hdiutil create \
    -volname "$APP_NAME" \
    -srcfolder "$DMG_ROOT" \
    -ov \
    -format UDZO \
    "$DMG" >/dev/null

rm -rf "$DMG_ROOT" "$ICONSET" "$BASE_PNG"

echo "app: $APP"
echo "dmg: $DMG"
