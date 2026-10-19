#!/usr/bin/env bash
# Notarizes and staples a signed DMG.
# Credentials: NOTARY_PROFILE (from `xcrun notarytool store-credentials`), or APPLE_ID + APPLE_TEAM_ID + APPLE_APP_PASSWORD.
set -euo pipefail

DMG="${1:?usage: notarize.sh <file.dmg>}"

if [ -n "${NOTARY_PROFILE:-}" ]; then
  auth=(--keychain-profile "$NOTARY_PROFILE")
else
  : "${APPLE_ID:?APPLE_ID is required}" "${APPLE_TEAM_ID:?APPLE_TEAM_ID is required}" "${APPLE_APP_PASSWORD:?APPLE_APP_PASSWORD is required}"
  auth=(--apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD")
fi

result="$(xcrun notarytool submit "$DMG" "${auth[@]}" --wait --timeout 1h --output-format json)" || true
echo "$result"
field() { python3 -c "import json,sys; print(json.loads(sys.argv[1]).get('$1', ''))" "$result"; }
id="$(field id)"
status="$(field status)"

if [ "$status" != "Accepted" ]; then
  echo "Notarization failed with status '$status'." >&2
  [ -n "$id" ] && xcrun notarytool log "$id" "${auth[@]}" || true
  exit 1
fi

xcrun stapler staple "$DMG"
spctl --assess --type open --context context:primary-signature --verbose=2 "$DMG"
