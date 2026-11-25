#!/usr/bin/env bash
# Runs OpenUtau.Cli from this checkout, building it first when it has not been built yet.
# The SDK need not be on PATH: ~/.dotnet is tried too, or name it with DOTNET=/path/to/dotnet.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
dotnet="${DOTNET:-$(command -v dotnet || true)}"
if [ -z "$dotnet" ] && [ -x "$HOME/.dotnet/dotnet" ]; then
  dotnet="$HOME/.dotnet/dotnet"
fi
if [ -z "$dotnet" ]; then
  echo '{"ok":false,"error":{"code":"dotnet_not_found","message":"The .NET SDK was not found. Put dotnet on PATH or set DOTNET."}}' >&2
  exit 1
fi

config="${OPENUTAU_CLI_CONFIG:-Debug}"
dll="$root/OpenUtau.Cli/bin/$config/net10.0/OpenUtau.Cli.dll"
if [ ! -f "$dll" ]; then
  "$dotnet" build "$root/OpenUtau.Cli/OpenUtau.Cli.csproj" -c "$config" -v q -nologo >&2
fi
exec "$dotnet" "$dll" "$@"
