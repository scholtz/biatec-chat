#!/usr/bin/env bash
# Deletes EVERYTHING tagged Project=biatec in eu-central-1 that create.sh makes. Discovery uses the Resource Groups
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
for sg in $(ids_of security-group); do run_aws ec2 delete-security-group --group-id "$sg"; done
for vol in $(ids_of volume); do run_aws ec2 delete-volume --volume-id "$vol"; done

run_aws iam remove-role-from-instance-profile --instance-profile-name "$PROFILE_NAME" --role-name "$ROLE"
run_aws iam delete-instance-profile --instance-profile-name "$PROFILE_NAME"
run_aws iam detach-role-policy --role-name "$ROLE" --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore
run_aws iam delete-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock-kms"
run_aws iam delete-role --role-name "$ROLE"

run_aws kms delete-alias --alias-name "alias/$NAME_PREFIX-vault"
for key in $(ids_of key); do
  run_aws kms schedule-key-deletion --key-id "$key" --pending-window-in-days 7
done

# Anything else carrying the tag is reported, not ignored.
UNHANDLED=""
while read -r arn; do
  [ -z "$arn" ] && continue
  case "$arn" in *:instance/*|*:security-group/*|*:volume/*|*:key/*) ;; *) UNHANDLED="$UNHANDLED$arn"$'\n' ;; esac
done <<< "$ARNS"
if [ -n "$UNHANDLED" ]; then
  echo "WARNING: tagged resources this script does not know how to delete (remove them by hand):" >&2
  printf '%s' "$UNHANDLED" >&2
fi
echo "Afterwards re-run in dry-run mode: the tagged list must be empty (KMS keys stay listed as pending deletion for 7 days)."
