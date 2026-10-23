#!/usr/bin/env bash
# Offline test of preflight.sh against a fake kubectl (no cluster). Run: k8s/rbac/test-preflight.sh
set -uo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
mkdir "$TMP/bin"
cat > "$TMP/bin/kubectl" <<'STUB'
#!/usr/bin/env bash
case "$*" in
  "config view"*) printf '%s' "${FAKE_TOKEN:-}" ;;
  "auth can-i list secrets"*) echo "${FAKE_SECRETS:-no}" ;;
  "auth can-i create rolebindings"*) echo "${FAKE_RBAC:-no}" ;;
  "auth can-i get secrets/"*|"auth can-i delete deployments"*|"auth can-i create pods --subresource=exec"*) echo "${FAKE_EDIT:-no}" ;;
esac
STUB
chmod +x "$TMP/bin/kubectl"
export PATH="$TMP/bin:$PATH" NAMESPACE=biatec-chat-stage
fail=0
check() { if eval "$2"; then echo "ok: $1"; else echo "FAIL: $1"; fail=1; fi; }
b64() { printf '%s' "$1" | base64 -w0 | tr '+/' '-_' | tr -d '='; }
jwt() { echo "$(b64 '{"alg":"RS256"}').$(b64 "$1").sig"; }
now=$(date -u +%s)

FAKE_TOKEN="$(jwt "{\"exp\":$((now + 30 * 86400))}")" "$DIR/preflight.sh" >"$TMP/ok.out" 2>&1; rc_ok=$?
FAKE_TOKEN="$(jwt "{\"exp\":$((now + 2 * 86400))}")" "$DIR/preflight.sh" >"$TMP/soon.out" 2>&1; rc_soon=$?
FAKE_TOKEN="$(jwt "{\"exp\":$((now - 3600))}")" "$DIR/preflight.sh" >"$TMP/expired.out" 2>&1; rc_expired=$?
FAKE_TOKEN="$(jwt '{"sub":"x"}')" "$DIR/preflight.sh" >"$TMP/never.out" 2>&1; rc_never=$?
FAKE_TOKEN="opaque-token" "$DIR/preflight.sh" >"$TMP/opaque.out" 2>&1; rc_opaque=$?
FAKE_TOKEN="" "$DIR/preflight.sh" >"$TMP/none.out" 2>&1; rc_none=$?
FAKE_TOKEN="$(jwt "{\"exp\":$((now + 30 * 86400))}")" FAKE_SECRETS=yes FAKE_RBAC=yes "$DIR/preflight.sh" >"$TMP/wide.out" 2>&1; rc_wide=$?
FAKE_TOKEN="$(jwt "{\"exp\":$((now + 30 * 86400))}")" FAKE_EDIT=yes "$DIR/preflight.sh" >"$TMP/edit.out" 2>&1
FAKE_TOKEN="$(jwt "{\"exp\":$((now + 30 * 86400))}")" FAKE_RBAC=yes STRICT_IDENTITY=1 "$DIR/preflight.sh" >"$TMP/strict.out" 2>&1; rc_strict=$?
FAKE_TOKEN="$(jwt "{\"exp\":$((now + 30 * 86400))}")" STRICT_IDENTITY=1 "$DIR/preflight.sh" >"$TMP/strict-ok.out" 2>&1; rc_strict_ok=$?
NAMESPACE= "$DIR/preflight.sh" >/dev/null 2>&1; rc_ns=$?

check "a token valid for 30 days passes quietly" '[ $rc_ok -eq 0 ] && ! grep -q "::warning" "$TMP/ok.out" && grep -q "valid until" "$TMP/ok.out"'
check "a token expiring within 7 days warns and passes" '[ $rc_soon -eq 0 ] && grep -q "::warning::.*within 7 days" "$TMP/soon.out"'
check "an expired token fails with a clear message" '[ $rc_expired -eq 1 ] && grep -q "::error::.*expired on" "$TMP/expired.out"'
check "a token without exp warns" '[ $rc_never -eq 0 ] && grep -q "::warning::.*no expiry" "$TMP/never.out"'
check "an opaque token warns instead of failing" '[ $rc_opaque -eq 0 ] && grep -q "::warning::.*no expiry" "$TMP/opaque.out"'
check "no token is only a notice" '[ $rc_none -eq 0 ] && grep -q "::notice::" "$TMP/none.out"'
check "an identity that reads all secrets and edits RBAC is reported" '[ $rc_wide -eq 0 ] && grep -q "read every secret" "$TMP/wide.out" && grep -q "edit RBAC" "$TMP/wide.out"'
check "an edit-like identity (arbitrary secrets, delete, exec) is reported" 'grep -q "arbitrary secrets, delete workloads or exec" "$TMP/edit.out"'
check "a scoped identity gets no rights warning" '! grep -q "every secret\|edit RBAC\|arbitrary secrets" "$TMP/ok.out"'
check "STRICT_IDENTITY=1 fails a wide identity and passes a scoped one" '[ $rc_strict -eq 1 ] && [ $rc_strict_ok -eq 0 ]'
check "a missing NAMESPACE is an error" '[ $rc_ns -ne 0 ]'
exit $fail
