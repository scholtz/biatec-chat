---
id: T-144
title: "Shorten Roles Anywhere client certificate validity, monitor expiry, and give a revocation path (BCHAT-019)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: ["infra/aws/**", ".github/workflows/rolesanywhere-expiry.yml", "docs/aws-setup.md"]
parallel: yes
conflicts_with: []
issue: "#272"
branch: "task/T-144"
owner: "session-20261023-1501"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-24
updated: 2026-10-24
---

# T-144 - Shorten Roles Anywhere client certificate validity, monitor expiry, and give a revocation path (BCHAT-019)

## Context

Security audit finding BCHAT-019 (Low, issue #272). The backend reaches Bedrock with a one-year client certificate whose private key sits in a
Kubernetes secret and a GitHub environment secret. There is no revocation path short of disabling the whole trust anchor, and the only
expiry warning is the 14-day check inside a deploy, so a certificate can expire between deploys.

## Goal

Certificates are valid for 90 days by default, a weekly check warns well before they expire, and one environment's certificate can be revoked by a CRL without
touching the other.

## Scope

- In: `CERT_DAYS` default 90; `infra/aws/rolesanywhere-expiry.sh` and a weekly workflow reading the environment secrets; `infra/aws/rolesanywhere-revoke.sh` (openssl CRL plus `aws rolesanywhere import-crl`); offline tests in `infra/aws/test-scripts.sh`; the runbook in `docs/aws-setup.md`.
- Out: automatic rotation of the GitHub secrets (needs a credential with write access to them); running anything against AWS.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `rolesanywhere.sh` issues certificates valid for 90 days unless `CERT_DAYS` says otherwise.
- [ ] The expiry script exits non-zero for a certificate inside the warning window (default 30 days) and zero outside it; the weekly workflow runs it for Stage and Production.
- [ ] The revoke script produces a CRL that lists the revoked certificate's serial and not the others', and imports it with `import-crl` (checked against the fake `aws` in the offline test).
- [ ] `docs/aws-setup.md` documents rotation, revocation and monitoring; the existing offline tests stay green.

## Verification

`infra/aws/test-scripts.sh`.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-24 01:00 · claude · (new) → backlog · created from GitHub issue #272 (BCHAT-019); blockers scan: none
- 2026-10-23 15:01 · scholtz · taken, and session-20261023-1501 · backlog → in-progress · claimed
