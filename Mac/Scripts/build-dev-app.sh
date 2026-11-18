#!/bin/zsh
set -euo pipefail

SCRIPT_DIR=${0:A:h}
PROJECT_DIR=${SCRIPT_DIR:h}
DIST_DIR="${NATIVE_SERIALS_MAC_PACKAGE_DIR:-/tmp/NativeSerialsMac-dist}"
APP_DIR="$DIST_DIR/Native Serials.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"

cd "$PROJECT_DIR"
swift build -c release --arch arm64 --arch x86_64
BIN_DIR=$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"
cp "$BIN_DIR/NativeSerials" "$MACOS_DIR/NativeSerials"
cp "$PROJECT_DIR/../Manual.html" "$RESOURCES_DIR/Manual.html"
cp "$PROJECT_DIR/../ThirdParty/Onj.SignedUpdater/LICENSE" "$RESOURCES_DIR/ThirdPartyNotices.txt"

cat > "$CONTENTS_DIR/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleDevelopmentRegion</key>
  <string>en</string>
  <key>CFBundleExecutable</key>
  <string>NativeSerials</string>
  <key>CFBundleIdentifier</key>
  <string>com.andrelouis.nativeserials</string>
  <key>CFBundleInfoDictionaryVersion</key>
  <string>6.0</string>
  <key>CFBundleName</key>
  <string>Native Serials</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>1.1.1</string>
  <key>CFBundleVersion</key>
  <string>1.1.1</string>
  <key>LSMinimumSystemVersion</key>
  <string>13.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
</dict>
</plist>
PLIST

echo "Built $APP_DIR"
cd "$DIST_DIR"
/usr/bin/zip -qry "NativeSerialsMac-1.1.1.zip" "Native Serials.app"
echo "Built $DIST_DIR/NativeSerialsMac-1.1.1.zip"
