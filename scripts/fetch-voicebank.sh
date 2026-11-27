#!/usr/bin/env bash
# Puts the bundled voicebank into Singers/<name> before building.
#   scripts/fetch-voicebank.sh              download the Hugging Face files named in scripts/voicebank.json
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
  "$py" -c 'import json,sys
v = json.load(open(sys.argv[1]))
for k in sys.argv[2].split("."): v = v[k]
print(v)' "$manifest" "$1"
}

# download REPO REVISION FILE OUT
download() {
  curl -fL --retry 3 --retry-delay 5 -o "$4" "https://huggingface.co/$1/resolve/$2/$3"
}

sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

name="$(field name)"
repo="$(field repo)"
revision="$(field revision)"
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

voc_asset="$(field vocoder.asset)"
voc_expected="$(field vocoder.sha256)"
voc_dest="$dest/$(field vocoder.path)"
stamp="$expected $voc_expected"

if [ -d "$dest" ] && [ -f "$marker" ] && [ "$(cat "$marker")" = "$stamp" ]; then
  echo "$name voicebank is up to date"
  exit 0
fi

mkdir -p "$cache"
zip="$cache/$asset"
if [ ! -f "$zip" ] || [ "$(sha256 "$zip")" != "$expected" ]; then
  rm -f "$zip"
  download "$repo" "$revision" "$asset" "$zip"
fi

actual="$(sha256 "$zip")"
if [ "$actual" != "$expected" ]; then
  echo "SHA256 mismatch for $asset: expected $expected, got $actual" >&2
  exit 1
fi

rm -rf "$dest"
mkdir -p "$root/Singers"
"$py" -m zipfile -e "$zip" "$root/Singers"
echo "Extracted $asset to $dest"

# The vocoder is pinned to its own repo; the zip normally carries the same file already.
if [ ! -f "$voc_dest" ] || [ "$(sha256 "$voc_dest")" != "$voc_expected" ]; then
  voc="$cache/$voc_asset"
  if [ ! -f "$voc" ] || [ "$(sha256 "$voc")" != "$voc_expected" ]; then
    rm -f "$voc"
    download "$(field vocoder.repo)" "$(field vocoder.revision)" "$voc_asset" "$voc"
  fi
  actual="$(sha256 "$voc")"
  if [ "$actual" != "$voc_expected" ]; then
    echo "SHA256 mismatch for $voc_asset: expected $voc_expected, got $actual" >&2
    exit 1
  fi
  mkdir -p "$(dirname "$voc_dest")"
  cp "$voc" "$voc_dest"
  echo "Placed $voc_asset from $(field vocoder.repo)"
fi
echo "$stamp" > "$marker"
