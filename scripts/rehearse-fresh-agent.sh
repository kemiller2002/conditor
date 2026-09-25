#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="${1:-$(mktemp -d -t conditor-fresh-agent.XXXXXX)}"
LAUNCHER="${2:-codex}"

case "$LAUNCHER" in
  codex|claude) ;;
  *)
    echo "Unsupported launcher: $LAUNCHER (expected codex or claude)" >&2
    exit 2
    ;;
esac

if [[ -d "$TARGET" ]] && find "$TARGET" -mindepth 1 -maxdepth 1 ! -name .git -print -quit | grep . >/dev/null 2>&1; then
  echo "Fresh-agent rehearsal target must be empty except for an optional .git directory: $TARGET" >&2
  exit 2
fi

mkdir -p "$TARGET"

if [[ ! -d "$TARGET/.git" ]]; then
  git -C "$TARGET" init -q
fi

if command -v conditor >/dev/null 2>&1; then
  CONDITOR=(conditor)
else
  CONDITOR=(dotnet run --project "$ROOT/src/Conditor.Cli/Conditor.Cli.fsproj" --configuration Release --)
fi

echo "Fresh-agent target: $TARGET"
echo "Launcher: $LAUNCHER"

"${CONDITOR[@]}" start \
  --preset evidence-triage-rehearsal \
  --launcher "$LAUNCHER" \
  --target "$TARGET"

test -f "$TARGET/.ros/context/current.json"
test -f "$TARGET/.conditor/lock.json"
test -f "$TARGET/conditor.json"

if ! grep -F '"id": "COND-MISSION-001"' "$TARGET/.ros/context/current.json" >/dev/null; then
  echo "Praxis mission is missing from live work context." >&2
  exit 3
fi

if ! grep -F '"semanticState": "complete"' "$TARGET/.ros/context/current.json" >/dev/null; then
  echo "Fresh agent returned without completing the Praxis mission." >&2
  echo "Repository retained for inspection: $TARGET" >&2
  exit 4
fi

node "$TARGET/ros" validate

echo "Fresh-agent rehearsal passed: $TARGET"
