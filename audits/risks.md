# Biatec Chat security risk register

- **Owner of the register:** Biatec Chat CTO / designated security owner
- **Governed by:** [audits/instructions.md](instructions.md), section 12
- **Last updated:** 2026-10-22 (cycle 1, report 20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01)
- **Classification:** Internal. No secrets, exploit detail or customer data belong here.

This register is the living list of security risks. Audit reports in [`reports/`](reports/) are
point-in-time; this file is current state. Every audit cycle updates it (instructions section 0,
step 9). Entries seeded before the first audit cycle carry the status **Identified, not yet
audited** and their ratings are provisional.

## How to use

- **ID**: `RISK-###`, assigned sequentially, never reused or renumbered. Closed risks stay listed.
- **Rating**: Likelihood (L) and Impact (I) each 1-5; Rating = L x I, banded as
  Critical 20-25, High 12-19, Medium 6-11, Low 1-5. Ratings proposed by the auditor, confirmed
  by the owner. A rating is "provisional" until an audit cycle has tested the control.
- **Status**: `Identified, not yet audited` | `Open` | `Mitigating` | `Mitigated` |
  `Accepted (expires YYYY-MM-DD)` | `Transferred` | `Closed`.
  An expired acceptance reverts to `Open` at the next cycle.
- **Owner**: a named person. "Unassigned" is allowed only until the next CTO review.
- **Audit refs**: finding IDs (`BCHAT-###`) and test IDs (`TEST-###`) from the reports, plus the
  report filename that last touched the entry.
- **Review date**: when the entry must be re-examined even if nothing changed. Default is the
  next audit cycle and at most six months out.

Add a row to the summary table and a detail block below it. Keep both in ID order.

## Summary

