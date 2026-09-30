#!/usr/bin/env sh
set -eu

REPOSITORY="${CONDITOR_REPOSITORY:-kemiller2002/conditor}"
VERSION="${CONDITOR_VERSION:-latest}"
INSTALL_DIR="${CONDITOR_INSTALL_DIR:-$HOME/.local/bin}"
NO_PATH_UPDATE="${CONDITOR_NO_PATH_UPDATE:-0}"
RELEASE_BASE="${CONDITOR_RELEASE_BASE:-}"

os="$(uname -s)"
arch="$(uname -m)"

case "$os" in
  Linux)
    if [ -f /etc/alpine-release ] || (command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -qi musl); then
      os_id="linux-musl"
    else
      os_id="linux"
    fi
    ;;
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

if [ -n "$RELEASE_BASE" ]; then
  base="$RELEASE_BASE"
elif [ "$VERSION" = "latest" ]; then
  base="https://github.com/$REPOSITORY/releases/latest/download"
else
  case "$VERSION" in
    v*) tag="$VERSION" ;;
    *) tag="v$VERSION" ;;
  esac
  base="https://github.com/$REPOSITORY/releases/download/$tag"
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "curl is required to bootstrap Conditor." >&2
  exit 3
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
destination="$INSTALL_DIR/conditor"
staged="$INSTALL_DIR/.conditor.new.$$"
cp "$binary" "$staged"
chmod +x "$staged"
mv -f "$staged" "$destination"

echo "Installed Conditor to $destination"

path_begin="# >>> conditor:path >>>"
path_end="# <<< conditor:path <<<"

choose_profile() {
  case "${SHELL:-}" in
    */zsh)
      if [ -n "${ZDOTDIR:-}" ]; then printf '%s/.zprofile\n' "$ZDOTDIR"; else printf '%s/.zprofile\n' "$HOME"; fi
      ;;
    */bash)
      if [ -f "$HOME/.bash_profile" ]; then printf '%s/.bash_profile\n' "$HOME"; else printf '%s/.profile\n' "$HOME"; fi
      ;;
    *) printf '%s/.profile\n' "$HOME" ;;
  esac
}

persist_path() {
  rc="$1"
  mkdir -p "$(dirname "$rc")"
  [ -f "$rc" ] || : > "$rc"

  clean="$tmp/profile.clean"
  awk -v begin="$path_begin" -v end="$path_end" '
    $0 == begin { skipping=1; next }
    $0 == end { skipping=0; next }
    !skipping { print }
  ' "$rc" > "$clean"

  {
    cat "$clean"
    if [ -s "$clean" ] && [ "$(tail -c 1 "$clean" 2>/dev/null || true)" != "" ]; then
      printf '\n'
    fi
    printf '%s\n' "$path_begin"
    printf 'export PATH="%s:$PATH"\n' "$INSTALL_DIR"
    printf '%s\n' "$path_end"
  } > "$rc"
}

case ":$PATH:" in
  *":$INSTALL_DIR:"*) ;;
  *)
    if [ "$NO_PATH_UPDATE" = "1" ]; then
      echo "Add $INSTALL_DIR to PATH to run 'conditor' directly."
    else
      rc="$(choose_profile)"
      persist_path "$rc"
      echo "Added $INSTALL_DIR to PATH for future shells via $rc"
    fi
    ;;
esac

if [ "$#" -gt 0 ]; then
  echo "Running installed Conditor: $*"
  exec "$destination" "$@"
fi
