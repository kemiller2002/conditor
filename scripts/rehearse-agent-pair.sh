#!/usr/bin/env bash
set -u -o pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BASE="${1:-$(mktemp -d -t conditor-agent-pair.XXXXXX)}"
OPENAI_TARGET="$BASE/openai"
CLAUDE_TARGET="$BASE/claude"
SUMMARY="$BASE/comparison.json"

mkdir -p "$BASE"

run_lane() {
  local name="$1"
  local launcher="$2"
  local target="$3"
  local log="$BASE/${name}.log"

  echo "=== Running ${name} lane (${launcher}) ==="
  set +e
  bash "$ROOT/scripts/rehearse-fresh-agent.sh" "$target" "$launcher" >"$log" 2>&1
  local code=$?
  set -e
  echo "$code" > "$BASE/${name}.exit-code"
  echo "${name} exit code: $code"
  return 0
}

set -e
run_lane "openai" "codex" "$OPENAI_TARGET"
run_lane "claude" "claude" "$CLAUDE_TARGET"

python3 - "$ROOT" "$BASE" "$SUMMARY" <<'PY'
import json
import pathlib
import subprocess
import sys

root = pathlib.Path(sys.argv[1])
base = pathlib.Path(sys.argv[2])
summary_path = pathlib.Path(sys.argv[3])

def read_json(path):
    try:
        return json.loads(path.read_text())
    except Exception:
        return None

def lane(name):
    target = base / name
    exit_code_path = base / f"{name}.exit-code"
    exit_code = int(exit_code_path.read_text().strip()) if exit_code_path.exists() else None
    score = read_json(target / "rehearsal" / "score.json")
    evidence = read_json(target / "rehearsal" / "completion-evidence.json")
    return {
        "lane": name,
        "target": str(target),
        "exitCode": exit_code,
        "passed": bool(score and score.get("passed") and exit_code == 0),
        "score": score,
        "completionEvidence": evidence,
        "log": str(base / f"{name}.log"),
    }

try:
    conditor_commit = subprocess.check_output(
        ["git", "-C", str(root), "rev-parse", "HEAD"], text=True
    ).strip()
except Exception:
    conditor_commit = None

result = {
    "schemaVersion": 1,
    "experiment": "fresh-agent-evidence-triage-pair",
    "conditorCommit": conditor_commit,
    "isolation": {
        "sameFactoryCheckout": True,
        "separateTargetRepositories": True,
        "blindToSiblingLane": True,
        "samePreset": "evidence-triage-rehearsal",
        "sameIndependentScorer": True,
    },
    "lanes": [lane("openai"), lane("claude")],
    "interpretationRule": (
        "Compare observable repository evidence, questions/interventions, score failures, "
        "build/test results, churn, elapsed time, and cost when available. Do not infer "
        "an overall winner without a preregistered evaluation rule."
    ),
}

summary_path.write_text(json.dumps(result, indent=2) + "\n")
print(json.dumps(result, indent=2))
PY

echo
echo "Paired fresh-agent rehearsal complete."
echo "Artifacts retained at: $BASE"
echo "Comparison: $SUMMARY"

openai_code="$(cat "$BASE/openai.exit-code")"
claude_code="$(cat "$BASE/claude.exit-code")"

if [[ "$openai_code" -ne 0 || "$claude_code" -ne 0 ]]; then
  echo "At least one lane failed independent rehearsal acceptance." >&2
  exit 6
fi
