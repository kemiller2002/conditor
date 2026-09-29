#!/usr/bin/env bash
# Cross-repository clean-room rehearsal of the Echelon installation inventory.
#
#   blank target -> Conditor workstation plan -> install Praxis + Ordo
#   -> register installations with Project Administration
#   -> governed work -> execution envelope -> bounded step -> receipt
#   -> verify -> query Project Administration
#   -> upgrade Praxis (history retained, projection updated)
#   -> uninstall (history retained, state removed)
#
# Everything happens in a sacrificial temporary directory: a temporary
# workstation home, a temporary target repository, and a temporary clone of
# Project Administration acting as the inventory store. Nothing touches the
# real home, repositories or remote inventory.
#
# Required environment:
#   CONDITOR        conditor executable (a file path)
#   PRAXIS          praxis executable supporting `execution` and `installation`
#   ADMINISTRATION  Project Administration `administration` executable
#   PA_SOURCE       Project Administration checkout/URL to clone as the store
#   CONDITOR_VERSION the version of the Conditor build being rehearsed
# Optional:
#   ARTIFACT_MIRROR directory laid out as <tag>/<asset> (else GitHub downloads)
#   KEEP=1          keep the work directory
set -euo pipefail

: "${CONDITOR:?}" "${PRAXIS:?}" "${ADMINISTRATION:?}" "${PA_SOURCE:?}"
WORK="$(mktemp -d)"
[[ "${KEEP:-0}" == 1 ]] || trap 'rm -rf "$WORK"' EXIT
HOME_WS="$WORK/home"
TARGET="$WORK/example"
STORE="$WORK/project-administration"
TARGET_ID="ws-cleanroom"
mkdir -p "$HOME_WS"
mirror=()
[[ -n "${ARTIFACT_MIRROR:-}" ]] && mirror=(--artifact-mirror "$ARTIFACT_MIRROR")

step() { printf '\n==> %s\n' "$*"; }
admin() { "$ADMINISTRATION" installation "$@" --store "$STORE"; }

step "Project Administration inventory store (temporary clone of $PA_SOURCE)"
git clone -q "$PA_SOURCE" "$STORE"
admin project --check

cat > "$WORK/administration.json" <<JSON
{ "schema": "echelon.administration/v1", "provider": "project-administration", "required": true,
  "environment": { "id": "$TARGET_ID" },
  "transport": { "kind": "local", "command": ["$ADMINISTRATION"], "store": "$STORE" } }
JSON
export ECHELON_ADMINISTRATION_CONFIG="$WORK/administration.json"

step "Blank target repository"
git init -q -b main "$TARGET"
git -C "$TARGET" config user.email rehearsal@example.test
git -C "$TARGET" config user.name "Clean-room rehearsal"
git -C "$TARGET" remote add origin https://github.com/echelon-foundry/example.git

step "Conditor workstation plan (project profile pinning Praxis 3.5.0 for the later upgrade)"
checksums="$(curl -fsSL https://github.com/kemiller2002/praxis/releases/download/v3.5.0/native-checksums.txt)"
assets="$(printf '%s\n' "$checksums" | awk '{ rid=$2; sub(/^praxis-/, "", rid); sub(/\.tar\.gz$|\.zip$/, "", rid); printf "%s\"%s\": { \"name\": \"%s\", \"sha256\": \"%s\" }", (NR>1?",":""), rid, $2, $1 }')"
cat > "$WORK/project.profile.json" <<JSON
{ "schema": "conditor.workstation-profile/v1", "id": "example-project", "version": 1, "extends": "echelon-engineering",
  "components": [ { "id": "praxis", "version": "3.5.0", "executable": "praxis", "versionProbe": ["--version"],
    "source": { "kind": "github-release", "repository": "kemiller2002/praxis", "tag": "v3.5.0" },
    "assets": { $assets } } ] }
JSON
"$CONDITOR" workstation plan --profile "$WORK/project.profile.json" --home "$HOME_WS"
digest="$("$CONDITOR" workstation plan --profile "$WORK/project.profile.json" --home "$HOME_WS" --json | sed -n 's/.*"digest": "\(sha256:[0-9a-f]*\)".*/\1/p' | head -1)"

step "Install Praxis 3.5.0 and Ordo 1.4.0 (authorized plan $digest); registration follows matching receipts"
"$CONDITOR" workstation apply --profile "$WORK/project.profile.json" --home "$HOME_WS" "${mirror[@]}" \
  --praxis "$PRAXIS" --target-id "$TARGET_ID" --authorize "$digest"
"$HOME_WS/.local/bin/praxis" --version
"$HOME_WS/.local/bin/ordo" --version

