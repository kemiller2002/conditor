#!/usr/bin/env sh
set -eu

REPOSITORY="kemiller2002/conditor"
VERSION="${CONDITOR_VERSION:-latest}"
INSTALL_DIR="${CONDITOR_INSTALL_DIR:-$HOME/.local/bin}"

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux) os_id="linux" ;;
  Darwin) os_id="osx" ;;
  *)
    echo "Unsupported operating system: $os" >&2
    exit 2
    ;;
esac

case "$arch" in
  x86_64|amd64) arch_id="x64" ;;
  arm64|aarch64) arch_id="arm64" ;;
  *)
    echo "Unsupported architecture: $arch" >&2
    exit 2
    ;;
esac

rid="$os_id-$arch_id"
asset="conditor-$rid"

if [ "$VERSION" = "latest" ]; then
  base="https://github.com/$REPOSITORY/releases/latest/download"
else
  case "$VERSION" in
    v*) tag="$VERSION" ;;
    *) tag="v$VERSION" ;;
  esac
  base="https://github.com/$REPOSITORY/releases/download/$tag"
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM
binary="$tmp/$asset"
checksums="$tmp/checksums.txt"

curl -fsSL "$base/$asset" -o "$binary"
curl -fsSL "$base/checksums.txt" -o "$checksums"

expected="$(awk -v name="$asset" '$2 == name { print $1 }' "$checksums")"
if [ -z "$expected" ]; then
  echo "No checksum published for $asset" >&2
  exit 3
fi

if command -v sha256sum >/dev/null 2>&1; then
  actual="$(sha256sum "$binary" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
  actual="$(shasum -a 256 "$binary" | awk '{print $1}')"
else
  echo "Neither sha256sum nor shasum is available." >&2
  exit 3
fi

if [ "$actual" != "$expected" ]; then
  echo "Checksum verification failed for $asset" >&2
  exit 3
fi

mkdir -p "$INSTALL_DIR"
chmod +x "$binary"
mv "$binary" "$INSTALL_DIR/conditor"

echo "Installed Conditor to $INSTALL_DIR/conditor"

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *) echo "Add $INSTALL_DIR to PATH to run 'conditor' directly." ;;
esac
