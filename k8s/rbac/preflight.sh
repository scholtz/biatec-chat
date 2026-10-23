#!/usr/bin/env bash
# Looks at the identity of the current kubeconfig before a deploy (BCHAT-020). Needs NAMESPACE; uses kubectl and jq.
#   - an expired token fails the step with a clear message; a token that expires within 7 days, or never, is a warning;
#   - a warning when the identity can read all or arbitrary secrets, delete workloads, exec into any pod or edit RBAC in the namespace
#     (it is wider than k8s/rbac/deploy-role.yaml). Only the namespace is probed; cluster-scope bindings are the cluster admin's to review.
# Warnings do not fail the deploy, so existing environments move to the scoped Role on the cluster admin's schedule (docs/cicd.md). Once an
# environment has moved, set STRICT_IDENTITY=1 (the repository or environment variable of that name): the warnings then fail the step.
set -uo pipefail
: "${NAMESPACE:?NAMESPACE is required}"
WARN_DAYS="${WARN_DAYS:-7}"
STRICT_IDENTITY="${STRICT_IDENTITY:-}"
problems=0
command -v jq >/dev/null || { echo "::error::jq is required by k8s/rbac/preflight.sh."; exit 1; }

token="$(kubectl config view --raw --minify -o 'jsonpath={.users[0].user.token}' 2>/dev/null || true)"
if [ -z "$token" ]; then
  echo "::notice::The kubeconfig has no bearer token (certificate, token file or exec plugin); its expiry is not checked here."
else
  payload="$(printf '%s' "$token" | cut -d. -f2 | tr '_-' '/+')"
  case $(( ${#payload} % 4 )) in 2) payload="$payload==" ;; 3) payload="$payload=" ;; esac
  exp="$(printf '%s' "$payload" | base64 -d 2>/dev/null | jq -r '.exp // empty' 2>/dev/null || true)"
  now="$(date -u +%s)"
  if ! [[ "$exp" =~ ^[0-9]+$ ]]; then
    echo "::warning::The kubeconfig token has no expiry (or is not a JWT): anyone who gets it keeps access until it is revoked. Issue a time-limited one (docs/cicd.md, \"The deploy identity\")."
    problems=1
  elif [ "$exp" -le "$now" ]; then
    echo "::error::The kubeconfig token in KUBE_CONFIG expired on $(date -u -d "@$exp" +%F 2>/dev/null || echo "$exp"). Issue a new one and update the secret (docs/cicd.md, \"The deploy identity\")."
    exit 1
  elif [ $(( exp - now )) -lt $(( WARN_DAYS * 86400 )) ]; then
    echo "::warning::The kubeconfig token expires on $(date -u -d "@$exp" +%F 2>/dev/null || echo "$exp"), within $WARN_DAYS days. Issue a new one and update the secret."
  else
    echo "kubeconfig token valid until $(date -u -d "@$exp" +%F 2>/dev/null || echo "$exp")"
  fi
fi

# yes -> 0, no -> 1. Anything else (API unreachable, request rejected) is a failed probe: it is reported, and under STRICT_IDENTITY it fails.
probe_failed=0
can() {
  local answer
  answer="$(kubectl auth can-i "$@" -n "$NAMESPACE" 2>/dev/null | head -n1 || true)"
  case "$answer" in
    yes) return 0 ;;
    no) return 1 ;;
    *) probe_failed=1; return 1 ;;
  esac
}
if can list secrets; then
  echo "::warning::The deploy identity can read every secret in $NAMESPACE. Bind the scoped Role (k8s/rbac/deploy-role.yaml) instead of the built-in admin role."
  problems=1
fi
# Rights that deploy-role.yaml does not grant: any of these answering yes means the identity is wider than the scoped Role.
if can get secrets/unrelated-probe || can delete deployments.apps || can create pods --subresource=exec; then
  echo "::warning::The deploy identity can read arbitrary secrets, delete workloads or exec into any pod in $NAMESPACE. Bind the scoped Role (k8s/rbac/deploy-role.yaml) instead of the built-in admin or edit role."
  problems=1
fi
if can create rolebindings.rbac.authorization.k8s.io; then
  echo "::warning::The deploy identity can edit RBAC in $NAMESPACE. Bind the scoped Role (k8s/rbac/deploy-role.yaml) instead of the built-in admin role."
  problems=1
fi
if [ "$probe_failed" = 1 ]; then
  echo "::warning::kubectl auth can-i did not answer; the rights of the deploy identity could not be checked."
  problems=1
fi
if [ "$problems" = 1 ] && [ "$STRICT_IDENTITY" = 1 ]; then
  echo "::error::STRICT_IDENTITY=1: the deploy identity or its token does not meet the policy above."
  exit 1
fi
exit 0
