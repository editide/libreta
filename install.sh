#!/bin/sh
# Installs the latest Libreta release, which bundles its own runtime:
#   curl -fsSL https://raw.githubusercontent.com/editide/libreta/main/install.sh | sh
set -eu

release_url="${LIBRETA_RELEASE_URL:-https://github.com/editide/libreta/releases/latest/download}"
application_directory="${LIBRETA_INSTALL_DIR:-${HOME}/.local/lib/libreta}"
command_directory="${LIBRETA_BIN_DIR:-${HOME}/.local/bin}"

case "$(uname -s)" in
  Linux) platform=linux ;;
  Darwin) platform=osx ;;
  *) echo "No Libreta release for $(uname -s); on Windows, use install.ps1." >&2; exit 1 ;;
esac
case "$(uname -m)" in
  x86_64 | amd64) architecture=x64 ;;
  arm64 | aarch64) architecture=arm64 ;;
  *) echo "No Libreta release for $(uname -m) processors." >&2; exit 1 ;;
esac

# The directory is replaced wholesale, so refuse one that does not already hold Libreta.
if [ -e "${application_directory}" ] && [ ! -e "${application_directory}/libreta" ] \
  && [ ! -e "${application_directory}/Libreta" ]; then
  echo "${application_directory} exists and does not hold Libreta; set LIBRETA_INSTALL_DIR elsewhere." >&2
  exit 1
fi

archive="libreta-${platform}-${architecture}.tar.gz"
temporary="$(mktemp -d)"
trap 'rm -rf "${temporary}"' EXIT
echo "Downloading ${release_url}/${archive}"
curl -fsSL "${release_url}/${archive}" -o "${temporary}/${archive}"
tar -xzf "${temporary}/${archive}" -C "${temporary}"

mkdir -p "$(dirname "${application_directory}")" "${command_directory}"
rm -rf "${application_directory}"
mv "${temporary}/libreta" "${application_directory}"
if [ "${platform}" = osx ]; then
  xattr -dr com.apple.quarantine "${application_directory}" 2>/dev/null || true
fi
ln -sfn "${application_directory}/libreta" "${command_directory}/libreta"
"${command_directory}/libreta" --help >/dev/null

echo
echo "Installed ${command_directory}/libreta"
case ":${PATH}:" in
  *":${command_directory}:"*) ;;
  *)
    echo
    echo "${command_directory} is not on PATH. Add this line to your shell configuration, then open a new shell:"
    echo "  export PATH=\"${command_directory}:\$PATH\""
    ;;
esac
