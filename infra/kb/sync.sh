#!/usr/bin/env bash
# Syncs the Biatec Chat knowledge base (docs/kb/*.md) into the Bedrock Knowledge Base and re-indexes it.
#
#   AWS_PROFILE=hackathon ./infra/kb/sync.sh
#
# Uploads to s3://$BUCKET/$PREFIX (only that prefix is synced with --delete, so other files in the bucket are left
# alone), starts an ingestion job on the knowledge base's S3 data source and waits for it. Override with KB_ID,
# DATA_SOURCE_ID, BUCKET, PREFIX. Uses the caller's SSO profile or role; never creates credentials.
set -euo pipefail

REGION="eu-central-1"
KB_ID="${KB_ID:-DZZGQITEAS}"
DATA_SOURCE_ID="${DATA_SOURCE_ID:-XYLE4VEJV6}"
BUCKET="${BUCKET:-biantec-chatbot-knowladgebase}"
PREFIX="${PREFIX:-biatec-chat-kb/}"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)/docs/kb"
AWS=(aws --region "$REGION")

echo "Uploading $DIR to s3://$BUCKET/$PREFIX" >&2
"${AWS[@]}" s3 sync "$DIR" "s3://$BUCKET/$PREFIX" --delete --exclude '*' --include '*.md' --content-type 'text/markdown; charset=utf-8'

JOB="$("${AWS[@]}" bedrock-agent start-ingestion-job --knowledge-base-id "$KB_ID" --data-source-id "$DATA_SOURCE_ID" \
  --query ingestionJob.ingestionJobId --output text)"
echo "Ingestion job $JOB started on $KB_ID/$DATA_SOURCE_ID" >&2

while true; do
  STATUS="$("${AWS[@]}" bedrock-agent get-ingestion-job --knowledge-base-id "$KB_ID" --data-source-id "$DATA_SOURCE_ID" \
    --ingestion-job-id "$JOB" --query ingestionJob.status --output text)"
  case "$STATUS" in
    COMPLETE|FAILED|STOPPED) break ;;
  esac
  sleep 10
done

"${AWS[@]}" bedrock-agent get-ingestion-job --knowledge-base-id "$KB_ID" --data-source-id "$DATA_SOURCE_ID" \
  --ingestion-job-id "$JOB" --query 'ingestionJob.{status:status,statistics:statistics,failureReasons:failureReasons}'
[[ "$STATUS" == "COMPLETE" ]]
