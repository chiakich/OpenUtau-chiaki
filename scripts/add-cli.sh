#!/usr/bin/env bash
# Puts the command line (OpenUtau.Cli) next to a published app, so both share one runtime and one set of libraries.
# usage: add-cli.sh <rid> <app-dir>   (app-dir: the publish folder, or one holding the macOS .app)
set -euo pipefail

RID="${1:?usage: add-cli.sh <rid> <app-dir>}"
DIR="${2:?usage: add-cli.sh <rid> <app-dir>}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-dotnet}"

app="$(find "$DIR" -maxdepth 2 -name '*.app' -type d | head -n 1)"
if [ -n "$app" ]; then
  DIR="$app/Contents/MacOS"
fi
if [ ! -f "$DIR/OpenUtau.Core.dll" ]; then
  echo "No published app in $DIR" >&2
  exit 1
fi

out="${CLI_PUBLISH_DIR:-$root/bin/cli-$RID}"
# shellcheck disable=SC2086
"$dotnet" publish "$root/OpenUtau.Cli/OpenUtau.Cli.csproj" -c Release -r "$RID" --self-contained true \
  -p:PublishReadyToRun=true -o "$out" ${CLI_PUBLISH_ARGS:-}

# Only what the app does not have: its own copies are the ones it was built and tested with.
added=0
while IFS= read -r -d '' file; do
  rel="${file#"$out"/}"
  if [ ! -e "$DIR/$rel" ]; then
    mkdir -p "$(dirname "$DIR/$rel")"
    cp -p "$file" "$DIR/$rel"
    echo "added $rel"
    added=$((added + 1))
  fi
done < <(find "$out" -type f -print0)
echo "Added $added file(s) to $DIR"
ls "$DIR"/OpenUtau.Cli* > /dev/null
