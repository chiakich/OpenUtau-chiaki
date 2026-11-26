#!/usr/bin/env bash
# Usage: make-mcpb.sh <version> [out.mcpb]
# Packs the Claude Desktop extension that starts the MCP server of an installed app.
set -euo pipefail

version="${1:?version}"
out="${2:-OpenUtau-Chiaki.mcpb}"
root="$(cd "$(dirname "$0")/.." && pwd)"
src="$root/OpenUtau.Cli/mcpb"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

mkdir -p "$work/server"
cp "$src/server/launcher.js" "$work/server/"
# The manifest takes plain SemVer only.
VERSION="${version%%[-+]*}" node -e '
  const fs = require("fs");
  const m = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
  m.version = process.env.VERSION;
  fs.writeFileSync(process.argv[2], JSON.stringify(m, null, 2) + "\n");
' "$src/manifest.json" "$work/manifest.json"

case "$out" in /*) ;; *) out="$PWD/$out" ;; esac
rm -f "$out"
(cd "$work" && zip -q -r "$out" manifest.json server)
echo "$out"
