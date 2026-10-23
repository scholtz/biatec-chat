#!/usr/bin/env bash
# Revokes ONE environment's Roles Anywhere client certificate with a certificate revocation list (BCHAT-019), without touching the others.
# Disabling the trust anchor or the profile stops everything; a CRL stops just the certificate that leaked.
#
#   BIATEC_AWS_APPROVED=yes infra/aws/rolesanywhere-revoke.sh <environment> --apply     # DEFAULT IS DRY-RUN
#
# Reads $OUT_DIR/ca.pem, $OUT_DIR/ca.key and $OUT_DIR/<environment>/certificate.pem (the files rolesanywhere.sh wrote), records the certificate in
# $OUT_DIR/crl/revoked.index (kept, so earlier revocations stay in every later CRL), signs a CRL with the CA, and imports it into the trust
# anchor ($NAME_PREFIX-crl; created the first time, updated afterwards). Roles Anywhere then refuses the revoked certificate within minutes.
# Afterwards issue a fresh certificate (RENEW=1 rolesanywhere.sh --apply) and replace the two AWS_CLIENT_* secrets and the cluster secret.
# Environment: OUT_DIR=./rolesanywhere-out, CRL_DAYS=365 (how long the CRL is valid; re-run before it lapses or Roles Anywhere stops trusting it).
set -euo pipefail
export MSYS2_ARG_CONV_EXCL="/CN="
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"

ENV_NAME=""
for arg in "$@"; do case "$arg" in --*) ;; *) ENV_NAME="$arg" ;; esac; done
[ -n "$ENV_NAME" ] || { echo "Usage: rolesanywhere-revoke.sh <environment> [--apply]" >&2; exit 2; }
OUT_DIR="${OUT_DIR:-./rolesanywhere-out}"
CRL_DAYS="${CRL_DAYS:-365}"
CERT="$OUT_DIR/$ENV_NAME/certificate.pem"
CRL_NAME="$NAME_PREFIX-crl"
ANCHOR_NAME="$NAME_PREFIX"

mode_banner
command -v openssl >/dev/null || { echo "openssl is required." >&2; exit 1; }
for f in "$OUT_DIR/ca.pem" "$OUT_DIR/ca.key" "$CERT"; do [ -f "$f" ] || { echo "Missing $f (run rolesanywhere.sh first, on the machine that holds $OUT_DIR)." >&2; exit 1; }; done

CRL_DIR="$OUT_DIR/crl"
mkdir -p "$CRL_DIR"
INDEX="$CRL_DIR/revoked.index"
touch "$INDEX"

# openssl ca keeps a database of issued certificates; ours were signed with `openssl x509 -req`, so only revoked ones are recorded.
serial="$(openssl x509 -in "$CERT" -noout -serial | sed 's/^serial=//' | tr 'a-f' 'A-F')"
[ $(( ${#serial} % 2 )) -eq 0 ] || serial="0$serial"
TAB="$(printf '\t')"
if [ "$APPLY" != 1 ]; then
  echo "[dry-run] would revoke certificate $serial of $ENV_NAME: record it in $INDEX, sign a CRL with the CA, and run aws rolesanywhere import-crl --name $CRL_NAME (or update-crl when it exists) on trust anchor $ANCHOR_NAME."
  exit 0
fi
if grep -q "$TAB$serial$TAB" "$INDEX" 2>/dev/null; then
  echo "Certificate $serial of $ENV_NAME is already in the revocation list."
else
  not_after="$(date -u -d "$(openssl x509 -in "$CERT" -noout -enddate | sed 's/^notAfter=//')" +%y%m%d%H%M%SZ 2>/dev/null || true)"
  [ -n "$not_after" ] || not_after="$(date -u -d '+1 year' +%y%m%d%H%M%SZ)"
  subject="$(openssl x509 -in "$CERT" -noout -subject | sed 's/^subject= *//; s/ = /=/g; s#, #/#g; s#^#/#')"
  printf 'R\t%s\t%s,keyCompromise\t%s\tunknown\t%s\n' "$not_after" "$(date -u +%y%m%d%H%M%SZ)" "$serial" "$subject" >> "$INDEX"
  echo "Recorded certificate $serial of $ENV_NAME as revoked."
fi

cp "$INDEX" "$CRL_DIR/index.txt"
[ -f "$CRL_DIR/crlnumber" ] || echo 01 > "$CRL_DIR/crlnumber"
# A native Windows openssl (Git Bash) cannot read /tmp-style paths inside its config file: write the Windows form there.
native() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf '%s' "$1"; fi; }
cat > "$CRL_DIR/openssl.cnf" <<EOF
[ ca ]
default_ca = CA_default
[ CA_default ]
database = $(native "$CRL_DIR/index.txt")
crlnumber = $(native "$CRL_DIR/crlnumber")
default_md = sha256
default_crl_days = $CRL_DAYS
unique_subject = no
EOF
openssl ca -config "$CRL_DIR/openssl.cnf" -gencrl -cert "$OUT_DIR/ca.pem" -keyfile "$OUT_DIR/ca.key" -out "$CRL_DIR/crl.pem" 2>/dev/null
echo "Wrote $CRL_DIR/crl.pem ($(grep -c "$TAB" "$INDEX" || true) revoked certificate(s), valid $CRL_DAYS days)"

if [ "$APPLY" = 1 ]; then
  ANCHOR_ARN="$(aws_ rolesanywhere list-trust-anchors --query "trustAnchors[?name=='$ANCHOR_NAME'].trustAnchorArn | [0]" --output text)"
  [ -n "$ANCHOR_ARN" ] && [ "$ANCHOR_ARN" != "None" ] || { echo "Trust anchor $ANCHOR_NAME not found." >&2; exit 1; }
  CRL_ID="$(aws_ rolesanywhere list-crls --query "crls[?name=='$CRL_NAME'].crlId | [0]" --output text)"
  fileref="fileb://$CRL_DIR/crl.pem"
  if command -v cygpath >/dev/null 2>&1; then fileref="fileb://$(cygpath -m "$CRL_DIR/crl.pem")"; fi
  if [ -z "$CRL_ID" ] || [ "$CRL_ID" = "None" ]; then
    run_aws rolesanywhere import-crl --name "$CRL_NAME" --crl-data "$fileref" --trust-anchor-arn "$ANCHOR_ARN" --enabled --tags "key=$TAG_KEY,value=$TAG_VALUE"
  else
    run_aws rolesanywhere update-crl --crl-id "$CRL_ID" --crl-data "$fileref"
  fi
else
  echo "[dry-run] aws rolesanywhere import-crl --name $CRL_NAME --crl-data <$CRL_DIR/crl.pem> --trust-anchor-arn <$ANCHOR_NAME> --enabled (or update-crl when it exists)"
fi
echo "Next: issue a new certificate (RENEW=1 infra/aws/rolesanywhere.sh --apply) and replace the AWS_CLIENT_CERT / AWS_CLIENT_KEY secrets and the cluster secret of $ENV_NAME."
