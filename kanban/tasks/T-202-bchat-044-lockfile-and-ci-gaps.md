---
id: T-202
title: "Install the mock OIDC image from the workspace lock; run the verifier and conformance suites in CI (BCHAT-044)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["packages/consent-protocol/mock-oidc/Dockerfile", ".github/workflows/ci.yml", ".npmrc", "docs/cicd.md"]
parallel: yes
conflicts_with: []
issue: "#297"
branch: "task/T-202"
owner: "session-20261023-1213"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-202 - Install the mock OIDC image from the workspace lock; run the verifier and conformance suites in CI (BCHAT-044)

## Context

Security audit finding BCHAT-044 (Low, issue #297, CNS-8, DLV-13). `packages/consent-protocol/mock-oidc/Dockerfile` installs with `npm install --no-package-lock`, so every build resolves fresh versions instead of the workspace lock. There is no `.npmrc` with `ignore-scripts`. `.github/workflows/ci.yml` runs the consent-protocol and mock-oidc suites but not the evidence-verifier tests or the consent conformance suite.

## Goal

The image is built from `pnpm-lock.yaml` with `--frozen-lockfile`, dependency install scripts are off by default, and CI runs the verifier tests and the conformance suite.

## Scope

- In: the mock-oidc Dockerfile (pnpm, frozen lockfile, like the builders sidecar image), a root `.npmrc`, two CI steps, a line in docs/cicd.md.
- Out: the images job pins (BCHAT-017, BCHAT-018).

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [x] `docker build -f packages/consent-protocol/mock-oidc/Dockerfile .` succeeds and installs from the workspace lock (no `--no-package-lock`).
- [x] A root `.npmrc` sets `ignore-scripts=true` and `pnpm install --frozen-lockfile` plus the whole JS test run still work.
- [x] CI runs `pnpm --filter @biatec/evidence-verifier test` and the consent conformance suite, and both pass locally.
- [x] The built mock OIDC image still starts and answers on its port.

## Verification

`docker build` of the image and a run with a curl of its port, `pnpm install --frozen-lockfile`, `pnpm --filter @biatec/evidence-verifier test`, `pnpm --filter @biatec/consent-protocol conformance`.

## Hand-off

- **Summary:** the mock OIDC image now installs with `pnpm install --frozen-lockfile --prod --filter mock-oidc...` from the workspace lock (multi-stage, like the builders image); root `.npmrc` with `ignore-scripts=true`; CI runs the consent conformance suite and the evidence-verifier tests and builds the mock OIDC image; docs/cicd.md updated.
- **Verification:** `docker build` of the image succeeds and the container answers 200 on `/.well-known/openid-configuration`; `pnpm install --frozen-lockfile` works with `.npmrc`; conformance (23 tests) and evidence-verifier pass locally.

## Review

## Activity log

- 2026-10-23 12:13 · kanban-split · (new) → backlog · created from audit issue #297
- 2026-10-23 12:13 · scholtz · taken · status stays backlog
- 2026-10-23 12:13 · session-20261023-1213 · backlog → in-progress · claimed, branch task/T-202
