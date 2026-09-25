#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: write-github-oidc-token.sh <audience> <absolute-output-path>" >&2
  exit 2
fi

audience="$1"
output_path="$2"

if [[ "$output_path" != /* ]]; then
  echo "OIDC token output path must be absolute." >&2
  exit 2
fi

: "${ACTIONS_ID_TOKEN_REQUEST_URL:?GitHub Actions OIDC request URL is unavailable; grant id-token: write}"
: "${ACTIONS_ID_TOKEN_REQUEST_TOKEN:?GitHub Actions OIDC request token is unavailable; grant id-token: write}"

output_dir="$(dirname "$output_path")"
mkdir -p "$output_dir"
chmod 700 "$output_dir"

encoded_audience="$(jq -rn --arg audience "$audience" '$audience | @uri')"
temporary="$output_path.tmp.$$"

cleanup() {
  rm -f "$temporary"
}
trap cleanup EXIT

curl --fail-with-body --silent --show-error \
  -H "Authorization: bearer $ACTIONS_ID_TOKEN_REQUEST_TOKEN" \
  "${ACTIONS_ID_TOKEN_REQUEST_URL}&audience=${encoded_audience}" \
  | jq -er '.value | select(type == "string" and length > 0)' > "$temporary"

chmod 600 "$temporary"
mv -f "$temporary" "$output_path"
trap - EXIT
