---
id: T-124
title: "Pin the algo-safe deployment programs and validator per network and check the genesis (BCHAT-010)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["backend/Biatec.Chat.Tools/Safe/Create/**", "backend/Biatec.Chat.Api/Safe/Create/**", "backend/Biatec.Chat.Tests/Safe/Create/**", "docs/**"]
parallel: yes
conflicts_with: []
issue: "#263"
branch: "task/T-124"
owner: "session-20261022-2200"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-22
updated: 2026-10-22
---

# T-124 - Pin the algo-safe deployment programs and validator per network and check the genesis (BCHAT-010)

## Context

Security audit finding BCHAT-010 (Low, issue #263). `SafeBuilder.BuildDeployAsync` accepts any application-create call
that carries the requested name: the approval and clear programs and the validator app id are whatever the sidecar
built, and the group's genesis is not checked.

## Goal

A deployment whose programs or validator differ from the configured pins for its network is refused, and every safe
group must belong to the requested network.

## Scope

- In: `Safe:Deployment:Pins:<network>` (approval SHA-256, clear SHA-256, validator app id), enforced when configured;
  a genesis-id check on every safe-create group; tests; docs on how to compute the pins.
- Out: shipping pin values for networks (they depend on the deployed algo-safe release; not derivable offline); the sidecar-side genesis check.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] With a pin configured, a deploy whose approval program, clear program or validator differs is refused; the matching deploy passes.
- [x] A safe-create group built for another genesis is refused.
- [x] Unpinned networks behave as before.
- [x] Existing safe-create tests stay green.

## Verification

`dotnet test backend --filter Safe`.

## Hand-off

- **Summary:** `Safe:Deployment:Pins:<network>` (approval SHA-256, clear SHA-256, validator app id) is enforced on the deploy call's bytes when configured; every safe-create group must carry the requested network's genesis id. PR #306.
- **Files changed:** Tools/Safe/Create/SafeBuilder.cs, SafeDeploymentOptions.cs, Api/Safe/Create/SafeCreateExecutors.cs (registration), Tests/Safe/Create/SafeCreateTests.cs, k8s/README.md.
- **How it was verified:** dotnet test (Safe 78 passed, then full suite); tests for matching and each mismatching pin, and for another network.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** No pin values are shipped: they depend on the deployed algo-safe release and cannot be derived offline here, so pinning is off until you set them (k8s/README.md says how). The genesis check compares the genesis id only (the test fixture's genesis hash is synthetic), and the validator pin compares the builder's own statement (it is not in the program bytes). Please provide or approve the hashes for testnet and mainnet.

## Review

## Activity log

- 2026-10-22 22:00 · claude · (new) → backlog · created from GitHub issue #263 (BCHAT-010); blockers scan: none
- 2026-10-22 22:01 · scholtz · taken, and session-20261022-2200 · backlog → in-progress · claimed