| ID | Title | L | I | Rating | Status | Owner | Review by |
| --- | --- | ---: | ---: | --- | --- | --- | --- |
| RISK-001 | Consent protocol (CIBA, step-up, receipts, device enrollment) exists only against the mock OIDC | 4 | 5 | High (20, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-002 | No IdP revocation: a stolen refresh token stays valid at Biatec OIDC for up to 30 days | 3 | 4 | High (12, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-003 | Authenticator: keys not validated on devices, not biometric-bound, platform folders absent | 4 | 5 | Critical (20, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-004 | Evidence anchoring and export only partially integrated; unanchored windows | 3 | 3 | Medium (9, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-005 | Public `sub` instead of pairwise identifiers: erasure cannot unlink retained receipts | 4 | 2 | Medium (8, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-006 | Pending consent slots, rate limits and pauses are per client across all Biatec sites | 2 | 3 | Medium (6, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-007 | Hostile host page JavaScript can drive the widget below phone-approval thresholds | 4 | 4 | High (16, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-008 | Prompt injection through untrusted content (KB, token metadata, on-chain notes, tool results) | 4 | 3 | High (12, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-009 | Spending caps depend on the backend's own price feed and static test-network valuation | 3 | 4 | High (12, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-010 | Real OIDC does not honour `requireConsent`: cap becomes a hard stop; submission refused if OIDC signs without a challenge | 4 | 2 | Medium (8, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-011 | "No active device" is known only from the backend's own enrollment record | 3 | 3 | Medium (9, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-012 | Shared Bedrock quota and Redis outage: fail-open cost or abuse exposure under load | 3 | 3 | Medium (9, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-013 | Supply chain: CI workflows, published widget package and container images lack independent provenance assessment | 2 | 5 | Medium (10, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-014 | Vault master key custody when the KMS provider is configured off in the deployment | 2 | 5 | Medium (10, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-015 | Deprecated shared operator key (`legacy`) still accepted by the admin API | 3 | 4 | High (12, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-016 | Sibling dependencies (Biatec OIDC, MCP, CLAMM, algo-safe, smart contracts) are outside audit scope | 3 | 5 | High (15, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-017 | Bearer tokens are not sender-constrained (DPoP is a roadmap item) | 2 | 4 | Medium (8, provisional) | Open | Unassigned | 2027-01-22 |
| RISK-018 | No independent human review or device/live coverage in an AI-run audit cycle | 5 | 3 | High (15) | Open (confirmed by cycle 1) | Unassigned | 2027-01-22 |
| RISK-019 | Builder output trusted without field-level verification (liquidity) and no fee ceiling on swap/liquidity/safe-create/x402 | 3 | 5 | High (15) | Open | Unassigned | 2026-11-05 |
| RISK-020 | Cancel can race consent redemption: a cancelled preview is signed and broadcast | 2 | 4 | Medium (8) | Open | Unassigned | 2026-11-21 |
| RISK-021 | Anonymous abuse: unbounded session creation and shared rate-limit bucket behind ingress | 4 | 3 | High (12) | Open | Unassigned | 2026-11-21 |
| RISK-022 | Mock OIDC exposes device tokens and under-models CIBA rules; unsafe on reachable hosts | 2 | 4 | Medium (8) | Open | Unassigned | 2026-11-21 |
| RISK-023 | Bitcoin transfer path offered but unsupported at confirm; PSBT input values builder-asserted | 2 | 2 | Low (4) | Mitigated (transfers not offered by default, T-141; PSBT input values remain builder-asserted until a signing path exists, BCHAT-041) | scholtz | 2027-01-20 |

## Details

### RISK-001 Consent protocol exists only against the mock OIDC

- **Category:** External dependency / assurance gap
- **Description:** CIBA backchannel authentication, `authorization_details`, step-up challenges,
  device enrollment, attestation and receipts (OIDC fallback matrix rows 1-4, 21, 22) run only
  against this repository's mock. Against real Biatec OIDC, above-threshold signing is
  unavailable and the authenticator device screens are disabled behind `CONSENT_SETTINGS_API`.
- **Source:** [docs/required-changes-biatec-oidc.md](../docs/required-changes-biatec-oidc.md),
  [docs/threat-model.md](../docs/threat-model.md) "Controls this product deliberately does not own"
- **Current controls / fallback:** Feature stays disabled against real OIDC; mock-only behavior
  gated by configuration.
- **Treatment:** Audit must verify the gate holds against the real service (instructions 7.7),
  and that mock code cannot be enabled in production builds. Re-rate once Biatec OIDC ships.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-002 No IdP revocation; stolen refresh token valid up to 30 days

- **Category:** Identity / credential lifetime
- **Description:** Biatec OIDC has no revocation endpoint (rows 14, 18). Logout destroys the vault
  entry and parks the token; a refresh token stolen before logout stays usable at the IdP until
  it ages out (30 days).
- **Source:** [docs/security-notes-logout-and-revocation.md](../docs/security-notes-logout-and-revocation.md)
- **Current controls / fallback:** Vault encryption; retry worker ready to revoke once
  `Oidc:Revocation:Url` is configured.
- **Treatment:** Audit the vault read paths (7.1); measure actual residual lifetime; press
  for row 14 and the 8-hour lifetime in Biatec OIDC.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-003 Hardware-backed keys not validated on physical devices

- **Category:** Mobile / cryptography
- **Description:** Authenticator keystore and attestation code has not been exercised on signed
  release builds on physical Android and iOS devices; Dart mocks only.
- **Source:** instructions 7.7; architecture and threat documentation
- **Current controls / fallback:** None beyond code review.
- **Treatment:** M6 device testing with a recorded device matrix (7.3). Until then, every
  consent claim is mock-only.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-004 Evidence anchoring and export partially integrated

- **Category:** Evidence integrity
- **Description:** Merkle anchoring and export are partially wired; some windows are unanchored
  and both bundle and verification keys may come from the operator.
- **Source:** instructions 7.6 and 7.7
- **Current controls / fallback:** Signed evidence chain; offline verifier.
- **Treatment:** Verify anchors against an independent chain source; identify unanchored windows;
  test verifier on invalid bundles.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-005 Public `sub`; erasure cannot unlink retained receipts

- **Category:** Privacy
- **Description:** Pairwise subject identifiers per tenant client are missing (row 15). Retained
  receipts keep a public subject, so deletion is payload deletion, not anonymization.
- **Source:** [docs/required-changes-biatec-oidc.md](../docs/required-changes-biatec-oidc.md) row 15
- **Current controls / fallback:** Documented limitation.
- **Treatment:** Test erasure across stores (7.6); obtain legal advice on retention; press for
  pairwise `sub`.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-006 Consent slots and pauses shared across Biatec sites

- **Category:** Availability / abuse
- **Description:** Without origin-aware consent requests (row 17), pending slots, rate limits and
  pauses apply per client for all Biatec sites together. One compromised Biatec site can occupy
  the others' slots.
- **Source:** [docs/threat-model.md](../docs/threat-model.md) fallback table
- **Current controls / fallback:** Backend treats HTTP 429 as "try later".
- **Treatment:** Test slot exhaustion from one origin (7.4, 7.3); press for row 17.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-007 Hostile host page can drive the widget below approval thresholds

- **Category:** Browser boundary
- **Description:** Shadow DOM and the Confirm button do not isolate the widget from host
  JavaScript. A malicious or compromised host site can act as the user up to the phone-approval
  threshold and spending caps.
- **Source:** instructions 7.1; [docs/integration-widget.md](../docs/integration-widget.md)
- **Current controls / fallback:** Server-side caps, phone approval above threshold, origin
  registration.
- **Treatment:** Quantify what a hostile host can do below thresholds (7.1); confirm caps are
  enforced server-side regardless of widget state.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-008 Prompt injection through untrusted content

- **Category:** AI / untrusted input
- **Description:** KB content, token metadata, on-chain notes, host context and tool results reach
  the model. Injected instructions could attempt unauthorized tool calls or data extraction.
- **Source:** [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) security invariants; instructions 7.4
- **Current controls / fallback:** Tier and scope gates enforced server-side; untrusted-data
  delimiters and guardrails as defense in depth.
- **Treatment:** Deterministic forbidden-tool-call fixtures plus a versioned adversarial corpus
  (M5), re-run every cycle because model behavior drifts.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-009 Caps depend on the backend's own valuation

- **Category:** Financial business logic
- **Description:** `valuedEur` is the backend's prediction (row 11); Safe and DEX pool calls are
  unknown and always ask (rows 5, 6); test networks use a static price table (row 7). A wrong or
  stale price can under-count spend against the soft cap.
- **Source:** [docs/required-changes-biatec-oidc.md](../docs/required-changes-biatec-oidc.md)
- **Current controls / fallback:** Unknown application calls treated as above threshold.
- **Treatment:** Boundary, stale-price and unknown-asset tests (7.2); review price-feed trust.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-010 `requireConsent` not honoured by real OIDC

- **Category:** Availability / consent enforcement
- **Description:** Against real Biatec OIDC the cap is a hard stop for the rest of the window, and
  if OIDC signs without a challenge after `requireConsent` was sent, the backend refuses to
  submit (`consent_not_enforced`). Correct fail-closed behavior, but it must be verified, and it
  degrades the product until row 3 ships.
- **Source:** [docs/required-changes-biatec-oidc.md](../docs/required-changes-biatec-oidc.md) row 3
- **Current controls / fallback:** `Caps:OidcHonoursRequireConsent` default false.
- **Treatment:** Test that submission is refused, not downgraded (7.2); confirm the flag cannot be
  enabled against real OIDC by mistake.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-011 "No active device" known only from the backend's own record

- **Category:** Consent enforcement
- **Description:** The reduced per-client daily cap without an active device (row 10) relies on
  the backend's enrollment record or row 20; where neither exists, it relies on OIDC's step-up.
- **Source:** [docs/required-changes-biatec-oidc.md](../docs/required-changes-biatec-oidc.md) row 10
- **Current controls / fallback:** `Caps:NoDevice` enforcement of the 100 EUR cap.
- **Treatment:** Test missing-device paths at every entry point (7.2).
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-012 Shared Bedrock quota and Redis outage

- **Category:** Resource abuse / availability
- **Description:** The 50 requests/minute Bedrock limit is shared by the whole team; Redis loss may
  cause rate limits, breakers and pending-slot accounting to fail open.
- **Source:** [AGENTS.md](../AGENTS.md); instructions 7.4
- **Current controls / fallback:** Per-tenant and global breakers, throttling with backoff.
- **Treatment:** Approved Redis-outage and multi-replica scenarios; confirm fail-closed for
  security decisions and bounded cost for availability ones.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-013 Supply chain provenance not independently assessed

- **Category:** Supply chain / delivery
- **Description:** GitHub workflows (`ci`, `deploy`, `deploy-stage`, `promote-production`,
  `publish-widget`), the published widget package and container images have not been traced
  source -> build -> artifact -> deployed digest by an independent party.
- **Source:** [docs/cicd.md](../docs/cicd.md); instructions 6.2 check 15 and 7.5
- **Current controls / fallback:** Repository CI as configured.
- **Treatment:** M7 review, SBOM, pinned actions, workflow permissions and fork handling.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-014 Vault master key custody without KMS

- **Category:** Cryptography / key management
- **Description:** The optional KMS provider may be configured off in the actual deployment,
  leaving the vault master key in the deployed secret provider.
- **Source:** instructions 7.5
- **Current controls / fallback:** Kubernetes secrets; encryption at rest.
- **Treatment:** Verify the deployed provider, rotation and access path; do not credit a
  configured-off KMS.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-015 Deprecated shared operator key still accepted

- **Category:** Administrative access
- **Description:** Named operator keys exist (row 23), but the shared `Onboarding:OperatorKey` is
  still accepted as the deprecated operator `legacy`, weakening attribution of admin actions.
- **Source:** [docs/admin-api.md](../docs/admin-api.md); [docs/required-changes-biatec-oidc.md](../docs/required-changes-biatec-oidc.md) row 23
- **Current controls / fallback:** Signed admin evidence chain records the operator name.
- **Treatment:** Verify in deployment whether the legacy key is set; plan its removal.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-016 Sibling dependencies outside audit scope

- **Category:** Assurance gap
- **Description:** Biatec OIDC, Biatec MCP, BiatecCLAMM, algo-safe and the smart contracts are
  consumed as published packages and public APIs and are not audited by this engagement.
- **Source:** instructions section 4
- **Current controls / fallback:** Malicious-response testing with local doubles; pinned versions.
- **Treatment:** Record the gap in every report; obtain or commission audits of the dependencies;
  test reachability of known advisories.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-017 Bearer tokens not sender-constrained

- **Category:** Identity
- **Description:** DPoP is a roadmap item. A bearer token extracted from the vault or in transit
  inside the cluster can be replayed by whoever holds it.
- **Source:** [docs/threat-model.md](../docs/threat-model.md) "Out of scope for version 1"
- **Current controls / fallback:** Tokens never leave the backend; vault encryption; TLS.
- **Treatment:** Track; re-rate once RISK-002 and the OIDC changes land.
- **Audit refs:** none yet
- **Status / owner / review:** Open (confirmed in code by cycle 1) / Unassigned / 2027-01-22

### RISK-018 AI-run audit cycle lacks independent review and device/live coverage

- **Category:** Audit process
- **Description:** A cycle run by an AI agent (instructions section 0) has no second human
  assessor, no physical devices and no live-model or staging access unless granted. Its results
  are inputs to a human-reviewed cycle, not a release decision on their own.
- **Source:** instructions section 0
- **Current controls / fallback:** Report metadata marks the cycle as AI-run; limitations listed.
- **Treatment:** Schedule a human-reviewed cycle before any release decision; have a human re-run
  the AI-built harness.
- **Audit refs:** none yet
- **Status / owner / review:** Open / Unassigned / 2027-01-22

### RISK-019 Builder output trusted without field-level verification; no fee ceiling

- **Category:** Financial integrity / builder boundary
- **Description:** Liquidity groups from the sidecar are signed after checking only sender and sidecar-reported facts; rekey, close-out, receivers, app ids and fee are not verified. Swap, liquidity, safe-create and x402 verifiers have no fee ceiling.
- **Current controls:** OIDC consent threshold and phone approval; every other builder path verifies fields.
- **Treatment:** `LiquidityGroupVerifier`, fee ceilings, program/validator pinning; disable the capabilities by tenant policy until fixed.
- **Audit refs:** BCHAT-001, BCHAT-003, BCHAT-010; TEST-008, TEST-015; report 20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01
- **Status / owner / review:** Open / Unassigned / 2026-11-05

### RISK-020 Cancel races consent redemption

- **Category:** Concurrency
- **Description:** Cancel checks for an approved consent and flips the preview; the consent resume path signs without an atomic claim, so a "cancelled" preview can still be broadcast. Resume also skips pause/analyze, x402 and no-device gates.
- **Treatment:** atomic step claim in resume, cancel refuses signed steps, re-run pre-sign gates.
- **Audit refs:** BCHAT-002, BCHAT-011
- **Status / owner / review:** Open / Unassigned / 2026-11-21

### RISK-021 Anonymous abuse and shared rate-limit bucket

- **Category:** Availability / resource abuse
- **Description:** Session endpoints create rows and evidence without limits, and no overlay configures trusted proxies, so all visitors and the operator API share one per-IP bucket behind the ingress.
- **Treatment:** ForwardedHeaders in every overlay, rate limits on session endpoints, global body limit.
- **Audit refs:** BCHAT-004, BCHAT-005, BCHAT-035; TEST-009
- **Status / owner / review:** Open / Unassigned / 2026-11-21

### RISK-022 Mock OIDC exposure and under-modelled rules

- **Category:** Test double / assurance
- **Description:** `/mock/devices/{kid}` returns device tokens and activation bypasses the 24 h delay with no gate, and the image binds all interfaces. The mock does not enforce CIBA interval, pending caps or subject-scoped receipts. Not deployed by compose or k8s.
- **Treatment:** gate helpers behind `MOCK_TEST_HELPERS`, bind localhost by default, model the missing rules.
- **Audit refs:** BCHAT-006, BCHAT-024; TEST-010
- **Status / owner / review:** Open / Unassigned / 2026-11-21

### RISK-023 Bitcoin path reachable but unsupported

- **Category:** Functional / financial integrity
- **Description:** `wallet.transfer` offers Bitcoin and builds a preview that Confirm always fails as `group_mismatch`; PSBT input values rely on `witness_utxo` asserted by the builder.
- **Treatment:** gate Bitcoin out of the schema until a PSBT signing path exists.
- **Audit refs:** BCHAT-037, BCHAT-041
- **Status / owner / review:** Open / Unassigned / 2027-01-20

## Change log

| Date | Change | By | Report |
| --- | --- | --- | --- |
| 2026-10-22 | Register created and seeded with 18 entries from documented limitations and the commissioning brief | Ludovit Scholtz (AI-assisted) | none |
| 2026-10-22 | Cycle 1: seeded entries RISK-001 to RISK-017 confirmed in code (status Open); RISK-003 re-rated to Critical 20 (BCHAT-007/008/009); RISK-018 confirmed; added RISK-019 to RISK-023 | Claude Fable 5.1 (AI-run cycle, operator Ludovit Scholtz) | 20261022T084008304Z_claude-fable-5-1_acc0471b1903_final_v01 |
