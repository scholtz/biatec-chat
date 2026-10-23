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

- [x] `rolesanywhere.sh` issues certificates valid for 90 days unless `CERT_DAYS` says otherwise.
- [x] The expiry script exits non-zero for a certificate inside the warning window (default 30 days) and zero outside it; the weekly workflow runs it for Stage and Production.
- [x] The revoke script produces a CRL that lists the revoked certificate's serial and not the others', and imports it with `import-crl` (checked against the fake `aws` in the offline test).
- [x] `docs/aws-setup.md` documents rotation, revocation and monitoring; the existing offline tests stay green.

## Verification

`infra/aws/test-scripts.sh`.

## Hand-off

- **Summary:** rolesanywhere.sh issues client certificates valid for 90 days by default (CERT_DAYS). rolesanywhere-expiry.sh checks certificate files against a window (default 30 days; exit 1 inside it, 2 on usage errors) and a weekly workflow runs it on the AWS_CLIENT_CERT secret of Stage and Production. rolesanywhere-revoke.sh records one environment's certificate in a kept revocation index, signs a CRL with the CA and imports (or updates) it in the trust anchor; without --apply it only prints the plan and writes nothing. docs/aws-setup.md documents rotation, monitoring and revocation.
- **Files changed:** infra/aws/rolesanywhere.sh, infra/aws/rolesanywhere-expiry.sh (new), infra/aws/rolesanywhere-revoke.sh (new), infra/aws/test-scripts.sh, .github/workflows/rolesanywhere-expiry.yml (new), docs/aws-setup.md.
- **How it was verified:** infra/aws/test-scripts.sh passes offline: 90-day default, expiry flags a 10-day certificate and passes a 200-day one, window and usage errors, the CRL verifies against the CA and lists only the revoked serial, a second revocation keeps the first, import-crl is called against the fake aws, dry run writes nothing and a missing certificate is refused.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The weekly workflow was not run (it needs the real environment secrets); an environment with required reviewers would make it wait for approval. Nothing was run against AWS: the real import-crl call is checked only against the fake aws. Rotation of the GitHub secrets stays manual. The CRL is valid for CRL_DAYS (3650, beyond the CA certificate) and nothing monitors its expiry; update-crl replaces the whole CRL, so the script refuses to update an existing AWS CRL from a local index with no revocations unless FORCE_CRL=1.

## Review

## Activity log

- 2026-10-24 01:00 · claude · (new) → backlog · created from GitHub issue #272 (BCHAT-019); blockers scan: none
- 2026-10-23 15:01 · scholtz · taken, and session-20261023-1501 · backlog → in-progress · claimed
