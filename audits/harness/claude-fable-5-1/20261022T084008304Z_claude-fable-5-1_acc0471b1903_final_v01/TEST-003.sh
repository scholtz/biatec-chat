#!/usr/bin/env bash
# TEST-003 .NET dependency advisories: the only known reachable advisory at the audited commit is
# MessagePack 3.1.8 (GHSA-qhrr-8q5h-9q3h, moderate). Any other advisory, or a higher severity, fails.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
out=$(cd "$ROOT" && dotnet list backend/Biatec.Chat.slnx package --vulnerable --include-transitive 2>&1)
rows=$(echo "$out" | grep -E "^\s+>" | sort -u)
echo "$rows"
echo "$rows" | grep -vqE "^\s*$|MessagePack\s+3\.1\.8\s+Moderate" && fail TEST-003 "new or changed .NET advisory"
echo "$out" | grep -qiE "High|Critical" && fail TEST-003 "high or critical .NET advisory present"
pass TEST-003 "only the recorded MessagePack moderate advisory"
