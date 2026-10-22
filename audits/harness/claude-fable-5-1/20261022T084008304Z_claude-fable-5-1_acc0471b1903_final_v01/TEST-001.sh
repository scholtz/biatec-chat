#!/usr/bin/env bash
# TEST-001 Backend test suite is green at the audited commit (baseline reproduction, M9).
# Needs Redis and PostgreSQL (docker compose up -d redis postgres) and the .NET 10 SDK.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
out=$(cd "$ROOT" && dotnet test backend/Biatec.Chat.slnx --nologo -v q 2>&1) || { echo "$out" | tail -20; fail TEST-001 "dotnet test exited non-zero"; }
echo "$out" | grep -qE "Failed:\s+0," || { echo "$out" | tail -5; fail TEST-001 "backend tests failed"; }
pass TEST-001 "$(echo "$out" | grep -oE 'Passed:\s+[0-9]+, Skipped:\s+[0-9]+, Total:\s+[0-9]+')"
