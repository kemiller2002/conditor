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

set +e
"${CONDITOR[@]}" start \
  --preset evidence-triage-rehearsal \
  --launcher "$LAUNCHER" \
  --target "$TARGET"
start_code=$?
set -e

set +e
bash "$ROOT/scripts/score-fresh-agent-rehearsal.sh" "$TARGET"
score_code=$?
set -e

if [[ "$start_code" -ne 0 ]]; then
  echo "Conditor/provider execution failed with exit code $start_code." >&2
  echo "Repository and independent score retained for inspection: $TARGET" >&2
  exit "$start_code"
fi

if [[ "$score_code" -ne 0 ]]; then
  echo "Fresh agent returned, but independent rehearsal acceptance failed." >&2
  echo "Repository retained for inspection: $TARGET" >&2
  exit "$score_code"
fi

echo "Fresh-agent rehearsal passed: $TARGET"
