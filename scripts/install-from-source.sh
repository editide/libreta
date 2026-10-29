#!/usr/bin/env bash
# Builds Libreta in place and links it onto PATH. A Debug build serves wwwroot from source, so only changes to the C#
# need a rebuild.
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
command_directory="${LIBRETA_BIN_DIR:-${HOME}/.local/bin}"

if command -v dotnet >/dev/null; then
  dotnet_command="$(command -v dotnet)"
elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  dotnet_command="${HOME}/.dotnet/dotnet"
else
  echo "The .NET 10 SDK was not found on PATH or at ~/.dotnet/dotnet; see https://dot.net/download." >&2
  exit 1
fi

"${dotnet_command}" build "${repository}/src/Libreta" --configuration Debug
mkdir -p "${command_directory}"
ln -sfn "${repository}/src/Libreta/bin/Debug/net10.0/Libreta" "${command_directory}/libreta"
"${command_directory}/libreta" --help >/dev/null

echo
echo "Installed ${command_directory}/libreta"
if [[ ":${PATH}:" != *":${command_directory}:"* ]]; then
  echo "Add ${command_directory} to PATH to run libreta from any directory."
fi
