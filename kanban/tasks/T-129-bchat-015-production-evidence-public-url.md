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

- In: `Evidence__PublicBaseUrl: https://chat.biatec.io` in `k8s/main`; `XForwardedProto` next to `XForwardedFor` in `ClientAddress` (same trusted networks); tests; README note.
- Out: BCHAT-005 follow-ups (narrowing the CIDR).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] `k8s/main` sets an `https` `Evidence__PublicBaseUrl`; TEST-012 passes.
- [x] A trusted proxy's `X-Forwarded-Proto: https` makes the request scheme `https`; from an untrusted peer it is ignored.
- [x] Existing tests stay green.

## Verification

`dotnet test backend --filter ClientAddress`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-012.sh`.

## Hand-off

- **Summary:** k8s/main now sets Evidence__PublicBaseUrl (https://chat.biatec.io) like stage, so bundles advertise an https JWKS URL; ClientAddress honours X-Forwarded-Proto together with X-Forwarded-For, only from the configured proxies. PR #312.
- **Files changed:** k8s/main/backend-config.patch.yaml, Api/Public/ClientAddress.cs, Tests/Public/ClientAddressTests.cs, k8s/README.md.
- **How it was verified:** dotnet test full suite (1561 passed); TEST-012 passes; new tests for trusted proto honoured, untrusted ignored, unconfigured ignored, proto-only and invalid values.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The production host name chat.biatec.io is taken from k8s/main/ingress.yaml; please confirm it is the public host evidence verifiers should use. X-Forwarded-Proto is now trusted from the same private ranges as X-Forwarded-For, so another in-cluster pod could set the scheme until the NetworkPolicy ticket (BCHAT-016) lands; PublicBaseUrl does not depend on it.

## Review

## Activity log

- 2026-10-23 11:00 · claude · (new) → backlog · created from GitHub issue #268 (BCHAT-015); blockers scan: none
- 2026-10-23 10:15 · scholtz · taken, and session-20261023-1015 · backlog → in-progress · claimed
