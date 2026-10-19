#!/usr/bin/env bash
# Shared helpers for the Biatec Chat hackathon AWS scripts. Sourced, not run.
# SAFETY: every script defaults to DRY-RUN. Nothing touches AWS unless you pass --apply AND set BIATEC_AWS_APPROVED=yes
# (the user's explicit approval for anything that bills while idle). No credentials are read or written here: the
# SSO profile "hackathon" is used locally; on the instance the IAM role is used.
set -euo pipefail

export AWS_REGION="eu-central-1"
export AWS_DEFAULT_REGION="eu-central-1"
PROFILE="${AWS_PROFILE:-hackathon}"
TAG_KEY="Project"
TAG_VALUE="biatec"
NAME_PREFIX="biatec-chat"
# Allowed instance types per AGENTS.md: t3.*, t4g.*, m6i/m7g.large|xlarge, c6i/c7g.large. No GPUs.
ALLOWED_TYPE_REGEX='^(t3\.[a-z0-9]+|t4g\.[a-z0-9]+|m6i\.(large|xlarge)|m7g\.(large|xlarge)|c6i\.large|c7g\.large)$'

APPLY=0
for arg in "$@"; do
  case "$arg" in
    --apply) APPLY=1 ;;
    --dry-run) APPLY=0 ;;
  esac
done

if [ "$APPLY" = 1 ] && [ "${BIATEC_AWS_APPROVED:-}" != "yes" ]; then
  echo "Refusing --apply: set BIATEC_AWS_APPROVED=yes only after the user explicitly approved this run (idle-billing resources)." >&2
  exit 2
fi

# run: print the command; execute it only with --apply.
run() {
  if [ "$APPLY" = 1 ]; then
    echo "+ $*"
    "$@"
  else
    echo "[dry-run] $*"
  fi
}

# aws_: read-only helper (describe/list/get). Only call it when APPLY=1 or from read-only discovery.
aws_() { aws --profile "$PROFILE" --region "$AWS_REGION" "$@"; }
# run_aws: dry-run aware aws call (mutating).
run_aws() { run aws --profile "$PROFILE" --region "$AWS_REGION" "$@"; }

tag_spec() { echo "ResourceType=$1,Tags=[{Key=$TAG_KEY,Value=$TAG_VALUE},{Key=Name,Value=$NAME_PREFIX}]"; }

mode_banner() {
  if [ "$APPLY" = 1 ]; then echo "== APPLY mode (approved) =="; else echo "== DRY-RUN: nothing is created, changed or deleted. Pass --apply to execute. =="; fi
}
