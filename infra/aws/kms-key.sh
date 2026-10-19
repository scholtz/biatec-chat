#!/usr/bin/env bash
# Creates the token-vault KMS key (alias/biatec-chat-vault, eu-central-1, tag Project=biatec) for the Kubernetes path
# (create.sh makes its own for the EC2 path). Idempotent: if the alias exists, prints that key and changes nothing.
# A KMS key bills while idle (about 1 USD/month), so --apply needs the user's approval:
#   BIATEC_AWS_APPROVED=yes ./kms-key.sh --apply
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"
ALIAS="alias/$NAME_PREFIX-vault"

mode_banner
EXISTING="$(aws_ kms list-aliases --query "Aliases[?AliasName=='$ALIAS'].TargetKeyId | [0]" --output text 2>/dev/null || true)"
if [ -n "$EXISTING" ] && [ "$EXISTING" != "None" ]; then
  KEY_ID="$EXISTING"
  echo "$ALIAS already exists."
elif [ "$APPLY" = 1 ]; then
  KEY_ID="$(aws_ kms create-key --description "Biatec Chat token vault" --tags "TagKey=$TAG_KEY,TagValue=$TAG_VALUE" \
      --query KeyMetadata.KeyId --output text)"
  run_aws kms create-alias --alias-name "$ALIAS" --target-key-id "$KEY_ID"
else
  run_aws kms create-key --description "Biatec Chat token vault" --tags "TagKey=$TAG_KEY,TagValue=$TAG_VALUE"
  run_aws kms create-alias --alias-name "$ALIAS" --target-key-id "<new-key-id>"
  exit 0
fi
ARN="$(aws_ kms describe-key --key-id "$KEY_ID" --query KeyMetadata.Arn --output text)"
echo "KMS_KEY_ID (GitHub Stage secret) and KMS_KEY_ARN (for k8s-role.sh): $ARN"
