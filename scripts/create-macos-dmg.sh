#!/usr/bin/env bash
set -euo pipefail

ARCH_NAME="${1:?usage: create-macos-dmg.sh <arch-name> e.g. osx-x64}"

# Velopack output from scripts/macos/pack-velopack.sh, already signed when an identity is set.
APP="bin/${ARCH_NAME}/velopack/OpenUtau-Chiaki.app"
OUT="OpenUtau-Chiaki-${ARCH_NAME}.dmg"

if [[ ! -d "${APP}" ]]; then
  echo "App bundle not found: ${APP} (run pack-velopack.sh first)" >&2
  exit 1
fi

if [[ -z "${MACOS_SIGN_IDENTITY:-}" || "${MACOS_SIGN_IDENTITY}" == "-" ]]; then
  # vpk skips signing without an identity; Apple Silicon still needs an ad hoc seal.
  bash "$(dirname "$0")/macos/sign-app.sh" "${APP}"
fi

rm -f "${OUT}" *.dmg
hdiutil detach "/Volumes/OpenUtau Chiaki" -force 2>/dev/null || true
hdiutil detach "/Volumes/OpenUtau" -force 2>/dev/null || true
hdiutil detach "/Volumes/OpenUtau-Chiaki" -force 2>/dev/null || true

npm install -g create-dmg
create-dmg --no-code-sign "${APP}"
mv *.dmg "${OUT}"
if [[ -n "${MACOS_SIGN_IDENTITY:-}" && "${MACOS_SIGN_IDENTITY}" != "-" ]]; then
  codesign --force --timestamp --sign "${MACOS_SIGN_IDENTITY}" "${OUT}"
else
  codesign -fvs - "${OUT}" || true
fi
