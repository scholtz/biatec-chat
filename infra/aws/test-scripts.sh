#!/usr/bin/env bash
# Offline test of teardown.sh and rolesanywhere.sh against a fake `aws` on PATH (no AWS access). Run: infra/aws/test-scripts.sh
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
mkdir "$TMP/bin"
cat > "$TMP/bin/aws" <<'STUB'
#!/usr/bin/env bash
echo "aws $*" >> "$FAKE_LOG"
case "$*" in
  *get-resources*) printf 'arn:aws:bedrock:eu-central-1:1:guardrail/g-1\tarn:aws:kms:eu-central-1:1:key/k-1\tarn:aws:s3:::stray\tarn:aws:rolesanywhere:eu-central-1:1:profile/p-1\tarn:aws:rolesanywhere:eu-central-1:1:trust-anchor/ta-1\n' ;;
  *list-trust-anchors*|*list-profiles*) echo None ;;
  *create-trust-anchor*) echo arn:aws:rolesanywhere:eu-central-1:123456789012:trust-anchor/ta-1 ;;
  *create-profile*) echo arn:aws:rolesanywhere:eu-central-1:123456789012:profile/p-1 ;;
  *describe-key*) echo "PendingDeletion" ;;
  *iam\ delete-*|*kms\ delete-alias*) echo "An error occurred (NoSuchEntity) when calling" >&2; exit 254 ;;
  *get-open-id-connect-provider*|*get-role*) exit 254 ;;
  *sts*) echo 123456789012 ;;
esac
exit 0
STUB
chmod +x "$TMP/bin/aws"
export PATH="$TMP/bin:$PATH" FAKE_LOG="$TMP/log" BIATEC_AWS_APPROVED=yes
fail=0
check() { if eval "$2"; then echo "ok: $1"; else echo "FAIL: $1"; fail=1; fi; }

: > "$FAKE_LOG"
"$DIR/teardown.sh" --apply >"$TMP/td.out" 2>&1; td_rc=$?
check "teardown survives a gone role, alias and pending key" '[ $td_rc -eq 0 ]'
check "teardown deletes the guardrail" 'grep -q "bedrock delete-guardrail --guardrail-identifier g-1" "$FAKE_LOG"'
check "teardown deletes the Roles Anywhere profile and trust anchor" 'grep -q "rolesanywhere delete-profile --profile-id p-1" "$FAKE_LOG" && grep -q "rolesanywhere delete-trust-anchor --trust-anchor-id ta-1" "$FAKE_LOG"'
check "teardown still reached delete-role" 'grep -q "iam delete-role --role-name biatec-chat-bedrock-role" "$FAKE_LOG" && grep -q "iam delete-role --role-name biatec-chat-k8s-role" "$FAKE_LOG"'
check "teardown skipped key pending deletion" '! grep -q schedule-key-deletion "$FAKE_LOG"'
check "teardown reports unknown tagged resources" 'grep -q "arn:aws:s3:::stray" "$TMP/td.out"'

: > "$FAKE_LOG"
OUT_DIR="$TMP/ra" "$DIR/rolesanywhere.sh" --apply >"$TMP/ra.out" 2>&1; ra_rc=$?
check "rolesanywhere creates trust anchor, role and profile" '[ $ra_rc -eq 0 ] && grep -q "rolesanywhere create-trust-anchor --name biatec-chat" "$FAKE_LOG" && grep -q "iam create-role --role-name biatec-chat-bedrock-role" "$FAKE_LOG" && grep -q "rolesanywhere create-profile --name biatec-chat" "$FAKE_LOG"'
check "rolesanywhere puts the Bedrock policy" 'grep -q "put-role-policy --role-name biatec-chat-bedrock-role --policy-name biatec-chat-bedrock " "$FAKE_LOG"'
check "client certificates chain to the CA, are not CAs, and carry the CN" 'for e in stage main; do openssl verify -CAfile "$TMP/ra/ca.pem" "$TMP/ra/$e/certificate.pem" >/dev/null && openssl x509 -in "$TMP/ra/$e/certificate.pem" -noout -subject | grep -q "biatec-chat-$e" && openssl x509 -in "$TMP/ra/$e/certificate.pem" -noout -text | grep -q "CA:FALSE" || exit 1; done'
check "rolesanywhere prints the ARNs for the secrets" 'grep -q "AWS_PROFILE_ARN .*profile/p-1" "$TMP/ra.out" && grep -q "AWS_TRUST_ANCHOR_ARN .*trust-anchor/ta-1" "$TMP/ra.out"'
check "role policy grants Bedrock only" '! grep -q "kms:" "$DIR/bedrock-role-policy.json"'
exit $fail
