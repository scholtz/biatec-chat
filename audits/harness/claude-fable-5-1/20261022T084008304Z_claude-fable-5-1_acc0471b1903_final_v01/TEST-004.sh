#!/usr/bin/env bash
# TEST-004 npm dependency advisories. Expected at the audited commit: critical only in apps/demo dev
# tooling (tinypool via vitest); high only through builders/sidecar -> biatec-concentrated-liquidity-amm
# (parse-duration, serialize-javascript, undici, sharp, nanoid). Any critical/high outside these paths fails.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
out=$(cd "$ROOT" && pnpm audit --audit-level high 2>&1 | sed 's/\x1b\[[0-9;]*m//g')
echo "$out" | grep -E "^│ (critical|high|Paths)" | sed 's/ \{2,\}/ /g'
bad=$(echo "$out" | grep -E "^│ Paths" | grep -vE "apps__demo>vitest>tinypool|builders__sidecar>biatec-concentrated-liquidity-" || true)
[ -z "$bad" ] || { echo "$bad"; fail TEST-004 "critical/high advisory on an unexpected path"; }
pass TEST-004 "critical/high advisories limited to the recorded paths (still open findings, see report)"
