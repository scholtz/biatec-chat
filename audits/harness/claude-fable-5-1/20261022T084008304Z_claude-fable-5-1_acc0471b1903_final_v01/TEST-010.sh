#!/usr/bin/env bash
# TEST-010 (BCHAT-006) Mock OIDC test helpers under /mock/* must be gated by an explicit environment flag and must not
# return device bearer tokens. Fails while app.ts serves /mock/devices/{kid} with device_token and no flag.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
f=packages/consent-protocol/mock-oidc/src/app.ts
ggrep -qE "MOCK_TEST_HELPERS|MOCK_HELPERS" -- "$f" packages/consent-protocol/mock-oidc/src/server.ts || fail TEST-010 "no environment gate for /mock/* helpers (BCHAT-006 open)"
pass TEST-010 "mock helpers are gated by an environment flag; verify manually that device_token is not returned"
