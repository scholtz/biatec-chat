#!/usr/bin/env bash
# Shared helpers for the audit harness. Source this file; do not run it.
# ROOT is the repository root: BIATEC_ROOT if set, else four levels above this folder
# (audits/harness/<auditor-tag>/<report-basename>/). Every test aborts if ROOT is not a git checkout.
set -u
ROOT="${BIATEC_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../.." && pwd)}"
pass() { echo "PASS $1: $2"; }
fail() { echo "FAIL $1: $2"; exit 1; }
git -C "$ROOT" rev-parse --verify HEAD >/dev/null 2>&1 || fail "${TEST_ID:-harness}" "ROOT=$ROOT is not a git checkout; set BIATEC_ROOT"
# ggrep: git grep that distinguishes "no match" (ok) from an error (abort). Usage: ggrep <args...>
ggrep() { local out; out=$(git -C "$ROOT" grep "$@" 2>&1); local rc=$?; [ $rc -le 1 ] || { echo "$out"; fail "${TEST_ID:-harness}" "git grep error"; }; printf "%s" "$out"; return $rc; }
