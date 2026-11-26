#!/usr/bin/env bash
# Signs an .app with the hardened runtime. Uses MACOS_SIGN_IDENTITY, or an ad hoc signature when unset.
set -euo pipefail

APP="${1:?usage: sign-app.sh <path/to/App.app>}"
IDENTITY="${MACOS_SIGN_IDENTITY:--}"
ENTITLEMENTS="$(cd "$(dirname "$0")" && pwd)/entitlements.plist"
MAIN="$APP/Contents/MacOS/$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP/Contents/Info.plist")"

if [ "$IDENTITY" = "-" ]; then
  echo "MACOS_SIGN_IDENTITY not set; signing ad hoc."
  base=(codesign --force --options runtime --timestamp=none --sign -)
else
  base=(codesign --force --options runtime --timestamp --sign "$IDENTITY")
fi

# Contents/MacOS also holds data files (voicebank, resources); codesign requires each one to be sealed.
# Nested files first, then the main executable with entitlements, then the bundle.
find "$APP/Contents/MacOS" -type f ! -path "$MAIN" -print0 | xargs -0 -n 20 -P 8 "${base[@]}"
# The command line is a second .NET host: without the entitlements the hardened runtime stops it loading the runtime.
CLI="$APP/Contents/MacOS/OpenUtau.Cli"
if [ -f "$CLI" ]; then
  "${base[@]}" --entitlements "$ENTITLEMENTS" "$CLI"
fi
"${base[@]}" --entitlements "$ENTITLEMENTS" "$MAIN"
"${base[@]}" --entitlements "$ENTITLEMENTS" "$APP"

codesign --verify --deep --strict --verbose=2 "$APP"
