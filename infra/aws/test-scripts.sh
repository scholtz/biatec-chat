#!/usr/bin/env bash
# Offline test of teardown.sh and k8s-role.sh against a fake `aws` on PATH (no AWS access). Run: infra/aws/test-scripts.sh
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
mkdir "$TMP/bin"
cat > "$TMP/bin/aws" <<'STUB'
#!/usr/bin/env bash
echo "aws $*" >> "$FAKE_LOG"
case "$*" in
  *get-resources*) printf 'arn:aws:bedrock:eu-central-1:1:guardrail/g-1\tarn:aws:kms:eu-central-1:1:key/k-1\tarn:aws:s3:::stray\n' ;;
  *describe-key*) echo "PendingDeletion" ;;
  *iam\ delete-*|*kms\ delete-alias*) echo "An error occurred (NoSuchEntity) when calling" >&2; exit 254 ;;
  *get-open-id-connect-provider*|*get-role*) exit 254 ;;
  *sts*) echo 123456789012 ;;
esac
exit 0
STUB
chmod +x "$TMP/bin/aws"
# curl stub: the issuer discovery check in k8s-role.sh
printf '#!/usr/bin/env bash\nexit 0\n' > "$TMP/bin/curl"; chmod +x "$TMP/bin/curl"
export PATH="$TMP/bin:$PATH" FAKE_LOG="$TMP/log" BIATEC_AWS_APPROVED=yes
fail=0
check() { if eval "$2"; then echo "ok: $1"; else echo "FAIL: $1"; fail=1; fi; }

: > "$FAKE_LOG"
"$DIR/teardown.sh" --apply >"$TMP/td.out" 2>&1; td_rc=$?
check "teardown survives a gone role, alias and pending key" '[ $td_rc -eq 0 ]'
check "teardown deletes the guardrail" 'grep -q "bedrock delete-guardrail --guardrail-identifier g-1" "$FAKE_LOG"'
check "teardown still reached delete-role" 'grep -q "iam delete-role --role-name biatec-chat-k8s-role" "$FAKE_LOG"'
check "teardown skipped key pending deletion" '! grep -q schedule-key-deletion "$FAKE_LOG"'
check "teardown reports unknown tagged resources" 'grep -q "arn:aws:s3:::stray" "$TMP/td.out"'

: > "$FAKE_LOG"
ISSUER_URL=https://issuer.example.test "$DIR/k8s-role.sh" --apply >"$TMP/kr.out" 2>&1; kr_rc=$?
check "k8s-role creates provider and role" '[ $kr_rc -eq 0 ] && grep -q "create-open-id-connect-provider" "$FAKE_LOG" && grep -q "iam create-role" "$FAKE_LOG"'
check "k8s-role puts the Bedrock policy" 'grep -q "put-role-policy --role-name biatec-chat-k8s-role --policy-name biatec-chat-bedrock " "$FAKE_LOG"'
check "role policy grants Bedrock only" '! grep -q "kms:" "$DIR/bedrock-role-policy.json"'
exit $fail
