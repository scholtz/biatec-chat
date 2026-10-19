#!/usr/bin/env bash
# Offline test of teardown.sh / create.sh against a fake `aws` on PATH (no AWS access). Run: infra/aws/test-scripts.sh
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
mkdir "$TMP/bin"
cat > "$TMP/bin/aws" <<'STUB'
#!/usr/bin/env bash
echo "aws $*" >> "$FAKE_LOG"
case "$*" in
  *get-resources*) printf 'arn:aws:ec2:eu-central-1:1:instance/i-1\tarn:aws:ec2:eu-central-1:1:volume/vol-1\tarn:aws:kms:eu-central-1:1:key/k-1\n' ;;
  *describe-volumes*) echo "An error occurred (InvalidVolume.NotFound)" >&2; exit 254 ;;
  *describe-key*) echo "PendingDeletion" ;;
  *list-role-tags*) exit 254 ;;
  *run-instances*)
    n=$(cat "$FAKE_STATE" 2>/dev/null || echo 0); echo $((n+1)) > "$FAKE_STATE"
    if [ "$n" -lt 1 ]; then echo "An error occurred (InvalidParameterValue): Invalid IAM Instance Profile name" >&2; exit 254; fi ;;
  *iam\ delete-*|*iam\ remove-*|*iam\ detach-*|*kms\ delete-alias*) echo "An error occurred (NoSuchEntity) when calling" >&2; exit 254 ;;
  *sts*) echo 123456789012 ;;
  *create-key*) echo key-1 ;;
  *describe-security-groups*) echo sg-1 ;;
  *get-parameter*) echo ami-1 ;;
esac
exit 0
STUB
chmod +x "$TMP/bin/aws"
# sleep stub keeps the retry test fast
printf '#!/usr/bin/env bash\nexit 0\n' > "$TMP/bin/sleep"; chmod +x "$TMP/bin/sleep"
export PATH="$TMP/bin:$PATH" FAKE_LOG="$TMP/log" FAKE_STATE="$TMP/state" BIATEC_AWS_APPROVED=yes
fail=0
check() { if eval "$2"; then echo "ok: $1"; else echo "FAIL: $1"; fail=1; fi; }

: > "$FAKE_LOG"
"$DIR/teardown.sh" --apply >"$TMP/td.out" 2>&1; td_rc=$?
check "teardown survives gone volume, IAM, alias, pending key" '[ $td_rc -eq 0 ]'
check "teardown skipped the missing volume" 'grep -q "vol-1 no longer exists" "$TMP/td.out"'
check "teardown never deleted the missing volume" '! grep -q "delete-volume" "$FAKE_LOG"'
check "teardown still reached delete-role" 'grep -q "iam delete-role --role-name" "$FAKE_LOG"'
check "teardown skipped key pending deletion" '! grep -q schedule-key-deletion "$FAKE_LOG"'

: > "$FAKE_LOG"; rm -f "$FAKE_STATE"
"$DIR/create.sh" --apply >"$TMP/cr.out" 2>&1; cr_rc=$?
check "create retries run-instances after profile error" '[ $cr_rc -eq 0 ] && [ "$(grep -c "ec2 run-instances" "$FAKE_LOG")" -eq 2 ]'
check "create waits for the instance profile" 'grep -q "iam wait instance-profile-exists" "$FAKE_LOG"'
exit $fail
