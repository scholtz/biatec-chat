#!/usr/bin/env bash
# Lets the backend pods on a Kubernetes cluster (EKS or not) reach Bedrock without access keys: registers the
# cluster's service-account issuer as an IAM OIDC provider and creates role biatec-chat-k8s-role, assumable only by the
# biatec-chat-backend service account through sts:AssumeRoleWithWebIdentity. See k8s/README.md "AWS access".
# Nothing here bills while idle. DEFAULT IS DRY-RUN:
#   ISSUER_URL=https://<issuer> BIATEC_AWS_APPROVED=yes ./k8s-role.sh --apply
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"

ISSUER_URL="${ISSUER_URL:?set ISSUER_URL to the cluster service-account issuer (https://...)}"
NAMESPACES="${NAMESPACES:-biatec-chat-main biatec-chat-stage}"
SERVICE_ACCOUNT="$NAME_PREFIX-backend"
ROLE="$NAME_PREFIX-k8s-role"
case "$ISSUER_URL" in https://*) ;; *) echo "ISSUER_URL must start with https://" >&2; exit 1 ;; esac
ISSUER_HOST="${ISSUER_URL#https://}"; ISSUER_HOST="${ISSUER_HOST%/}"

mode_banner
# AWS fetches the issuer's discovery document and JWKS itself; fail early if they aren't public.
if ! curl -fsS "https://$ISSUER_HOST/.well-known/openid-configuration" >/dev/null; then
  echo "https://$ISSUER_HOST/.well-known/openid-configuration is not reachable; AWS can't validate the pod tokens." >&2; exit 1
fi

if [ "$APPLY" = 1 ]; then
  ACCOUNT_ID="$(aws_ sts get-caller-identity --query Account --output text)"
else
  ACCOUNT_ID="${ACCOUNT_ID:-111111111111}"  # placeholder in dry-run
fi
PROVIDER_ARN="arn:aws:iam::$ACCOUNT_ID:oidc-provider/$ISSUER_HOST"

SUBS="$(for ns in $NAMESPACES; do printf '"system:serviceaccount:%s:%s",' "$ns" "$SERVICE_ACCOUNT"; done)"
TRUST_FILE="$(mktemp)"; POLICY_FILE="$(mktemp)"
trap 'rm -f "$TRUST_FILE" "$POLICY_FILE"' EXIT
cat > "$TRUST_FILE" <<JSON
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": {"Federated": "$PROVIDER_ARN"},
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": {"$ISSUER_HOST:aud": "sts.amazonaws.com"},
      "ForAnyValue:StringEquals": {"$ISSUER_HOST:sub": [${SUBS%,}]}
    }
  }]
}
JSON
sed -e "s|\${ACCOUNT_ID}|$ACCOUNT_ID|g" "$DIR/bedrock-role-policy.json" > "$POLICY_FILE"

if [ "$APPLY" = 1 ] && aws_ iam get-open-id-connect-provider --open-id-connect-provider-arn "$PROVIDER_ARN" >/dev/null 2>&1; then
  echo "OIDC provider $PROVIDER_ARN already exists; making sure it accepts the sts.amazonaws.com audience."
  # Adding a client id the provider already lists is a no-op.
  run_aws iam add-client-id-to-open-id-connect-provider --open-id-connect-provider-arn "$PROVIDER_ARN" --client-id sts.amazonaws.com
else
  run_aws iam create-open-id-connect-provider --url "https://$ISSUER_HOST" --client-id-list sts.amazonaws.com \
    --tags "Key=$TAG_KEY,Value=$TAG_VALUE"
fi
# Re-running updates the trust and the policy (new namespaces, another key) instead of stopping on EntityAlreadyExists.
if [ "$APPLY" = 1 ] && aws_ iam get-role --role-name "$ROLE" >/dev/null 2>&1; then
  run_aws iam update-assume-role-policy --role-name "$ROLE" --policy-document "file://$TRUST_FILE"
else
  run_aws iam create-role --role-name "$ROLE" --assume-role-policy-document "file://$TRUST_FILE" \
    --max-session-duration 3600 --tags "Key=$TAG_KEY,Value=$TAG_VALUE"
fi
run_aws iam put-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock" --policy-document "file://$POLICY_FILE"

echo "Put this in the aws-role-arn key of biatec-chat-secrets: arn:aws:iam::$ACCOUNT_ID:role/$ROLE"
