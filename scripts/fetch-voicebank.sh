#!/usr/bin/env bash
# Puts the bundled voicebank into Singers/<name> before building.
#   scripts/fetch-voicebank.sh              download the release asset named in scripts/voicebank.json
#   scripts/fetch-voicebank.sh --from DIR   copy a local voicebank folder instead (no download)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
manifest="$root/scripts/voicebank.json"
cache="$root/.cache/voicebank"

py="$(command -v python3 || command -v python || true)"
if [ -z "$py" ]; then
  echo "python3 is required" >&2
  exit 1
fi

field() {
  "$py" -c 'import json,sys; print(json.load(open(sys.argv[1]))[sys.argv[2]])' "$manifest" "$1"
}

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

name="$(field name)"
tag="$(field tag)"
asset="$(field asset)"
expected="$(field sha256)"
dest="$root/Singers/$name"
marker="$cache/$name.sha256"

if [ "${1:-}" = "--from" ]; then
  src="${2:?--from needs a folder}"
  mkdir -p "$dest"
  rsync -a --delete --exclude '.DS_Store' "${src%/}/" "$dest/"
  rm -f "$marker"
  echo "Copied $src to $dest"
  exit 0
fi

if [ -d "$dest" ] && [ -f "$marker" ] && [ "$(cat "$marker")" = "$expected" ]; then
  echo "$name voicebank is up to date"
  exit 0
fi

mkdir -p "$cache"
zip="$cache/$asset"
if [ ! -f "$zip" ] || [ "$(sha256 "$zip")" != "$expected" ]; then
  rm -f "$zip"
  gh release download "$tag" --pattern "$asset" --dir "$cache" --repo chiakich/OpenUtau-chiaki
fi

actual="$(sha256 "$zip")"
if [ "$actual" != "$expected" ]; then
  echo "SHA256 mismatch for $asset: expected $expected, got $actual" >&2
  exit 1
fi

rm -rf "$dest"
mkdir -p "$root/Singers"
"$py" -m zipfile -e "$zip" "$root/Singers"
echo "$expected" > "$marker"
echo "Extracted $asset to $dest"
