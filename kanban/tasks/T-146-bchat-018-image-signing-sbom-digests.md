---
id: T-146
title: "Sign images with cosign, attach an SBOM, deploy by verified digest and pin base images (BCHAT-018)"
status: backlog
priority: P3
epic: "Security"
blocked_by: []
area: [".github/workflows/deploy-stage.yml", ".github/workflows/deploy.yml", ".github/dependabot.yml", "apps/demo/Dockerfile", "backend/Dockerfile", "builders/sidecar/Dockerfile", "packages/consent-protocol/mock-oidc/Dockerfile", "infra/ci/**", "docs/cicd.md"]
parallel: yes
conflicts_with: []
issue: "#271"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#threat-model-highlights"
created: 2026-10-23
updated: 2026-10-23
---

# T-146 - Sign images with cosign, attach an SBOM, deploy by verified digest and pin base images (BCHAT-018)

## Context

Security audit finding BCHAT-018 (Low, issue #271, RISK-013, CWE-494). The stage workflow pushes images to Docker Hub with
`provenance: false` and no signature or SBOM, `deploy.yml` deploys by a mutable tag, and every Dockerfile starts from a base image by tag.
Whoever can overwrite a tag on Docker Hub (a leaked token, a compromised base image) changes what runs, and nothing can show that a running
image came from this repository's workflow.

## Goal

Every image the stage workflow pushes is signed keyless (cosign, GitHub OIDC) and carries an SBOM attestation, a deploy can verify the
signature and then deploys the digest it verified, and the base images of the Dockerfiles are pinned by digest and kept current by Dependabot.

## Scope

- In: cosign keyless signing and an SPDX SBOM attestation of the backend, builders and demo images in `deploy-stage.yml`; a step in
  `deploy.yml` that resolves the tag to a digest, verifies the signature against the stage workflow identity, and pins the digest in the
  overlay; the kill switch `vars.IMAGE_SIGNING_REQUIRED` (signing failures and unsigned images only warn until it is `true`); base images
  pinned by digest in all Dockerfiles and Dependabot `docker` entries for them; a check that every Dockerfile `FROM` is pinned; the runbook in `docs/cicd.md`.
- Out: build provenance attestations (`provenance: false` stays: the promotion re-tags a plain manifest and reads its labels); signing the
  widget npm package (already published with provenance); a policy controller in the cluster; running the signing against Docker Hub.

## Parallel work

Parallel work possible: **yes**. No other unfinished task changes these files.

## Acceptance criteria

- [ ] `deploy-stage.yml` signs each pushed image by digest and attests an SBOM, with `id-token: write` only on the build job.
- [ ] `deploy.yml` verifies each overlay image's signature (identity `deploy-stage.yml` on `main`, issuer GitHub) and pins the verified digest before applying; an unsigned image fails the deploy only when `IMAGE_SIGNING_REQUIRED` is `true`.
- [ ] Every `FROM` in the repository's Dockerfiles carries a `@sha256:` digest (a check fails otherwise), the images still build, and Dependabot proposes updates for them.
- [ ] `docs/cicd.md` describes how to verify an image by hand and how to turn enforcement on.

## Verification

`python infra/ci/check_dockerfile_pins.py`; `docker build` of each image in CI; workflows parse as YAML. The signing and verification steps themselves
only run on GitHub: their first run is the test, which is why enforcement is off until the first signed deploy has succeeded.

## Hand-off

- **Summary:**
- **Files changed:**
- **How it was verified:**
- **Follow-up tasks created:**
- **Unblocks:**
- **Open questions:**

## Review

## Activity log

- 2026-10-23 17:30 · claude · (new) → backlog · created from GitHub issue #271 (BCHAT-018); blockers scan: none
