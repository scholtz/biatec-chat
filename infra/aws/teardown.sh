#!/usr/bin/env bash
# Deletes EVERYTHING tagged Project=biatec in eu-central-1 that create.sh, k8s-role.sh and infra/guardrail/create.sh make. Discovery uses the Resource Groups
# Tagging API (read-only), so hand-made tagged resources are found too; types this script cannot delete are reported,
# never silently skipped. DEFAULT IS DRY-RUN:  BIATEC_AWS_APPROVED=yes ./teardown.sh --apply
# KMS keys cannot be deleted at once: they are scheduled for deletion after the 7 day minimum waiting period.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"
mode_banner

ARNS="$(aws_ resourcegroupstaggingapi get-resources --tag-filters "Key=$TAG_KEY,Values=$TAG_VALUE" \
  --query 'ResourceTagMappingList[].ResourceARN' --output text | tr '\t' '\n' | sed '/^$/d')"
ROLE="$NAME_PREFIX-instance-role"
PROFILE_NAME="$NAME_PREFIX-instance-profile"

echo "Tagged resources found:"; echo "${ARNS:-<none>}"

ids_of() { echo "$ARNS" | grep ":$1/" | sed 's|.*/||' || true; }

# Order matters: instances, then security groups and volumes, then IAM (global, looked up by name), then KMS.
IDS="$(ids_of instance | tr '\n' ' ')"
if [ -n "${IDS// /}" ]; then
  # shellcheck disable=SC2086
  run_aws ec2 terminate-instances --instance-ids $IDS
  # shellcheck disable=SC2086
  if [ "$APPLY" = 1 ]; then aws_ ec2 wait instance-terminated --instance-ids $IDS; fi
fi
for sg in $(ids_of security-group); do run_aws_ok ec2 delete-security-group --group-id "$sg"; done
# DeleteOnTermination=true removes the root volume with the instance, but the Tagging API can still list it for a while:
# only delete volumes that still exist.
for vol in $(ids_of volume); do
  if aws_ ec2 describe-volumes --volume-ids "$vol" >/dev/null 2>&1; then
    run_aws_ok ec2 delete-volume --volume-id "$vol"
  else
    echo "Volume $vol no longer exists, skipping."
  fi
done

# IAM is global and its tags are NOT returned by the regional Tagging API, so look the role up explicitly (by the name
# create.sh uses) and report its tag. Every delete tolerates "already gone", so reruns and partial state are fine.
if aws_ iam list-role-tags --role-name "$ROLE" --query "Tags[?Key=='$TAG_KEY'&&Value=='$TAG_VALUE']" --output text 2>/dev/null | grep -q .; then
  echo "IAM role $ROLE is tagged $TAG_KEY=$TAG_VALUE."
else
  echo "IAM role $ROLE not found or not tagged; the deletes below tolerate NotFound."
fi
run_aws_ok iam remove-role-from-instance-profile --instance-profile-name "$PROFILE_NAME" --role-name "$ROLE"
run_aws_ok iam delete-instance-profile --instance-profile-name "$PROFILE_NAME"
run_aws_ok iam detach-role-policy --role-name "$ROLE" --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore
run_aws_ok iam delete-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock-kms"
run_aws_ok iam delete-role --role-name "$ROLE"

# The k8s web-identity role from k8s-role.sh. Its OIDC provider is shared per cluster issuer, so it is only listed here.
run_aws_ok iam delete-role-policy --role-name "$NAME_PREFIX-k8s-role" --policy-name "$NAME_PREFIX-bedrock-kms"
run_aws_ok iam delete-role --role-name "$NAME_PREFIX-k8s-role"
echo "IAM OIDC providers (delete the cluster's by hand once nothing else uses it):"
aws_ iam list-open-id-connect-providers --query 'OpenIDConnectProviderList[].Arn' --output text 2>/dev/null || true

# The Bedrock guardrail from infra/guardrail/create.sh (pay per use, not idle-billing).
for g in $(ids_of guardrail); do run_aws_ok bedrock delete-guardrail --guardrail-identifier "$g"; done

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
  case "$arn" in *:instance/*|*:security-group/*|*:volume/*|*:key/*|*:guardrail/*) ;; *) UNHANDLED="$UNHANDLED$arn"$'\n' ;; esac
done <<< "$ARNS"
if [ -n "$UNHANDLED" ]; then
  echo "WARNING: tagged resources this script does not know how to delete (remove them by hand):" >&2
  printf '%s' "$UNHANDLED" >&2
fi
echo "Afterwards re-run in dry-run mode: the tagged list must be empty (KMS keys may stay listed as pending deletion for 7 days). IAM is not part of that list; the script checks the role and profile by name."
