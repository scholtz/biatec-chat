---
id: T-121
title: "Configure trusted proxies for X-Forwarded-For in every overlay (BCHAT-005)"
status: in-progress
priority: P2
epic: "Security"
blocked_by: []
area: ["k8s/**", "backend/Biatec.Chat.Tests/Public/**", "docs/**"]
parallel: yes
conflicts_with: []
issue: "#258"
branch: "task/T-121"
owner: "session-20261022-1900"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-121 - Configure trusted proxies for X-Forwarded-For in every overlay (BCHAT-005)

## Context

Security audit finding BCHAT-005 (Medium, issue #258). `ClientAddress` trusts X-Forwarded-For only from configured
proxies and defaults to loopback only. No Kubernetes overlay configures any, so behind ingress-nginx every visitor
shares the ingress pod's address: one visitor can exhaust every per-IP limit (20/min public chat, 60/min admin), and
operator audit logs record the ingress IP. BCHAT-004 (session creation limit) depends on a real client address.

## Goal

Both overlays make the backend see the real client address, and a test proves the trust rule.

## Scope

- In: `ForwardedHeaders__KnownNetworks__N` in the main and stage backend ConfigMap patches (private pod/node ranges,
  documented as to be narrowed to the ingress pod CIDR); a unit test of `ClientAddress`; a note in `k8s/README.md`.
- Out: per-operator limiter partitioning, ingress `limit-rps` (optional in the finding); BCHAT-015 (X-Forwarded-Proto).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] Every overlay configures `ForwardedHeaders__KnownNetworks`; TEST-009 passes.
- [x] Unit test: X-Forwarded-For is honoured from a trusted peer (client address is the forwarded one) and ignored from an untrusted peer and when nothing is configured.
- [x] `ForwardLimit` stays 1.

## Verification

`dotnet test backend --filter ClientAddress`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-009.sh`.

## Hand-off

- **Summary:** Both overlays trust X-Forwarded-For from the RFC 1918 ranges; ClientAddress tests added; k8s README documents it. PR #303.
- **Files changed:** k8s/main and k8s/stage backend-config patches, k8s/README.md, Tests/Public/ClientAddressTests.cs.
- **How it was verified:** 5 new tests pass; TEST-009 passes. Not verified on a cluster (no access).
- **Follow-up tasks created:** none.
- **Unblocks:** BCHAT-004 (#257) builds on a correct client address.
- **Open questions:** I used the RFC 1918 ranges because the ingress pod CIDR is not known from the repo. The trust is by network, so an in-cluster pod could still spoof the header until BCHAT-016 (NetworkPolicy) lands, and the DEX two-hop path still shares one bucket (both documented in k8s/README.md). Please narrow to the real CIDR and confirm a RemoteIp in an admin log line on stage.

## Review

## Activity log

- 2026-10-22 19:00 · claude · (new) → backlog · created from GitHub issue #258 (BCHAT-005); blockers scan: none
- 2026-10-22 19:01 · scholtz · taken, and session-20261022-1900 · backlog → in-progress · claimed
