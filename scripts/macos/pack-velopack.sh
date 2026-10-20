#!/usr/bin/env bash
# Packs the prepared .app with Velopack so installs from the DMG can update in-app.
# Signs with MACOS_SIGN_IDENTITY when set; notarization happens on the DMG afterwards.
set -euo pipefail

ARCH_NAME="${1:?usage: pack-velopack.sh <arch-name> e.g. osx-arm64}"
: "${PACK_VERSION:?PACK_VERSION is required}"

APP="bin/${ARCH_NAME}/publish/OpenUtau-Chiaki.app"
PACKED_DIR="bin/${ARCH_NAME}/velopack"
OUT_DIR="Releases/${ARCH_NAME}"
CHANNEL="${ARCH_NAME}"
if [ "${BETA:-false}" = "true" ]; then
  CHANNEL="${ARCH_NAME}-beta"
fi

if [ ! -d "${APP}" ]; then
  echo "App bundle not found: ${APP} (run prepare-app.sh first)" >&2
  exit 1
fi

mkdir -p "${OUT_DIR}"
dotnet tool install -g vpk --version 1.2.0
export PATH="${HOME}/.dotnet/tools:${PATH}"

# The previous full package is the delta base; the first release of a channel has none.
if [ -n "${GITHUB_TOKEN:-}" ] && [ -n "${REPO_URL:-}" ]; then
  download=(download github --repoUrl "${REPO_URL}" --channel "${CHANNEL}" --token "${GITHUB_TOKEN}" --outputDir "${OUT_DIR}")
  if [ "${BETA:-false}" = "true" ]; then
    download+=(--pre)
  fi
  vpk "${download[@]}" || echo "vpk download skipped/failed (first release for channel?). Continuing with pack."
fi

sign=()
if [ -n "${MACOS_SIGN_IDENTITY:-}" ] && [ "${MACOS_SIGN_IDENTITY}" != "-" ]; then
  # vpk rejects entitlements files without the .entitlements extension.
  entitlements="$(mktemp -d)/app.entitlements"
  cp "$(cd "$(dirname "$0")" && pwd)/entitlements.plist" "${entitlements}"
  sign=(--signAppIdentity "${MACOS_SIGN_IDENTITY}" --signEntitlements "${entitlements}")
  if [ -n "${MACOS_KEYCHAIN:-}" ]; then
    sign+=(--keychain "${MACOS_KEYCHAIN}")
  fi
fi

vpk pack \
  --packId OpenUtau.Chiaki \
  --packVersion "${PACK_VERSION}" \
  --packDir "${APP}" \
  --mainExe OpenUtau-Chiaki \
  --packTitle OpenUtau-Chiaki \
  --channel "${CHANNEL}" \
  --runtime "${ARCH_NAME}" \
  --noInst \
  ${sign[@]+"${sign[@]}"} \
  --outputDir "${OUT_DIR}"

portable="$(find "${OUT_DIR}" -maxdepth 1 -name '*-Portable.zip' | head -n 1)"
if [ -z "${portable}" ]; then
  echo "Velopack Portable.zip not found in ${OUT_DIR}" >&2
  exit 1
fi
# The DMG ships this app (it carries UpdateMac). ditto keeps the xattr signatures on non-Mach-O files.
rm -rf "${PACKED_DIR}"
mkdir -p "${PACKED_DIR}"
ditto -x -k "${portable}" "${PACKED_DIR}"
rm -f "${portable}"

# vpk download left the previous full package here only as the delta base; it is already in its own release.
find "${OUT_DIR}" -maxdepth 1 -name '*.nupkg' ! -name "OpenUtau.Chiaki-${PACK_VERSION}-*" -delete
if ! ls "${OUT_DIR}"/*-full.nupkg >/dev/null 2>&1; then
  echo "Full package for ${PACK_VERSION} not found in ${OUT_DIR}" >&2
  exit 1
fi

# assets.*.json still lists Portable.zip, which is not shipped.
python3 - "${OUT_DIR}" <<'PY'
import json, os, sys
d = sys.argv[1]
names = sorted(os.listdir(d))
assets = [
    {"RelativeFileName": n, "Type": "Full" if n.endswith("-full.nupkg") else "Delta"}
    for n in names if n.endswith("-full.nupkg") or n.endswith("-delta.nupkg")
]
for n in names:
    if n.startswith("assets.") and n.endswith(".json"):
        with open(os.path.join(d, n), "w") as f:
            json.dump(assets, f, separators=(",", ":"))
PY
