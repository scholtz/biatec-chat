#!/usr/bin/env bash
# TEST-012 (BCHAT-015) The production overlay must set Evidence__PublicBaseUrl so evidence bundles advertise an https JWKS URL.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
ggrep -qE "Evidence__PublicBaseUrl:\s*\"?https://" -- k8s/main || fail TEST-012 "k8s/main has no https Evidence__PublicBaseUrl (BCHAT-015 open)"
pass TEST-012 "production overlay sets Evidence__PublicBaseUrl"
