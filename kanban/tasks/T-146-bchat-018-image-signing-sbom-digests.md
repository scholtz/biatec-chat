---
id: T-146
title: "Sign images with cosign, attach an SBOM, deploy by verified digest and pin base images (BCHAT-018)"
status: in-progress
priority: P3
epic: "Security"
blocked_by: []
area: [".github/workflows/deploy-stage.yml", ".github/workflows/deploy.yml", ".github/dependabot.yml", "apps/demo/Dockerfile", "backend/Dockerfile", "builders/sidecar/Dockerfile", "packages/consent-protocol/mock-oidc/Dockerfile", "infra/ci/**", "docs/cicd.md"]
parallel: yes
conflicts_with: []
issue: "#271"
branch: "task/T-146"
owner: "session-20261023-1557"
taken_by: "scholtz"
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

- [ ] `deploy-stage.yml` signs each pushed image by digest and attests an SBOM, with `id-token: write` only on the build job. (Written; proven by the first stage run.)
- [ ] `deploy.yml` verifies each overlay image's signature (identity `deploy-stage.yml` on `main`, issuer GitHub) and pins the verified digest before applying; an unsigned image fails the deploy only when `IMAGE_SIGNING_REQUIRED` is `true`.
- [x] Every `FROM` in the repository's Dockerfiles carries a `@sha256:` digest (a check fails otherwise), the images still build, and Dependabot proposes updates for them.
- [x] `docs/cicd.md` describes how to verify an image by hand and how to turn enforcement on.

## Verification

`python infra/ci/check_dockerfile_pins.py`; `docker build` of each image in CI; workflows parse as YAML. The signing and verification steps themselves
only run on GitHub: their first run is the test, which is why enforcement is off until the first signed deploy has succeeded.

## Hand-off

- **Summary:** deploy-stage.yml signs the pushed backend, builders and demo images by digest with keyless cosign (id-token: write on the build job only) and attaches a syft SPDX SBOM as a signed attestation. deploy.yml resolves each overlay image to its digest, verifies the signature against the deploy-stage.yml-on-main identity and pins the verified digest in the overlay before applying. The repository variable IMAGE_SIGNING_REQUIRED switches enforcement on; until then signing/verification failures only warn. All Dockerfile base images are pinned by digest, infra/ci/check_dockerfile_pins.py enforces it in CI, and Dependabot has docker entries for the four Dockerfile directories. docs/cicd.md has the runbook, manual verification and how to turn enforcement on.
- **Files changed:** .github/workflows/deploy-stage.yml, .github/workflows/deploy.yml, .github/workflows/ci.yml, .github/dependabot.yml, backend/Dockerfile, builders/sidecar/Dockerfile, apps/demo/Dockerfile, packages/consent-protocol/mock-oidc/Dockerfile, infra/ci/check_dockerfile_pins.py, docs/cicd.md.
- **How it was verified:** check_dockerfile_pins.py passes on the 4 Dockerfiles and its self-test rejects unpinned/short digests; digests were read from the registries with docker buildx imagetools; kubectl kustomize renders a tag plus digest as name:tag@sha256 (the digest wins at runtime); all edited workflows and dependabot.yml parse as YAML; action SHAs resolved from the release tags. CI builds all images from the pinned bases.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** The cosign and syft steps and the digest verification only run on GitHub; they have never run, so the first stage deploy after the merge is their test (enforcement is off until you set IMAGE_SIGNING_REQUIRED=true after a log shows 'Verified' for all three images). cosign-installer 4.x installs cosign 3.x, whose defaults differ from 2.x (untested here). Docker Hub must allow the signature/attestation tags (sha256-*.sig/.att) to be pushed by DOCKERHUB_TOKEN. postgres:16 and redis:7 in k8s/base and the compose files are still by tag: pinning them rolls the databases to the newest minor on the next deploy, left for a deliberate change. Production promote of a pre-signing version fails once enforcement is on. Build provenance stays off (the promotion needs plain manifests).

## Review

## Activity log

- 2026-10-23 17:30 · claude · (new) → backlog · created from GitHub issue #271 (BCHAT-018); blockers scan: none
- 2026-10-23 15:57 · scholtz · taken, and session-20261023-1557 · backlog → in-progress · claimed
