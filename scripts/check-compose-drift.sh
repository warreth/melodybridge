#!/usr/bin/env bash
# Fails when the production compose in this repo has drifted from the
# copy served at https://melodybridge.app/compose.yml (repo
# warreth/melodybridge.app). Docs and landing page tell users to wget
# that file, so the two must stay identical.
#
# Usage: scripts/check-compose-drift.sh [url]
#   url - optional override, defaults to the live landing site. Point it
#         at a local checkout to test before the app repo is pushed:
#         scripts/check-compose-drift.sh file://$(pwd)/../melodybridge.app/compose.yml
set -euo pipefail

URL="${1:-https://melodybridge.app/compose.yml}"
LOCAL="compose.yml"
TMP="$(mktemp)"
trap 'rm -f "$TMP"' EXIT

echo "Fetching $URL ..."
if ! curl -fsSL "$URL" -o "$TMP"; then
  echo "FAIL: could not fetch $URL" >&2
  echo "If the app repo has not been pushed yet, pass the local copy:" >&2
  echo "  scripts/check-compose-drift.sh file://$(pwd)/../melodybridge.app/compose.yml" >&2
  exit 1
fi

if diff -u "$LOCAL" "$TMP"; then
  echo "OK: compose.yml matches the published copy"
else
  echo "FAIL: compose.yml has drifted from the published copy at $URL" >&2
  echo "Copy this repo's compose.yml into warreth/melodybridge.app and push." >&2
  exit 1
fi
