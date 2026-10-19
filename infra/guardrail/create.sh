#!/usr/bin/env bash
# Creates the Biatec Chat Bedrock guardrail in eu-central-1 (tag Project=biatec) and publishes a numbered version.
# Idempotent: if a guardrail with the same name exists it is updated, then a new numbered version is published.
#
#   AWS_PROFILE=hackathon ./infra/guardrail/create.sh
#
# Prints the id and the version. Put them in config (never DRAFT):
#   Bedrock__Guardrail__Enabled=true  Bedrock__Guardrail__GuardrailId=<id>  Bedrock__Guardrail__GuardrailVersion=<version>
# Uses the caller's SSO profile or role; never creates credentials.
set -euo pipefail
PY="$(command -v python3 || command -v python)"

REGION="eu-central-1"
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SPEC="$DIR/guardrail.json"
NAME="$(sed -n 's/^  "name": "\(.*\)",/\1/p' "$SPEC" | head -n1)"
export AWS_DEFAULT_REGION="$REGION"
AWS=(aws --region "$REGION" --output text)

ID="$("${AWS[@]}" bedrock list-guardrails --query "guardrails[?name=='$NAME'].id | [0]")"
if [[ -z "$ID" || "$ID" == "None" ]]; then
  echo "Creating guardrail $NAME in $REGION" >&2
  ID="$("${AWS[@]}" bedrock create-guardrail --cli-input-json "file://$SPEC" --query guardrailId)"
else
  echo "Updating guardrail $NAME ($ID) in $REGION" >&2
  # update-guardrail takes the same body but not the tags, and needs the identifier.
  TMP="$(mktemp)"
  trap 'rm -f "$TMP"' EXIT
  "$PY" - "$SPEC" "$ID" "$TMP" <<'PY'
import json, sys
spec = json.load(open(sys.argv[1]))
spec.pop("tags", None)
spec["guardrailIdentifier"] = sys.argv[2]
json.dump(spec, open(sys.argv[3], "w"))
PY
  "${AWS[@]}" bedrock update-guardrail --cli-input-json "file://$TMP" >/dev/null
fi

VERSION="$("${AWS[@]}" bedrock create-guardrail-version --guardrail-identifier "$ID" \
  --description "published $(date -u +%Y-%m-%dT%H:%MZ)" --query version)"
echo "guardrailId=$ID"
echo "guardrailVersion=$VERSION"
