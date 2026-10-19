#!/usr/bin/env bash
# Creates the hackathon hosting for Biatec Chat (Option B in docs/deploy-aws.md): KMS key, IAM role + instance profile,
# security group (no inbound; use SSM Session Manager) and ONE t4g.small instance. All tagged Project=biatec, eu-central-1.
# DEFAULT IS DRY-RUN. Idle-billing resources (EC2 + EBS + public IPv4 + KMS key) need the user's explicit approval:
#   BIATEC_AWS_APPROVED=yes ./create.sh --apply
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=common.sh
source "$DIR/common.sh"

INSTANCE_TYPE="${INSTANCE_TYPE:-t4g.small}"
VOLUME_GB="${VOLUME_GB:-20}"
if ! [[ "$INSTANCE_TYPE" =~ $ALLOWED_TYPE_REGEX ]]; then
  echo "Instance type $INSTANCE_TYPE is not allowed (t3.*, t4g.*, m6i/m7g.large|xlarge, c6i/c7g.large)." >&2; exit 1
fi

mode_banner
echo "IDLE-BILLING resources in this run (need explicit user approval): EC2 $INSTANCE_TYPE, ${VOLUME_GB} GB gp3, public IPv4, KMS key (see docs/deploy-aws.md for cost per day)."

if [ "$APPLY" = 1 ]; then
  ACCOUNT_ID="$(aws_ sts get-caller-identity --query Account --output text)"
else
  ACCOUNT_ID="${ACCOUNT_ID:-111111111111}"  # placeholder in dry-run
fi
ROLE="$NAME_PREFIX-instance-role"
PROFILE_NAME="$NAME_PREFIX-instance-profile"
SG_NAME="$NAME_PREFIX-sg"

# 1. KMS key for the token vault (symmetric, about 1 USD/month, flagged as idle-billing).
if [ "$APPLY" = 1 ]; then
  KMS_KEY_ID="$(aws_ kms create-key --description "Biatec Chat token vault" --tags "TagKey=$TAG_KEY,TagValue=$TAG_VALUE" \
      --query KeyMetadata.KeyId --output text)"
else
  run_aws kms create-key --description "Biatec Chat token vault" --tags "TagKey=$TAG_KEY,TagValue=$TAG_VALUE"
  KMS_KEY_ID="<new-key-id>"
fi
KMS_KEY_ARN="arn:aws:kms:$AWS_REGION:$ACCOUNT_ID:key/$KMS_KEY_ID"
run_aws kms create-alias --alias-name "alias/$NAME_PREFIX-vault" --target-key-id "$KMS_KEY_ID"

# 2. IAM role scoped to the eu. Claude 4.5 profiles and that one KMS key.
POLICY_FILE="$(mktemp)"
trap 'rc=$?; rm -f "$POLICY_FILE"; if [ "$rc" -ne 0 ]; then echo "create.sh failed (exit $rc). Tagged resources may already exist; run infra/aws/teardown.sh to clean up." >&2; fi; exit "$rc"' EXIT
sed -e "s|\${ACCOUNT_ID}|$ACCOUNT_ID|g" -e "s|\${KMS_KEY_ARN}|$KMS_KEY_ARN|g" "$DIR/bedrock-role-policy.json" > "$POLICY_FILE"
run_aws iam create-role --role-name "$ROLE" --assume-role-policy-document "file://$DIR/ec2-trust-policy.json" \
  --tags "Key=$TAG_KEY,Value=$TAG_VALUE"
run_aws iam put-role-policy --role-name "$ROLE" --policy-name "$NAME_PREFIX-bedrock-kms" --policy-document "file://$POLICY_FILE"
# SSM Session Manager access (no SSH, no inbound ports).
run_aws iam attach-role-policy --role-name "$ROLE" --policy-arn arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore
run_aws iam create-instance-profile --instance-profile-name "$PROFILE_NAME" --tags "Key=$TAG_KEY,Value=$TAG_VALUE"
run_aws iam add-role-to-instance-profile --instance-profile-name "$PROFILE_NAME" --role-name "$ROLE"

# 3. Security group in the default VPC: no inbound rules (reach the app through SSM port forwarding, or add a rule for your
#    own IP deliberately). Outbound stays open for Bedrock, KMS, Biatec OIDC and algod.
run_aws ec2 create-security-group --group-name "$SG_NAME" --description "Biatec Chat (no inbound)" \
  --tag-specifications "$(tag_spec security-group)"

# 4. The instance: AL2023 arm64 AMI from the public SSM parameter, IMDSv2 required, hop limit 2 so containers reach the role.
if [ "$APPLY" = 1 ]; then
  AMI_ID="$(aws_ ssm get-parameter --name /aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-arm64 --query Parameter.Value --output text)"
  SG_ID="$(aws_ ec2 describe-security-groups --filters "Name=group-name,Values=$SG_NAME" --query 'SecurityGroups[0].GroupId' --output text)"
else
  AMI_ID="<al2023-arm64-ami>"; SG_ID="<sg-id>"
fi
# IAM is eventually consistent: wait for the profile, then retry run-instances while it is still "invalid".
run_aws iam wait instance-profile-exists --instance-profile-name "$PROFILE_NAME"
if [ "$APPLY" = 1 ]; then sleep 10; fi
run_instances() {
  run_aws ec2 run-instances --image-id "$AMI_ID" --instance-type "$INSTANCE_TYPE" --count 1 \
  --iam-instance-profile "Name=$PROFILE_NAME" --security-group-ids "$SG_ID" \
  --metadata-options "HttpTokens=required,HttpPutResponseHopLimit=2" \
  --block-device-mappings "DeviceName=/dev/xvda,Ebs={VolumeSize=$VOLUME_GB,VolumeType=gp3,Encrypted=true,DeleteOnTermination=true}" \
  --user-data "file://$DIR/user-data.sh" \
  --tag-specifications "$(tag_spec instance)" "$(tag_spec volume)"
}
for attempt in 1 2 3 4 5; do
  if [ "$APPLY" != 1 ]; then run_instances; break; fi
  if OUT="$(run_instances 2>&1)"; then echo "$OUT"; break; fi
  echo "$OUT" >&2
  if echo "$OUT" | grep -q "Invalid IAM Instance Profile" && [ "$attempt" -lt 5 ]; then
    echo "Instance profile not propagated yet, retrying in 10s..." >&2; sleep 10
  else
    exit 1
  fi
done

echo "Next: see docs/deploy-aws.md (set Kms__KeyId=$KMS_KEY_ID, the Bedrock model ids, then docker compose up -d). Run teardown.sh when done."
