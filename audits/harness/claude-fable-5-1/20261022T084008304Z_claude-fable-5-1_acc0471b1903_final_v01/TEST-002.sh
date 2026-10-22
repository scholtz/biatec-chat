#!/usr/bin/env bash
# TEST-002 Package test suites: consent-protocol, mock-oidc, evidence-verifier, builder sidecar all green;
# chat-widget has 5 recorded baseline failures in the DEX host playground tests (not security tests).
# A different failure count in the widget is a deviation and must be investigated.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
cd "$ROOT" || exit 1
for p in packages/consent-protocol packages/consent-protocol/mock-oidc packages/evidence-verifier builders/sidecar; do
  out=$(cd "$p" && pnpm test 2>&1 | sed 's/\x1b\[[0-9;]*m//g')
  if echo "$out" | grep -qE "fail [1-9]|[1-9][0-9]* failed"; then echo "$out" | tail -15; fail TEST-002 "$p has failing tests"; fi
done
w=$(cd packages/chat-widget && pnpm test 2>&1 | sed 's/\x1b\[[0-9;]*m//g' | grep -E "^\s*Tests " )
echo "$w" | grep -qE "5 failed" || { echo "$w"; fail TEST-002 "chat-widget failure count differs from the recorded baseline of 5"; }
pass TEST-002 "consent-protocol, mock-oidc, evidence-verifier, sidecar green; widget at recorded baseline ($w)"
