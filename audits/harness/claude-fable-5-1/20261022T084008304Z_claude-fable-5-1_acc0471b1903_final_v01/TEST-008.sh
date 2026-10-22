#!/usr/bin/env bash
# TEST-008 (BCHAT-001) Liquidity groups from the builder sidecar must be verified field by field like swaps and
# transfers: the liquidity builder/verifier must refuse RekeyTo and CloseRemainderTo. Static check: fails while
# no rekey/close-out check exists under Tools/Dex/Liquidity. Replace with the unit test described in the finding once fixed.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
hits=$(ggrep -nE "RekeyTo|CloseRemainderTo|AssetCloseTo" -- backend/Biatec.Chat.Tools/Dex/Liquidity backend/Biatec.Chat.Api/Dex/Liquidity | grep -v '^$' || true)
[ -n "$hits" ] || fail TEST-008 "no rekey/close-out verification on the liquidity builder path (BCHAT-001 open)"
echo "$hits"; pass TEST-008 "liquidity path references rekey/close-out checks; confirm with the hostile-builder unit test"
