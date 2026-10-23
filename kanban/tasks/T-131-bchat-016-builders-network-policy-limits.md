---
id: T-131
title: "Restrict who can reach the builders sidecar and bound request and response sizes (BCHAT-016)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["k8s/base/**", "builders/sidecar/src/app.ts", "builders/sidecar/test/app.test.ts", "backend/Biatec.Chat.Tools/Builders/BuildersServiceCollectionExtensions.cs", "backend/Biatec.Chat.Tests/Builders/**"]
parallel: yes
conflicts_with: []
issue: "#269"
branch: "task/T-131"
owner: "session-20261023-1050"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-131 - Restrict who can reach the builders sidecar and bound request and response sizes (BCHAT-016)

## Context

Security audit finding BCHAT-016 (Low, issue #269). The builders sidecar is protected by a shared secret only: any pod in the cluster can
reach it, it reads request bodies without a limit, and the backend client reads responses without a limit.

## Goal

Only the backend pods can open connections to the sidecar; the sidecar rejects oversized request bodies; the backend refuses oversized responses.

## Scope

- In: a NetworkPolicy admitting the builders pod's port only from the backend pods; a body limit on the sidecar's `/v1/*` routes; `MaxResponseContentBufferSize` on the builders HTTP client; tests.
- Out: a NetworkPolicy for the backend itself (it needs the ingress controller's namespace, which is not known from this repository; follow-up); sidecar rate limits.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `kubectl kustomize` of both overlays contains the NetworkPolicy; it selects the builders pods and admits only pods labelled as the backend on port 3100.
- [ ] The sidecar answers 413 to a body above the limit and still accepts normal requests.
- [ ] The builders HTTP client has a response buffer limit.
- [ ] Existing sidecar and backend tests stay green.

## Verification

`pnpm --filter builders test`; `dotnet test backend --filter Builders`; `kubectl kustomize k8s/main` and `k8s/stage` (if kubectl is available).

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 13:00 · claude · (new) → backlog · created from GitHub issue #269 (BCHAT-016); blockers scan: none
- 2026-10-23 10:50 · scholtz · taken, and session-20261023-1050 · backlog → in-progress · claimed
