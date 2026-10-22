#!/usr/bin/env bash
# TEST-014 (BCHAT-017) Every third-party GitHub Action must be pinned to a full commit SHA. Fails on any @vN tag.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
tags=$(ggrep -hoE "uses: [A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+@v[0-9]+(\.[0-9]+)*" -- .github/workflows | sort -u | grep -v '^$' || true)
[ -z "$tags" ] || { echo "$tags"; fail TEST-014 "actions referenced by mutable tag (BCHAT-017 open)"; }
pass TEST-014 "all actions pinned by commit SHA"
