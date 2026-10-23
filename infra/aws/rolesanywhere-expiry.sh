#!/usr/bin/env bash
# Warns when a Roles Anywhere client certificate expires soon (BCHAT-019). Offline: it reads certificate files, nothing else.
#
#   infra/aws/rolesanywhere-expiry.sh [--days 30] certificate.pem [more.pem ...]
#
# Exit codes: 0 every certificate is valid for longer than the window, 1 at least one expires within it (or already expired), 2 usage or an
# unreadable file. The weekly workflow .github/workflows/rolesanywhere-expiry.yml runs it on the AWS_CLIENT_CERT secret of each environment.
set -uo pipefail
days=30
files=()
while [ $# -gt 0 ]; do
  case "$1" in
    --days) days="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,8p' "$0"; exit 0 ;;
    *) files+=("$1"); shift ;;
  esac
done
case "$days" in ''|*[!0-9]*) echo "--days needs a whole number." >&2; exit 2 ;; esac
[ "${#files[@]}" -gt 0 ] || { echo "Give at least one certificate file." >&2; exit 2; }
command -v openssl >/dev/null || { echo "openssl is required." >&2; exit 2; }

rc=0
for f in "${files[@]}"; do
  if ! end="$(openssl x509 -in "$f" -noout -enddate 2>/dev/null)"; then
    echo "::error::$f is not a readable certificate." >&2
    rc=2
    continue
  fi
  end="${end#notAfter=}"
  if openssl x509 -in "$f" -noout -checkend "$((days * 86400))" >/dev/null 2>&1; then
    echo "ok:   $f expires $end (more than $days days away)"
  else
    echo "::warning::$f expires $end: within $days days (or already expired). Renew it (docs/aws-setup.md, \"Renewing the client certificate\")."
    [ "$rc" = 2 ] || rc=1
  fi
done
exit "$rc"
