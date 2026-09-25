#!/usr/bin/env bash
set -euo pipefail

TARGET="${1:?usage: score-fresh-agent-rehearsal.sh <target>}"
EVIDENCE="$TARGET/rehearsal/completion-evidence.json"
SCORE="$TARGET/rehearsal/score.json"

failures=()
owners=()

record_failure() {
  failures+=("$1")
  owners+=("$2")
}

if [[ ! -f "$EVIDENCE" ]]; then
  record_failure "Missing rehearsal/completion-evidence.json" "factory-context"
else
  python3 - "$TARGET" "$EVIDENCE" <<'PY'
import json, os, pathlib, sys

target = pathlib.Path(sys.argv[1]).resolve()
evidence_path = pathlib.Path(sys.argv[2])
data = json.loads(evidence_path.read_text())

required = {
    "clean-build",
    "domain-tests",
    "blocked-transition",
    "responsive-workspace",
    "provider-boundary",
    "ai-authority-boundary",
    "report-generation",
}
items = data.get("evidence", [])
ids = [item.get("id") for item in items]

missing = sorted(required - set(ids))
duplicates = sorted({x for x in ids if ids.count(x) > 1})

errors = []
if data.get("schemaVersion") != 1:
    errors.append("schemaVersion must be 1")
if data.get("rehearsal") != "evidence-triage":
    errors.append("rehearsal must be evidence-triage")
if missing:
    errors.append("missing evidence ids: " + ", ".join(missing))
if duplicates:
    errors.append("duplicate evidence ids: " + ", ".join(duplicates))

for item in items:
    status = item.get("status")
    if status != "verified":
        errors.append(f"{item.get('id')}: status is {status!r}, expected 'verified'")
    paths = item.get("paths") or []
    if not paths:
        errors.append(f"{item.get('id')}: verified evidence has no paths")
    for rel in paths:
        full = (target / rel).resolve()
        try:
            full.relative_to(target)
        except ValueError:
            errors.append(f"{item.get('id')}: evidence path escapes target: {rel}")
            continue
        if not full.exists():
            errors.append(f"{item.get('id')}: evidence path does not exist: {rel}")

if errors:
    for error in errors:
        print(error)
    sys.exit(20)
PY
  if [[ $? -ne 0 ]]; then
    record_failure "Completion evidence is incomplete, unverified, or references missing paths" "proof"
  fi
fi

if [[ -f "$TARGET/.ros/context/current.json" ]]; then
  if ! grep -F '"semanticState": "complete"' "$TARGET/.ros/context/current.json" >/dev/null; then
    record_failure "Praxis mission is not complete" "ros"
  fi
else
  record_failure "Missing live ROS context" "ros"
fi

if [[ -f "$TARGET/ros" ]]; then
  if ! node "$TARGET/ros" validate >/tmp/conditor-rehearsal-ros.log 2>&1; then
    record_failure "ROS validation failed" "ros"
  fi
else
  record_failure "Missing repository ROS launcher" "ros"
fi

solution=""
if [[ -f "$TARGET/App.slnx" ]]; then
  solution="$TARGET/App.slnx"
else
  solution="$(find "$TARGET" -maxdepth 2 -type f \( -name '*.slnx' -o -name '*.sln' \) -print -quit || true)"
fi

if [[ -z "$solution" ]]; then
  record_failure "No .NET solution found" "conditor"
elif ! dotnet build "$solution" --configuration Release >/tmp/conditor-rehearsal-build.log 2>&1; then
  record_failure "Solution build failed" "conditor"
fi

mapfile -t test_projects < <(find "$TARGET" -type f \( -iname '*test*.fsproj' -o -iname '*tests*.fsproj' \) | sort)
if [[ "${#test_projects[@]}" -eq 0 ]]; then
  record_failure "No F# test project found" "ordo"
else
  for project in "${test_projects[@]}"; do
    if ! dotnet test "$project" --configuration Release >/tmp/conditor-rehearsal-tests.log 2>&1; then
      record_failure "Domain/application tests failed: ${project#$TARGET/}" "ordo"
      break
    fi
  done
fi

mapfile -t domain_files < <(find "$TARGET/src" -type f -iname '*Domain*.fs' 2>/dev/null | sort)
if [[ "${#domain_files[@]}" -eq 0 ]]; then
  record_failure "No F# domain source file found" "ordo"
else
  if grep -nEi '\b(GitHub|Octokit|GITHUB_TOKEN)\b' "${domain_files[@]}" >/tmp/conditor-rehearsal-provider-leaks.log 2>&1; then
    record_failure "Provider-specific GitHub concepts leaked into Domain" "architecture"
  fi
fi

mkdir -p "$TARGET/rehearsal"
python3 - "$SCORE" "${failures[@]}" -- "${owners[@]}" <<'PY'
import json, sys
score_path = sys.argv[1]
args = sys.argv[2:]
sep = args.index("--") if "--" in args else len(args)
failures = args[:sep]
owners = args[sep+1:] if sep < len(args) else []
result = {
    "schemaVersion": 1,
    "rehearsal": "evidence-triage",
    "passed": len(failures) == 0,
    "failureCount": len(failures),
    "failures": [
        {"message": msg, "owner": owners[i] if i < len(owners) else "unknown"}
        for i, msg in enumerate(failures)
    ],
}
with open(score_path, "w") as f:
    json.dump(result, f, indent=2)
    f.write("\n")
print(json.dumps(result, indent=2))
PY

if [[ "${#failures[@]}" -gt 0 ]]; then
  echo "Fresh-agent rehearsal score failed. See $SCORE" >&2
  exit 5
fi

echo "Fresh-agent rehearsal score passed: $SCORE"
