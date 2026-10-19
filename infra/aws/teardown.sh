#!/usr/bin/env bash
# Deletes what Biatec Chat creates on AWS (Bedrock access only): the k8s web-identity role from k8s-role.sh, the
# guardrail from infra/guardrail/create.sh, and any KMS key tagged Project=biatec (left from the earlier KMS vault).
# Discovery uses the Resource Groups Tagging API (read-only); tagged resources of other types are reported, never
# silently skipped. The cluster's IAM OIDC provider is only listed: other workloads may use it.
# DEFAULT IS DRY-RUN:  BIATEC_AWS_APPROVED=yes ./teardown.sh --apply
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"
mode_banner

ARNS="$(aws_ resourcegroupstaggingapi get-resources --tag-filters "Key=$TAG_KEY,Values=$TAG_VALUE" \
  --query 'ResourceTagMappingList[].ResourceARN' --output text | tr '\t' '\n' | sed '/^$/d')"
ROLE="$NAME_PREFIX-k8s-role"

echo "Tagged resources found:"; echo "${ARNS:-<none>}"

ids_of() { echo "$ARNS" | grep ":$1/" | sed 's|.*/||' || true; }

# IAM is global and its tags are NOT returned by the regional Tagging API, so the role is deleted by name. Both policy
# names are tried (biatec-chat-bedrock-kms from before the vault moved off KMS). Deletes tolerate "already gone".
run_aws_ok iam delete-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock"
run_aws_ok iam delete-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock-kms"
run_aws_ok iam delete-role --role-name "$ROLE"
echo "IAM OIDC providers (delete the cluster's by hand once nothing else uses it):"
aws_ iam list-open-id-connect-providers --query 'OpenIDConnectProviderList[].Arn' --output text 2>/dev/null || true

# The Bedrock guardrail (pay per use, not idle-billing).
for g in $(ids_of guardrail); do run_aws_ok bedrock delete-guardrail --guardrail-identifier "$g"; done

# KMS keys can't be deleted at once: they are scheduled with the 7-day minimum and stop billing when scheduled.
run_aws_ok kms delete-alias --alias-name "alias/$NAME_PREFIX-vault"
for key in $(ids_of key); do
  STATE="$(aws_ kms describe-key --key-id "$key" --query KeyMetadata.KeyState --output text 2>/dev/null || echo Missing)"
  case "$STATE" in
    PendingDeletion|PendingReplicaDeletion|Missing) echo "KMS key $key is $STATE, skipping." ;;
    *) run_aws_ok kms schedule-key-deletion --key-id "$key" --pending-window-in-days 7 ;;
  esac
done

# Anything else carrying the tag is reported, not ignored.
UNHANDLED=""
while read -r arn; do
  [ -z "$arn" ] && continue
  case "$arn" in *:guardrail/*|*:key/*) ;; *) UNHANDLED="$UNHANDLED$arn"$'\n' ;; esac
done <<< "$ARNS"
if [ -n "$UNHANDLED" ]; then
  echo "WARNING: tagged resources this script does not know how to delete (remove them by hand):" >&2
  printf '%s' "$UNHANDLED" >&2
fi
echo "Afterwards re-run in dry-run mode: the tagged list must be empty (KMS keys may stay listed as pending deletion for 7 days)."
