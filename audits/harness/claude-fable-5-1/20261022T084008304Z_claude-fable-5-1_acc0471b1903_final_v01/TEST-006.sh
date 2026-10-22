#!/usr/bin/env bash
# TEST-006 Invariant: the only non-test, non-comment references to MCP signTransaction are the blocked-name
# constant and its IsBlocked check in McpTypes.cs; no local private-key signing primitive exists in backend or sidecar.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
hits=$(ggrep -nE "signTransaction|SignTransaction" -- backend builders ':!**/*Tests*/**' ':!**/test/**' ':!**/*.md' | grep -vE ':\s*///|//.*[sS]ignTransaction' | grep -v '^$' || true)
unexpected=$(printf '%s\n' "$hits" | grep -vE '^backend/Biatec.Chat.Tools/Mcp/McpTypes.cs:[0-9]+:\s*(public const string SignTransaction = "signTransaction";|public static bool IsBlocked)' | grep -v '^$' || true)
[ -z "$unexpected" ] || { echo "$unexpected"; fail TEST-006 "signTransaction referenced outside the blocked-name constant"; }
keys=$(ggrep -nE "mnemonicToSecretKey|secretKeyToMnemonic|\bsignTxn\b|signLogicSigTransaction|Ed25519\.Sign\(|algosdk\.sign" -- backend builders ':!**/*Tests*/**' ':!**/test/**' ':!**/*.md' | grep -v '^$' || true)
[ -z "$keys" ] || { echo "$keys"; fail TEST-006 "local signing primitive present"; }
pass TEST-006 "signTransaction only appears as the blocked name; no local signing primitive"
