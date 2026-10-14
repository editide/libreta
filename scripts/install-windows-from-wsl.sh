#!/usr/bin/env bash
# From WSL, publishes a self-contained build for Windows, for Windows apps that can't reach WSL.
# Usage: scripts/install-windows-from-wsl.sh [windows-folder], defaulting to %USERPROFILE%\dev\libreta.
set -euo pipefail

repository="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

if command -v dotnet >/dev/null; then
  dotnet_command="$(command -v dotnet)"
elif [[ -x "${HOME}/.dotnet/dotnet" ]]; then
  dotnet_command="${HOME}/.dotnet/dotnet"
else
  echo "The .NET 10 SDK was not found on PATH or at ~/.dotnet/dotnet; see https://dot.net/download." >&2
  exit 1
fi

if [[ $# -gt 0 ]]; then
  target="$(wslpath -u "$1")"
else
  target="$(wslpath -u "$(cmd.exe /c 'echo %USERPROFILE%' 2>/dev/null | tr -d '\r')")/dev/libreta"
fi

"${dotnet_command}" publish "${repository}/src/Libreta" --configuration Release --runtime win-x64 --self-contained \
  --output "${target}"
"${target}/Libreta.exe" --help >/dev/null

echo
echo "Installed $(wslpath -w "${target}")\\Libreta.exe"
echo "Add $(wslpath -w "${target}") to the Windows PATH to run libreta from any directory."
