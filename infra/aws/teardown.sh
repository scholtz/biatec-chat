#!/usr/bin/env bash
# Deletes what Biatec Chat creates on AWS (Bedrock access only): the Roles Anywhere profile, trust anchor and role from
# rolesanywhere.sh, the guardrail from infra/guardrail/create.sh, and any KMS key tagged Project=biatec (left from the
# earlier KMS vault). The local CA and client certificates (rolesanywhere-out/) are not touched.
# Discovery uses the Resource Groups Tagging API (read-only); tagged resources of other types are reported, never
# silently skipped. The role of the earlier cluster-issuer design (biatec-chat-k8s-role) is removed too.
# DEFAULT IS DRY-RUN:  BIATEC_AWS_APPROVED=yes ./teardown.sh --apply
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"
mode_banner

ARNS="$(aws_ resourcegroupstaggingapi get-resources --tag-filters "Key=$TAG_KEY,Values=$TAG_VALUE" \
  --query 'ResourceTagMappingList[].ResourceARN' --output text | tr '\t' '\n' | sed '/^$/d')"
ROLE="$NAME_PREFIX-bedrock-role"
OLD_ROLE="$NAME_PREFIX-k8s-role"

echo "Tagged resources found:"; echo "${ARNS:-<none>}"

ids_of() { echo "$ARNS" | grep ":$1/" | sed 's|.*/||' || true; }

# Roles Anywhere profile and trust anchor first (they reference the role), found through their Project tag.
for p in $(ids_of profile); do run_aws_ok rolesanywhere delete-profile --profile-id "$p"; done
for t in $(ids_of trust-anchor); do run_aws_ok rolesanywhere delete-trust-anchor --trust-anchor-id "$t"; done

# IAM is global and its tags are NOT returned by the regional Tagging API, so roles are deleted by name. Older policy
# names and the earlier web-identity role (biatec-chat-k8s-role) are tried too. Deletes tolerate "already gone".
for r in "$ROLE" "$OLD_ROLE"; do
  run_aws_ok iam delete-role-policy --role-name "$r" --policy-name "$NAME_PREFIX-bedrock"
  run_aws_ok iam delete-role-policy --role-name "$r" --policy-name "$NAME_PREFIX-bedrock-kms"
  run_aws_ok iam delete-role --role-name "$r"
done
echo "IAM OIDC providers (left from the earlier cluster-issuer design; delete by hand if nothing else uses them):"
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
  case "$arn" in *:guardrail/*|*:key/*|*:profile/*|*:trust-anchor/*) ;; *) UNHANDLED="$UNHANDLED$arn"$'\n' ;; esac
done <<< "$ARNS"
if [ -n "$UNHANDLED" ]; then
  echo "WARNING: tagged resources this script does not know how to delete (remove them by hand):" >&2
  printf '%s' "$UNHANDLED" >&2
fi
echo "Afterwards re-run in dry-run mode: the tagged list must be empty (KMS keys may stay listed as pending deletion for 7 days)."
