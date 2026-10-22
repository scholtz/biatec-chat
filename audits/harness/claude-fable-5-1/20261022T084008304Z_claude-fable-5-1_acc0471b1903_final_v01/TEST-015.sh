#!/usr/bin/env bash
# TEST-015 (BCHAT-003) Swap and x402 group verifiers must bound the transaction fee like the transfer and safe-proposal
# builders do. Fails while neither verifier references Fee.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
for f in backend/Biatec.Chat.Tools/Dex/Swap/SwapGroupVerifier.cs backend/Biatec.Chat.Tools/X402/X402PayService.cs; do
  ggrep -qE "\.Fee\b" -- "$f" || fail TEST-015 "$f has no fee ceiling (BCHAT-003 open)"
done
pass TEST-015 "fee ceilings present on swap and x402 verifiers"
