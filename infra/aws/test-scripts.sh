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
  *list-trust-anchors*) if [[ "$*" == *rolesanywhere* ]] && [ -n "${FAKE_ANCHOR:-}" ]; then echo "$FAKE_ANCHOR"; else echo None; fi ;;
  *list-profiles*) echo None ;;
  *list-crls*) echo "${FAKE_CRL:-None}" ;;
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
export MSYS2_ARG_CONV_EXCL="/CN="  # Git Bash on Windows would rewrite the /CN= subject as a path
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

# ---- certificate lifecycle (BCHAT-019) ------------------------------------------------------------------------------------
check "client certificates are valid for 90 days by default" 'end=$(openssl x509 -in "$TMP/ra/stage/certificate.pem" -noout -enddate | sed "s/^notAfter=//"); start=$(openssl x509 -in "$TMP/ra/stage/certificate.pem" -noout -startdate | sed "s/^notBefore=//"); days=$(( ( $(date -u -d "$end" +%s) - $(date -u -d "$start" +%s) ) / 86400 )); [ "$days" -ge 89 ] && [ "$days" -le 91 ]'

# A certificate valid for 10 days and one valid for 200: the expiry check flags the first inside a 30 day window, not the second.
mkdir "$TMP/exp"
for d in 10 200; do
  openssl ecparam -name prime256v1 -genkey -noout -out "$TMP/exp/k$d.pem"
  openssl req -new -key "$TMP/exp/k$d.pem" -subj "/CN=biatec-chat-exp$d" -out "$TMP/exp/c$d.csr"
  openssl x509 -req -in "$TMP/exp/c$d.csr" -CA "$TMP/ra/ca.pem" -CAkey "$TMP/ra/ca.key" -CAcreateserial -sha256 -days "$d" -out "$TMP/exp/c$d.pem" 2>/dev/null
done
"$DIR/rolesanywhere-expiry.sh" --days 30 "$TMP/exp/c10.pem" >"$TMP/e10.out" 2>&1; rc10=$?
"$DIR/rolesanywhere-expiry.sh" --days 30 "$TMP/exp/c200.pem" >"$TMP/e200.out" 2>&1; rc200=$?
"$DIR/rolesanywhere-expiry.sh" --days 5 "$TMP/exp/c10.pem" >/dev/null 2>&1; rc5=$?
"$DIR/rolesanywhere-expiry.sh" --days 30 "$TMP/exp/c200.pem" "$TMP/exp/c10.pem" >/dev/null 2>&1; rcboth=$?
"$DIR/rolesanywhere-expiry.sh" --days 30 "$TMP/exp/missing.pem" >/dev/null 2>&1; rcmissing=$?
"$DIR/rolesanywhere-expiry.sh" --days abc "$TMP/exp/c200.pem" >/dev/null 2>&1; rcbad=$?
check "expiry check flags a certificate inside the window" '[ $rc10 -eq 1 ] && grep -q "within 30 days" "$TMP/e10.out"'
check "expiry check passes a certificate outside the window" '[ $rc200 -eq 0 ] && grep -q "^ok:" "$TMP/e200.out"'
check "expiry check honours the window and any expiring file fails the run" '[ $rc5 -eq 0 ] && [ $rcboth -eq 1 ]'
check "expiry check reports an unreadable file and a bad window as usage errors" '[ $rcmissing -eq 2 ] && [ $rcbad -eq 2 ]'

# Revocation: the CRL lists the revoked environment's serial and not the other's; it is signed by the CA; it is imported, then updated.
: > "$FAKE_LOG"
serial_stage=$(openssl x509 -in "$TMP/ra/stage/certificate.pem" -noout -serial | sed 's/^serial=//')
serial_main=$(openssl x509 -in "$TMP/ra/main/certificate.pem" -noout -serial | sed 's/^serial=//')
FAKE_ANCHOR=arn:aws:rolesanywhere:eu-central-1:123456789012:trust-anchor/ta-1 OUT_DIR="$TMP/ra" "$DIR/rolesanywhere-revoke.sh" stage --apply >"$TMP/rv.out" 2>&1; rv_rc=$?
check "revoke succeeds and imports the CRL into the trust anchor" '[ $rv_rc -eq 0 ] && grep -q "rolesanywhere import-crl --name biatec-chat-crl" "$FAKE_LOG"'
check "the CRL is signed by the CA and lists the stage certificate only" 'openssl crl -in "$TMP/ra/crl/crl.pem" -CAfile "$TMP/ra/ca.pem" -noout 2>/dev/null && openssl crl -in "$TMP/ra/crl/crl.pem" -noout -text | grep -qi "Serial Number: $serial_stage" && ! openssl crl -in "$TMP/ra/crl/crl.pem" -noout -text | grep -qi "Serial Number: $serial_main"'
FAKE_ANCHOR=arn:aws:rolesanywhere:eu-central-1:123456789012:trust-anchor/ta-1 OUT_DIR="$TMP/ra" "$DIR/rolesanywhere-revoke.sh" main --apply >"$TMP/rv2.out" 2>&1
check "a second revocation keeps the first in the CRL" 'openssl crl -in "$TMP/ra/crl/crl.pem" -noout -text | grep -qi "Serial Number: $serial_stage" && openssl crl -in "$TMP/ra/crl/crl.pem" -noout -text | grep -qi "Serial Number: $serial_main"'
check "revoke is a dry run without --apply and refuses a missing certificate" 'OUT_DIR="$TMP/ra" "$DIR/rolesanywhere-revoke.sh" stage >"$TMP/rv3.out" 2>&1 && grep -q "dry-run" "$TMP/rv3.out" && ! OUT_DIR="$TMP/ra" "$DIR/rolesanywhere-revoke.sh" nope >/dev/null 2>&1'
check "expiry check rejects --days without a value instead of looping" 'timeout 10 "$DIR/rolesanywhere-expiry.sh" "$TMP/exp/c200.pem" --days >/dev/null 2>&1; [ $? -eq 2 ]'
check "revoke accepts the GitHub environment name Production for main" 'OUT_DIR="$TMP/ra" "$DIR/rolesanywhere-revoke.sh" Production 2>&1 | grep -q "dry-run"'
check "teardown deletes the CRL" 'grep -q "delete-crl" "$DIR/teardown.sh"'
exit $fail
