#!/usr/bin/env bash
# Runs OpenUtau.Cli from this checkout, building it first when it has not been built yet.
# After changing the sources, run once with OPENUTAU_CLI_REBUILD=1.
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
# An existing build is used as it is, so that runs in progress are not disturbed: OPENUTAU_CLI_REBUILD=1 builds again.
if [ ! -f "$dll" ] || [ "${OPENUTAU_CLI_REBUILD:-}" = "1" ]; then
  if ! "$dotnet" build "$root/OpenUtau.Cli/OpenUtau.Cli.csproj" -c "$config" -v q -nologo >&2; then
    echo '{"ok":false,"error":{"code":"build_failed","message":"Building OpenUtau.Cli failed; the build output is above."}}' >&2
    exit 1
  fi
fi
exec "$dotnet" "$dll" "$@"
