#!/usr/bin/env bash
# Runs every TEST-###.sh in this folder and prints a summary. Exit code is the number of failed tests.
# Usage: BIATEC_ROOT=<repo root> bash run-all.sh      (BIATEC_ROOT is optional inside audits/harness/<tag>/<report>/)
cd "$(dirname "$0")" || exit 99
failed=0; total=0
for t in TEST-*.sh; do
  total=$((total+1))
  if bash "$t"; then :; else failed=$((failed+1)); fi
done
echo "---"
echo "harness: $((total-failed))/$total passed, $failed failed"
exit $failed
