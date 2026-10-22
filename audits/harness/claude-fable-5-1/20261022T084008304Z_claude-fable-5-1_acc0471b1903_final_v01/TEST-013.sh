#!/usr/bin/env bash
# TEST-013 (BCHAT-008) The authenticator's Android and iOS platform folders must be under version control so that
# manifests, entitlements and hardening flags are reviewable. Fails while neither folder is tracked.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
n=$(git -C "$ROOT" ls-files apps/biatec_authenticator/android apps/biatec_authenticator/ios | wc -l)
[ "$n" -gt 0 ] || fail TEST-013 "no android/ or ios/ files tracked for the authenticator (BCHAT-008 open)"
pass TEST-013 "$n platform files tracked"
