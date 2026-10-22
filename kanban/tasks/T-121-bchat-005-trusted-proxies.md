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

- [ ] Every overlay configures `ForwardedHeaders__KnownNetworks`; TEST-009 passes.
- [ ] Unit test: X-Forwarded-For is honoured from a trusted peer (client address is the forwarded one) and ignored from an untrusted peer and when nothing is configured.
- [ ] `ForwardLimit` stays 1.

## Verification

`dotnet test backend --filter ClientAddress`; `BIATEC_ROOT="$PWD" bash audits/harness/claude-fable-5-1/*/TEST-009.sh`.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-22 19:00 · claude · (new) → backlog · created from GitHub issue #258 (BCHAT-005); blockers scan: none
- 2026-10-22 19:01 · scholtz · taken, and session-20261022-1900 · backlog → in-progress · claimed
