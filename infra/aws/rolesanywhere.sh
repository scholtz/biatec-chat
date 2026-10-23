#!/usr/bin/env bash
# Lets the backend pods on ANY Kubernetes cluster reach Bedrock without access keys and without the cluster having a
# public service-account issuer: IAM Roles Anywhere. A private CA you own is registered as the trust anchor; each
# environment gets a client certificate from it (CN biatec-chat-<env>); the backend signs with that certificate
# (aws_signing_helper, run by the AWS SDK as credential_process) and receives short-lived credentials for the role
# biatec-chat-bedrock-role, which can only call Bedrock (infra/aws/bedrock-role-policy.json).
#
# Creates: CA + client certificates (local files in $OUT_DIR, NEVER commit), Roles Anywhere trust anchor + profile,
# the IAM role. Nothing here bills while idle. Re-running is safe: existing files and AWS objects are reused, the
# role trust and policy are updated (use it to add an environment, or RENEW=1 to replace client certificates).
# DEFAULT IS DRY-RUN:
#   BIATEC_AWS_APPROVED=yes ./rolesanywhere.sh --apply
# Environment: CERTS_ONLY=1 (local files only, no AWS), ENVIRONMENTS="stage main" (certificate names), OUT_DIR=./rolesanywhere-out, CERT_DAYS=90 (rotate before it lapses: rolesanywhere-expiry.sh and the weekly workflow warn 30 days ahead), RENEW=1.
set -euo pipefail
export MSYS2_ARG_CONV_EXCL="/CN="  # Git Bash on Windows would rewrite the /CN= subject as a path
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"

ENVIRONMENTS="${ENVIRONMENTS:-stage main}"
OUT_DIR="${OUT_DIR:-./rolesanywhere-out}"
CERT_DAYS="${CERT_DAYS:-90}"
ROLE="$NAME_PREFIX-bedrock-role"
ANCHOR_NAME="$NAME_PREFIX"
PROFILE_NAME="$NAME_PREFIX"
TAGS_RA="key=$TAG_KEY,value=$TAG_VALUE"

mode_banner
command -v openssl >/dev/null || { echo "openssl is required." >&2; exit 1; }

# ---- 1. Certificates (local) ------------------------------------------------------------------------------------
# ECDSA P-256. The CA key stays with whoever runs this script (keep it offline); only the CA certificate goes to AWS.
make_ca() {
  mkdir -p "$OUT_DIR"; chmod 700 "$OUT_DIR"
  if [ -f "$OUT_DIR/ca.pem" ] && [ -f "$OUT_DIR/ca.key" ]; then echo "CA exists: $OUT_DIR/ca.pem"; return; fi
  openssl ecparam -name prime256v1 -genkey -noout -out "$OUT_DIR/ca.key"
  chmod 600 "$OUT_DIR/ca.key"
  openssl req -x509 -new -key "$OUT_DIR/ca.key" -sha256 -days 3650 -subj "/CN=$NAME_PREFIX-rolesanywhere-ca" \
    -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out "$OUT_DIR/ca.pem"
  echo "Created CA $OUT_DIR/ca.pem (valid 10 years)"
}
make_client_cert() {
  local env="$1" d="$OUT_DIR/$1"
  mkdir -p "$d"
  if [ -f "$d/certificate.pem" ] && [ "${RENEW:-0}" != 1 ]; then echo "Client certificate exists: $d/certificate.pem (RENEW=1 to replace)"; return; fi
  # Keep the certificate a renewal replaces, so it can still be revoked (rolesanywhere-revoke.sh --cert) if it leaked.
  if [ -f "$d/certificate.pem" ]; then mkdir -p "$d/retired"; cp "$d/certificate.pem" "$d/retired/$(openssl x509 -in "$d/certificate.pem" -noout -serial | sed 's/^serial=//').pem"; fi
  openssl ecparam -name prime256v1 -genkey -noout -out "$d/private-key.pem"
  chmod 600 "$d/private-key.pem"
  openssl req -new -key "$d/private-key.pem" -subj "/CN=$NAME_PREFIX-$env" -out "$d/client.csr"
  printf 'basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=clientAuth\n' > "$d/client.ext"
  openssl x509 -req -in "$d/client.csr" -CA "$OUT_DIR/ca.pem" -CAkey "$OUT_DIR/ca.key" -CAcreateserial -sha256 \
    -days "$CERT_DAYS" -extfile "$d/client.ext" -out "$d/certificate.pem" 2>/dev/null
  rm -f "$d/client.csr" "$d/client.ext"
  echo "Created client certificate $d/certificate.pem for CN=$NAME_PREFIX-$env (valid $CERT_DAYS days)"
}

if [ "$APPLY" = 1 ]; then
  make_ca
  for e in $ENVIRONMENTS; do make_client_cert "$e"; done
else
  echo "[dry-run] would create a CA and a client certificate per environment ($ENVIRONMENTS) in $OUT_DIR"
fi

# CERTS_ONLY=1: stop after the local files (no AWS access needed), e.g. to put AWS_CLIENT_CERT/AWS_CLIENT_KEY into the
# GitHub secrets first. A later full run reuses the same CA and certificates from $OUT_DIR.
if [ "${CERTS_ONLY:-0}" = 1 ]; then echo "CERTS_ONLY=1: stopping before any AWS call."; exit 0; fi

# ---- 2. AWS -----------------------------------------------------------------------------------------------------
if [ "$APPLY" = 1 ]; then ACCOUNT_ID="$(aws_ sts get-caller-identity --query Account --output text)"; else ACCOUNT_ID="${ACCOUNT_ID:-111111111111}"; fi

