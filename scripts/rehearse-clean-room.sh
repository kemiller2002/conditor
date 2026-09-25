#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TARGET="${1:-$(mktemp -d)}"
cleanup=0
if [[ $# -eq 0 ]]; then
  cleanup=1
fi

finish() {
  if [[ "$cleanup" -eq 1 && -d "$TARGET" ]]; then
    rm -rf "$TARGET"
  fi
}
trap finish EXIT

mkdir -p "$TARGET"
git -C "$TARGET" init -q

pushd "$ROOT" >/dev/null

dotnet run --project src/Conditor.Cli --configuration Release -- plan --preset evidence-triage-rehearsal --target "$TARGET"

dotnet run --project src/Conditor.Cli --configuration Release -- init --preset evidence-triage-rehearsal --target "$TARGET"

popd >/dev/null

snapshot_tree() {
  find "$TARGET" -type f ! -path "$TARGET/.git/*" -print0 \
    | sort -z \
    | while IFS= read -r -d '' file; do
        relative="${file#"$TARGET"/}"
        printf '%s  %s\n' "$(sha256sum "$file" | awk '{print $1}')" "$relative"
      done
}

FIRST_SNAPSHOT="$(mktemp)"
SECOND_SNAPSHOT="$(mktemp)"
trap 'rm -f "$FIRST_SNAPSHOT" "$SECOND_SNAPSHOT"; finish' EXIT

snapshot_tree > "$FIRST_SNAPSHOT"

pushd "$ROOT" >/dev/null
dotnet run --project src/Conditor.Cli --configuration Release -- init \
  --preset evidence-triage-rehearsal \
  --target "$TARGET"
popd >/dev/null

snapshot_tree > "$SECOND_SNAPSHOT"

if ! diff -u "$FIRST_SNAPSHOT" "$SECOND_SNAPSHOT"; then
  echo "Second Conditor initialization produced repository drift." >&2
  exit 1
fi

test -f "$TARGET/.conditor/lock.json"
test -f "$TARGET/AGENTS.md"
test -f "$TARGET/kickoff/evidence-triage.kickoff.json"
test -f "$TARGET/kickoff/completion-evidence.schema.json"
test -f "$TARGET/src/engine/App.Engine.fsproj"
test -f "$TARGET/src/engine/Domain.fs"
test -f "$TARGET/src/kernel/package.json"
test -f "$TARGET/src/kernel/bootstrap.ts"

grep -F "kickoff/evidence-triage.kickoff.json" "$TARGET/AGENTS.md"
grep -F "Build the Incident Evidence Triage Board" "$TARGET/AGENTS.md"
grep -F "@echelon-foundry/design-system" "$TARGET/src/kernel/package.json"
grep -F "@echelon-foundry/print-components" "$TARGET/src/kernel/package.json"
grep -F "EchelonFoundry.Aegis.Core" "$TARGET/src/engine/App.Engine.fsproj"

if find "$TARGET" -maxdepth 3 -type f \( -name 'App.fs' -o -name 'Program.fs' \) | grep . >/dev/null 2>&1; then
  echo "Unexpected application implementation detected in bootstrap output." >&2
  exit 1
fi

unexpected_index="$(
  find "$TARGET" -maxdepth 3 -type f -name 'index.html' ! -path "$TARGET/src/kernel/index.html" -print -quit
)"
if [[ -n "$unexpected_index" ]]; then
  echo "Unexpected application index detected in bootstrap output: $unexpected_index" >&2
  exit 1
fi

grep -F 'export const scaffoldReady = true as const;' "$TARGET/src/kernel/bootstrap.ts"
grep -F '<ef-button><button type="button">Ready</button></ef-button>' "$TARGET/src/kernel/index.html"

echo "Conditor clean-room rehearsal bootstrap passed: $TARGET"
