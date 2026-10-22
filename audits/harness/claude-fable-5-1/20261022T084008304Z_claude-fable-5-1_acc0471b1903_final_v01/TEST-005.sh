#!/usr/bin/env bash
# TEST-005 Secret pattern scan over the tracked tree. Fails on any hit not in the allow-list below.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
hits=$(ggrep -nIE "AKIA[0-9A-Z]{16}|-----BEGIN [A-Z ]*PRIVATE KEY|ghp_[A-Za-z0-9]{36}|xox[bp]-[A-Za-z0-9-]+|sk-[A-Za-z0-9]{32,}|(password|passwd|secret|api[_-]?key)\s*[:=]\s*['\"][^'\"]{8,}" -- . ':!pnpm-lock.yaml' ':!**/*.lock' ':!audits/**')
# Allow-list reviewed in cycle 1: env-var references in workflows ("$VAR"), test fixtures, mock OIDC seed clients.
allow='="\$[A-Z_]+"|Biatec.Chat.Tests/|/test/|/tests/|mock-oidc/src/seed.ts|example|placeholder|changeme|<redacted>'
real=$(printf '%s\n' "$hits" | grep -vE "$allow" | grep -v '^$' || true)
[ -z "$real" ] || { echo "$real"; fail TEST-005 "possible secret outside the allow-list"; }
pass TEST-005 "no secret-pattern hits outside the reviewed allow-list ($(printf '%s\n' "$hits" | grep -c .) allow-listed hits)"
