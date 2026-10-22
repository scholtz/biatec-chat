#!/usr/bin/env bash
# TEST-011 (BCHAT-012) The token vault must refuse to start with the Dev key provider outside Development
# (mirroring the evidence key store). Fails while VaultRegistration only logs a warning.
TEST_ID=$(basename "$0" .sh); source "$(dirname "$0")/lib.sh"
f=backend/Biatec.Chat.Api/Vault/VaultRegistration.cs
if ggrep -qE "LogWarning\(\"Vault uses the local Dev key provider outside Development" -- "$f"; then
  fail TEST-011 "VaultRegistration still only warns for Dev provider outside Development (BCHAT-012 open)"
fi
ggrep -qE "AllowEphemeralKeys|throw new InvalidOperationException" -- "$f" || fail TEST-011 "no fail-closed gate found in VaultRegistration"
pass TEST-011 "vault refuses Dev provider outside Development"
