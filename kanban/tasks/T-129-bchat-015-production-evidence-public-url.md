---
id: T-129
title: "Set Evidence__PublicBaseUrl in the production overlay and honour X-Forwarded-Proto from trusted proxies (BCHAT-015)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["k8s/main/backend-config.patch.yaml", "backend/Biatec.Chat.Api/Public/ClientAddress.cs", "backend/Biatec.Chat.Tests/Public/ClientAddressTests.cs", "k8s/README.md"]
parallel: yes
conflicts_with: []
issue: "#268"
branch: "task/T-129"
owner: "session-20261023-1015"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-129 - Set Evidence__PublicBaseUrl in the production overlay and honour X-Forwarded-Proto from trusted proxies (BCHAT-015)

## Context

Security audit finding BCHAT-015 (Low, issue #268). Evidence bundles tell verifiers where to fetch the signing keys. Without
`Evidence__PublicBaseUrl` the URL is built from the request, which is `http` behind the TLS-terminating ingress. The stage overlay
sets it; the production overlay does not, and `X-Forwarded-Proto` is not honoured.

## Goal

Production bundles advertise an `https` JWKS URL, and the request scheme is the forwarded one when the peer is a trusted proxy.

## Scope

- In: `Evidence__PublicBaseUrl: https://chat.biatec.io` in `k8s/main`. X-Forwarded-Proto handling was considered and dropped (see Hand-off).
- Out: BCHAT-005 follow-ups (narrowing the CIDR).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] `k8s/main` sets an `https` `Evidence__PublicBaseUrl`; TEST-012 passes.
- [x] (Dropped, see Hand-off) X-Forwarded-Proto from trusted proxies.
- [x] Existing tests stay green.

## Verification

`dotnet test backend --filter ClientAddress`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-012.sh`.

## Hand-off

- **Summary:** k8s/main now sets Evidence__PublicBaseUrl (https://chat.biatec.io) like stage, so evidence bundles advertise an https JWKS URL. X-Forwarded-Proto is deliberately not enabled (see Open questions). PR #312.
- **Files changed:** k8s/main/backend-config.patch.yaml.
- **How it was verified:** TEST-012 passes; no backend code changed. (An X-Forwarded-Proto variant was built and tested, then dropped after review.)
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The finding also suggests honouring X-Forwarded-Proto from known proxies. I left it out: the only code that builds an absolute URL from the request scheme is the evidence JWKS URL, which PublicBaseUrl now overrides in both overlays, and trusting the header from the private ranges would let any in-cluster pod set the scheme until the NetworkPolicy ticket (BCHAT-016) lands. Say if you want it anyway. Also please confirm chat.biatec.io (taken from k8s/main/ingress.yaml) is the public host verifiers should use.

## Review

## Activity log

- 2026-10-23 11:00 · claude · (new) → backlog · created from GitHub issue #268 (BCHAT-015); blockers scan: none
- 2026-10-23 10:15 · scholtz · taken, and session-20261023-1015 · backlog → in-progress · claimed