# Trust anchor: the CA certificate. Looked up by name so a re-run reuses it.
ANCHOR_ARN=""
if [ "$APPLY" = 1 ]; then
  ANCHOR_ARN="$(aws_ rolesanywhere list-trust-anchors --query "trustAnchors[?name=='$ANCHOR_NAME'].trustAnchorArn | [0]" --output text)"
  if [ "$ANCHOR_ARN" = "None" ]; then ANCHOR_ARN=""; fi
fi
if [ -z "$ANCHOR_ARN" ]; then
  if [ "$APPLY" = 1 ]; then
    CA_ONE_LINE="$(awk '{printf "%s\\n", $0}' "$OUT_DIR/ca.pem")"
    SOURCE_JSON="{\"sourceType\":\"CERTIFICATE_BUNDLE\",\"sourceData\":{\"x509CertificateData\":\"$CA_ONE_LINE\"}}"
    echo "+ aws rolesanywhere create-trust-anchor --name $ANCHOR_NAME ..."
    ANCHOR_ARN="$(aws_ rolesanywhere create-trust-anchor --name "$ANCHOR_NAME" --enabled --source "$SOURCE_JSON" \
      --tags "$TAGS_RA" --query trustAnchor.trustAnchorArn --output text)"
  else
    ANCHOR_ARN="arn:aws:rolesanywhere:$AWS_REGION:$ACCOUNT_ID:trust-anchor/PLACEHOLDER"
    echo "[dry-run] aws rolesanywhere create-trust-anchor --name $ANCHOR_NAME --enabled --source <CA certificate> --tags $TAGS_RA"
  fi
else
  echo "Trust anchor $ANCHOR_NAME exists: $ANCHOR_ARN"
fi

# Role: only Roles Anywhere through THIS trust anchor, and only for certificates whose subject CN is one of ours.
CNS="$(for e in $ENVIRONMENTS; do printf '"%s-%s",' "$NAME_PREFIX" "$e"; done)"
TRUST_FILE="$(mktemp)"; POLICY_FILE="$(mktemp)"; trap 'rm -f "$TRUST_FILE" "$POLICY_FILE"' EXIT
# A native Windows AWS CLI (Git Bash) cannot read /tmp paths: hand it the Windows form of the temp files.
fileuri() { if command -v cygpath >/dev/null 2>&1; then echo "file://$(cygpath -m "$1")"; else echo "file://$1"; fi; }
cat > "$TRUST_FILE" <<JSON
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": {"Service": "rolesanywhere.amazonaws.com"},
    "Action": ["sts:AssumeRole", "sts:TagSession", "sts:SetSourceIdentity"],
    "Condition": {
      "ArnEquals": {"aws:SourceArn": "$ANCHOR_ARN"},
      "StringEquals": {"aws:PrincipalTag/x509Subject/CN": [${CNS%,}]}
    }
  }]
}
JSON
sed -e "s|\${ACCOUNT_ID}|$ACCOUNT_ID|g" "$DIR/bedrock-role-policy.json" > "$POLICY_FILE"
if [ "$APPLY" = 1 ] && aws_ iam get-role --role-name "$ROLE" >/dev/null 2>&1; then
  run_aws iam update-assume-role-policy --role-name "$ROLE" --policy-document "$(fileuri "$TRUST_FILE")"
else
  run_aws iam create-role --role-name "$ROLE" --assume-role-policy-document "$(fileuri "$TRUST_FILE")" \
    --max-session-duration 3600 --tags "Key=$TAG_KEY,Value=$TAG_VALUE"
fi
run_aws iam put-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock" --policy-document "$(fileuri "$POLICY_FILE")"
ROLE_ARN="arn:aws:iam::$ACCOUNT_ID:role/$ROLE"

# Profile: which role a certificate may assume, and for how long.
PROFILE_ARN=""
if [ "$APPLY" = 1 ]; then
  PROFILE_ARN="$(aws_ rolesanywhere list-profiles --query "profiles[?name=='$PROFILE_NAME'].profileArn | [0]" --output text)"
  if [ "$PROFILE_ARN" = "None" ]; then PROFILE_ARN=""; fi
fi
if [ -z "$PROFILE_ARN" ]; then
  if [ "$APPLY" = 1 ]; then
    echo "+ aws rolesanywhere create-profile --name $PROFILE_NAME ..."
    PROFILE_ARN="$(aws_ rolesanywhere create-profile --name "$PROFILE_NAME" --enabled --role-arns "$ROLE_ARN" \
      --duration-seconds 3600 --tags "$TAGS_RA" --query profile.profileArn --output text)"
  else
    PROFILE_ARN="arn:aws:rolesanywhere:$AWS_REGION:$ACCOUNT_ID:profile/PLACEHOLDER"
    echo "[dry-run] aws rolesanywhere create-profile --name $PROFILE_NAME --enabled --role-arns $ROLE_ARN --duration-seconds 3600 --tags $TAGS_RA"
  fi
else
  echo "Profile $PROFILE_NAME exists: $PROFILE_ARN"
fi

# ---- 3. What to put where ---------------------------------------------------------------------------------------
cat <<EOT

Done. Put these in the GitHub environment secrets (Stage and Production), see docs/aws-setup.md:
  AWS_ROLE_ARN          $ROLE_ARN
  AWS_TRUST_ANCHOR_ARN  $ANCHOR_ARN
  AWS_PROFILE_ARN       $PROFILE_ARN
  AWS_CLIENT_CERT       contents of $OUT_DIR/<env>/certificate.pem   (stage -> Stage, main -> Production)
  AWS_CLIENT_KEY        contents of $OUT_DIR/<env>/private-key.pem
e.g.  gh secret set AWS_CLIENT_CERT --env Stage < $OUT_DIR/stage/certificate.pem
Keep $OUT_DIR/ca.key offline and out of git; it signs every future client certificate.
EOT