step "Register Conditor itself on the workstation"
conditor_version="${CONDITOR_VERSION:?set CONDITOR_VERSION to the Conditor build version (Directory.Build.props)}"
"$PRAXIS" installation register --system conditor --version "$conditor_version" --target-kind environment \
  --source-repository kemiller2002/conditor --distribution source-build --json >/dev/null

step "Initialize the target repository with the installed Praxis and Ordo; register repository installations"
( cd "$TARGET" && "$HOME_WS/.local/bin/praxis" init >/dev/null && "$HOME_WS/.local/bin/ordo" init >/dev/null )
( cd "$TARGET" && "$PRAXIS" installation register --system praxis --version 3.5.0 --distribution github-release --release v3.5.0 --evidence manifest=.echelon/ros.json )
( cd "$TARGET" && "$PRAXIS" installation register --system ordo --version 1.4.0 --distribution github-release --release v1.4.0 --evidence manifest=.echelon/sde.json )
git -C "$TARGET" add -A && git -C "$TARGET" commit -q -m "Initialize governed repository"

step "Governed work, execution envelope, bounded step, receipt, verification"
mkdir -p "$TARGET/tests" "$TARGET/src"
printf 'test -f src/hello.txt\n' > "$TARGET/tests/gate.sh"
git -C "$TARGET" add -A && git -C "$TARGET" commit -q -m "Add acceptance gate"
exe="$(cd "$TARGET" && "$PRAXIS" execution start --work-item WI-1 --role implementation \
  --scope feature:hello --allow 'feature:hello=src/**' --evaluator gate-code=tests/gate.sh --worktree --json \
  | sed -n 's/.*"executionId": "\(EXE-[^"]*\)".*/\1/p' | head -1)"
ws="$(cd "$TARGET" && "$PRAXIS" execution show "$exe" --json | sed -n 's/.*"path": "\([^"]*\)".*/\1/p' | head -1)"
( cd "$TARGET" && "$PRAXIS" execution step declare "$exe" --step write-hello --expect-command "test -f src/hello.txt" )
mkdir -p "$TARGET/$ws/src" && echo hello > "$TARGET/$ws/src/hello.txt"
( cd "$TARGET" && "$PRAXIS" execution step run "$exe" --step write-hello )
git -C "$TARGET/$ws" add -A && git -C "$TARGET/$ws" commit -q -m "Say hello (Praxis-Execution: $exe)"
( cd "$TARGET" && "$PRAXIS" execution evaluate "$exe" --command "sh tests/gate.sh" )
( cd "$TARGET" && "$PRAXIS" execution transition "$exe" --action complete )
( cd "$TARGET" && "$PRAXIS" execution cleanup "$exe" )
( cd "$TARGET" && "$PRAXIS" execution show "$exe" )

step "Project Administration: what is installed where"
admin query --target repository:echelon-foundry/example
admin query --target "environment:$TARGET_ID"

step "Upgrade Praxis 3.5.0 -> 3.6.0 (workstation profile echelon-engineering, and the repository)"
digest="$("$CONDITOR" workstation plan --profile echelon-engineering --home "$HOME_WS" --json | sed -n 's/.*"digest": "\(sha256:[0-9a-f]*\)".*/\1/p' | head -1)"
"$CONDITOR" workstation apply --profile echelon-engineering --home "$HOME_WS" "${mirror[@]}" \
  --praxis "$PRAXIS" --target-id "$TARGET_ID" --authorize "$digest"
"$HOME_WS/.local/bin/praxis" --version
( cd "$TARGET" && "$HOME_WS/.local/bin/praxis" upgrade >/dev/null && "$PRAXIS" installation register --system praxis --version 3.6.0 --distribution github-release --release v3.6.0 --evidence manifest=.echelon/ros.json )
admin history --target repository:echelon-foundry/example --system praxis
admin history --target "environment:$TARGET_ID" --system praxis
admin query --target repository:echelon-foundry/example

step "Uninstall the workstation (removal registered after matching removal receipts)"
"$CONDITOR" uninstall --plan --home "$HOME_WS"
digest="$("$CONDITOR" uninstall --plan --home "$HOME_WS" | sed -n 's/.*--authorize \(sha256:[0-9a-f]*\).*/\1/p')"
"$CONDITOR" uninstall --home "$HOME_WS" --profile echelon-engineering --praxis "$PRAXIS" --target-id "$TARGET_ID" --authorize "$digest"
admin query --target "environment:$TARGET_ID"
admin history --target "environment:$TARGET_ID"
admin project --check
echo
echo "installation events recorded: $(ls "$STORE/installations/events" | grep -c '^IE-')"
echo "Clean-room rehearsal complete."
