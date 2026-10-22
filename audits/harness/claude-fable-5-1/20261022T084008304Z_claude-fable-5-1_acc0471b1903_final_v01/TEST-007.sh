#!/usr/bin/env bash
# TEST-007 Invariant: the widget and wire contracts never name OAuth tokens (browser must never receive them).
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
cd "$ROOT" || exit 1
hits=$(ggrep -nE "access_token|refresh_token|id_token|accessToken|refreshToken|idToken" -- packages/chat-widget/src packages/chat-protocol/src || true)
[ -z "$hits" ] || { echo "$hits"; fail TEST-007 "token field referenced in browser-side code"; }
pass TEST-007 "no OAuth token field in widget or chat-protocol sources"
