# Independent security audit: CTO commissioning instructions

- **Sponsor:** Biatec Chat CTO / designated security owner
- **Brief issued:** 2026-10-22 (this revision: 2026-10-22, revision 2)
- **Required deliverable:** Markdown reports in [`audits/reports/`](reports/), a re-runnable test
  harness under [`audits/harness/`](harness/), and an updated [risk register](risks.md)
- **Classification:** Instructions may be shared; assessment results are confidential by default.

These instructions define a **repeatable audit**: the same commissioning brief is executed for every
audit cycle, each cycle is pinned to an exact revision, every test can be re-run by a different
assessor from a clean checkout, and the results of each cycle feed the living
[risk register](risks.md). A cycle that cannot be re-run is not accepted.

## 0. How to run an audit (human team or AI agent)

When the sponsor says **"do new audit"** (or "run the audit", "new audit cycle"), the assessor,
whether a human team or an AI agent pointed at this file, executes the steps below and the result
is **one new report file in [`audits/reports/`](reports/)**, never an edit of an existing report.
An AI agent follows the same brief; the differences are listed at the end of this section.

1. **Pin the revision.** Work from a clean checkout. Record the full SHA, the 12-character short
   SHA and the worktree state (section 9.1). If the worktree is dirty, stop and say so unless the
   sponsor explicitly accepts a dirty-build audit.
2. **Determine the cycle.** List existing reports in `audits/reports/`. The newest `final` or
   `retest` report's `audited_commit` is this cycle's `baseline_commit`; its `cycle` plus one is
   this cycle's number. No prior report means cycle 1 and `baseline_commit` = `audited_commit`.
3. **Read the inputs**: this file, [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md), the
   documents in section 4, the current [risk register](risks.md), and the previous report if any.
4. **Compute the diff** from the baseline (section 5a, "Diff-driven re-audit") and include the
   changed-file, dependency and infrastructure lists in the report.
5. **Run the source code security audit** (section 6) over every security-critical module. Fill
   the code-review ledger (6.4) file by file. This step is never skipped or sampled.
6. **Run the test objectives** (section 7) that the available environment allows. Anything that
   needs a device, a live model, staging or an authorization you do not hold is marked
   `Not tested` with the reason; it is not silently dropped.
7. **Build or extend the harness** at `audits/harness/<auditor-tag>/<report-basename>/`
   (section 5a). Every `TEST-###` row in the coverage matrix either has a harness entry or a
   `device` / `live` / `staging` marker. Re-run the previous cycle's harness first.
8. **Write the report** using the filename rule in 9.1 and the skeleton in 9.2, with every
   finding in the 9.3 format. Put it at `audits/reports/<filename>.md`. Create the file even if
   there are zero confirmed findings; coverage, ledger, limitations and residual risk are still
   required.
9. **Update the risk register** ([risks.md](risks.md), section 12) in the same change: map every
   open finding, Partial/Not tested control and mock-only gap to a `RISK-###` entry, re-rate
   existing entries, and fill the report's "Risk register changes" section.
10. **Self-check against section 11's acceptance list**, then hand over. Do not mark anything as
    accepted; only the sponsor accepts.

An AI agent additionally:

- uses its model name as `auditor-tag` (for example `claude-fable-5-1`) and
  `auditor_organization: "AI-assisted assessment"`; names the operator who ran it in `assessors`;
- sets `independent_reviewer: "none (AI-run cycle)"` and `harness_rerun_by` to the same, and
  lists the missing independent review and the missing device/live coverage as limitations with
  the matching risk-register entries. An AI-run cycle is an input to a human-reviewed cycle, not
  a replacement for one;
- never calls live third-party systems, production, real wallets or the live model unless the
  sponsor granted that in the session; tests default to local doubles and `dotnet test` /
  `pnpm test` / `flutter test` style runs;
- never commits, pushes or publishes the report on its own; it writes the files and reports back.
  Committing is the sponsor's decision because results are confidential by default;
- scores with CVSS v4.0 like a human assessor, labels confidence honestly, and puts hypotheses it
  could not confirm in the appendix, not in the findings list.

## 1. Mandate and decision to support

Assess whether Biatec Chat can safely mediate financial actions in the presence of a hostile host
website, malicious model inputs or outputs, compromised dependencies, stolen sessions, and
concurrent requests. Give the CTO an evidence-based release recommendation and a prioritized
remediation plan. A scanner export, generic checklist, or statement that "tests passed" is not
an acceptable audit.

This brief commissions the *design and execution of the engagement*, not permission to probe
production, move funds, or access third-party systems. Testing starts only after the sponsor
and affected asset owners sign the statement of work (SOW) and rules of engagement (ROE).

The [product specification](../PRODUCT-DESCRIPTION.md) defines intended behavior and takes
precedence over other repository documents. Assess actual source, built artifacts and authorized
deployments independently: documentation and existing tests are claims to verify, not proof.
Record discrepancies rather than silently choosing the more convenient requirement.

The engagement must answer:

1. Can an unauthorized actor access another user's or tenant's data, credentials, or capabilities?
2. Can any path sign or submit something different from the exact action the user authorized?
3. Can prompt injection, an untrusted builder, or a compromised host bypass server-side policy?
4. Are consent, device binding, spending caps and replay defenses effective under concurrency,
   dependency failure and a multi-replica deployment?
5. Can sensitive data or evidence be stolen, altered, erased improperly, or falsely represented as verified?
6. Does the source code, at the audited revision, contain security defects in any security-critical
   module (section 6), and has every such module been read and reviewed, not just scanned?
7. Which conclusions are supported by production-equivalent evidence, which are mock-only,
   and what residual risks must block release or require named executive acceptance?

No report may claim the system is vulnerability-free, "fully audited", FAPI certified, PSD2
compliant, or otherwise certified merely because this assessment used related guidance.

### Audit cycles

An audit cycle is one execution of this brief against one pinned revision. Run a cycle:

- before every production release that changes a security-critical module listed in section 6;
- after any Critical or High finding is remediated (the retest is part of the originating cycle);
- at least every six months regardless of change, because dependencies, model behavior and the
  threat landscape move even when the code does not.

Each cycle produces: one report set in [`audits/reports/`](reports/) (section 9), a sanitized,
re-runnable harness in [`audits/harness/`](harness/) (section 5a), and an updated
[risk register](risks.md) (section 12). The next cycle starts from the previous cycle's audited
commit as its baseline (section 5a, "Diff-driven re-audit").

## 2. Staffing, schedule and change control

Propose a deliverable-based SOW. The bid must state person-days, named leads, tooling, device
coverage and exclusions. Commercial terms are agreed in the SOW and are outside these instructions.

| Workstream | Required output |
| --- | --- |
| Discovery, threat modeling and ROE | Asset inventory, data flows, attack trees, approved test plan |
| Source code security audit | Per-module code-review ledger, SAST/SCA results triaged by hand, findings with source locations |
| Backend, web, API and tenant isolation | Manual review, authenticated and unauthenticated tests |
| Authenticator and consent protocol | Android/iOS device evidence and protocol verification |
| Transaction integrity and financial business logic | Invariant, race, mutation and builder-boundary tests |
| AI orchestration, prompt injection and abuse | Versioned adversarial corpus and measured results |
| Infrastructure, CI/CD and supply chain | Deployment review, SBOM and provenance assessment |
| Evidence, cryptography and privacy lifecycle | Independent verification and lifecycle tests |
| Repeatable harness, independent QA, final reporting and two retest rounds | Re-runnable harness, reviewed findings, release recommendation, closure evidence, updated risk register |

Require a lead assessor, application/OAuth specialist, mobile specialist, blockchain/transaction
specialist, AI security specialist, and cloud/supply-chain specialist. Roles may overlap only
with demonstrated relevant experience and sufficient allocated time. Require an independent
technical reviewer who did not author the finding being reviewed. Disclose conflicts of interest
and subcontractors; confidentiality and evidence-handling obligations apply to every contributor.

Suggested schedule, finalized at kickoff:

- Week 1: scope freeze, access checks, inventory, ROE, threat model, coverage plan and harness skeleton.
- Weeks 2-5: parallel source code audit, manual review and controlled testing; weekly coverage
  and findings updates; harness grows with every test added.
- Week 6: adversarial end-to-end scenarios, independent re-run of the full harness by a second
  assessor, draft report.
- Week 7: engineering readout, factual corrections, final initial-assessment report, risk register update.
- Retest round 1: within 15 business days of an agreed remediation build.
- Retest round 2: within 15 business days of the next agreed remediation build.
  Reserve both rounds for 90 days after the initial final report; changes require written agreement.

The vendor must flag access delays or scope growth immediately and propose a written change
request. Never silently replace manual code review or device testing with automated scans.
Acceptance is based on evidence, coverage and repeatability, not vulnerability counts or a clean result.

## 3. Authorization, safety and confidentiality

Before active testing, obtain a signed asset allow-list: domains, IPs, tenant IDs, repositories,
cloud accounts, clusters/namespaces, mobile application IDs, networks, test wallets, third-party
endpoints, dates, tester source IPs, named contacts and emergency stop authority.

Agree exact per-target request rates, concurrency, maximum test funds, maximum model usage,
permitted fault injection, monitoring and maintenance windows. No numeric limit is permission
until the asset owner approves it.

### Mandatory boundaries

- Default to an isolated local environment and owner-approved staging with synthetic data,
  dedicated tenants and wallets. Use local chain simulation or testnet; never real customer funds.
- Public reachability is not authorization. Biatec OIDC, MCP, DEX, Safe, cloud services and host
  sites are separate assets; test their integration here using controlled doubles unless their
  owners separately authorize active testing. Do not modify sibling repositories.
- Production is limited to expressly approved read-only configuration inspection. Production
  exploitation, destructive tests, denial of service, persistence, social engineering, credential
  spraying, bulk extraction, real transfers and mainnet transactions are excluded unless a new,
  specific ROE permits them. Do not use discovered credentials against other systems.
- A simulated compromise is an explicit starting assumption, not permission to compromise a
  real supplier, operator account, customer device or shared cluster.
- No uploads of source, transcripts, credentials or findings to public scanners, external AI
  services or vendor telemetry collectors. Any external processor requires prior written
  approval, an appropriate data agreement and an approved retention/location policy.
- AWS calls/resources must use `eu-central-1`; Cost Explorer alone uses `us-east-1`.
  Use local SSO profile `hackathon` or an approved AWS workload role, never new IAM users or
  access keys. Tag provisioned resources `Project=biatec`; obtain explicit approval for anything
  billing while idle. Follow the repository's permitted instance types.
- Bedrock tests use approved Claude 4.5 `eu.` model IDs, explicit `maxTokens`, static-prompt
  caching and jittered throttling backoff. Coordinate below the team's shared 50 requests/minute
  and applicable per-model quota; a load test must not exhaust shared capacity.
- The recorded hackathon code freeze was 2026-10-20 at 13:30. It has elapsed as of this brief.
  Do not infer that changes or deployments are now authorized; confirm the current change window.

### Escalation and evidence handling

Stop the affected test immediately on unexpected funds movement, customer-data exposure,
service degradation, scope escape or signs of an active compromise. Notify the incident contact
immediately; for Critical findings require notification within one hour of confirmation, for
High within one business day. Do not wait for the final report. Use a secure channel, preserve
minimal evidence, and resume only with written approval.

Treat this repository as potentially public until its access controls are verified. Reports must
exist at the required path in the approved restricted audit checkout; **the path requirement is
not permission to commit or publish sensitive findings**. Submit a sanitized repository copy only
after sponsor disclosure approval. Keep raw evidence in the sponsor-approved encrypted evidence
store with least-privilege access and access logging. Never commit secrets, real tokens, private
keys, recovery codes, customer data or working credentials, even in a private repository.

Use synthetic markers to demonstrate exposure. Redact payloads consistently without removing the
facts needed to reproduce a finding. Give each artifact an evidence ID, capture time in UTC,
collector, target, tool version, SHA-256 digest and restricted location. Track custody and
transformations; distinguish original evidence from redacted derivatives.

At kickoff agree retention and deletion dates, incident/legal-hold exceptions, approved recipients
and a deletion certificate. Proposed default: vendor deletes raw evidence 90 days after final
retest acceptance unless a written hold applies; the sponsor retains the authoritative report.
Use an authenticated delivery channel; record the report digest and vendor approval/signature
outside the report itself to avoid a self-referential hash.

## 4. Inventory and scope map

Start with the [architecture](../docs/architecture.md), [threat model](../docs/threat-model.md),
[OIDC fallback matrix](../docs/required-changes-biatec-oidc.md),
[revocation notes](../docs/security-notes-logout-and-revocation.md),
[deployment guide](../k8s/README.md), [CI/CD guide](../docs/cicd.md) and the current
[risk register](risks.md).
Inventory every endpoint, capability, background worker, privileged interface, data store,
external call and deployed feature flag. Include undocumented and disabled routes; distinguish
code present, reachable, configured off, mock-only, planned and not supplied for testing.

| Surface | Repository entry points | Mandatory assessment focus |
| --- | --- | --- |
| Embeddable widget and host integration | [chat widget](../packages/chat-widget/), [integration guide](../docs/integration-widget.md), [demo](../apps/demo/) | Hostile host JS, message/markdown rendering, popup messaging, transport, consent UX, full destinations, SSE and browser data exposure |
| API, BFF and domain policy | [backend](../backend/), [wire contracts](../packages/chat-protocol/) | OAuth, sessions, tenant isolation, authorization, caps, confirmation, workers and input validation |
| Model and tools | [backend](../backend/), [knowledge base](../docs/kb/), [admin API](../docs/admin-api.md) | Capability gates, untrusted content, tool schemas, model/KB configuration, prompt caching, cost controls |
| Builders and financial integrations | [sidecar](../builders/sidecar/), [CLAMM integration](../docs/integration-biatec-clamm.md), [x402](../docs/x402-pay.md) | Unsigned groups, read/build/submit boundaries, payload validation, optional-feature gates |
| Mobile and consent | [authenticator](../apps/biatec_authenticator/), [consent protocol](../packages/consent-protocol/), [third-party integration](../docs/integration-third-party-consent.md) | Enrollment, hardware keys, attestation, recovery, approval and receipt semantics |
| Records and independent verification | [backend](../backend/), [evidence verifier](../packages/evidence-verifier/) | Vault encryption, evidence signatures, anchoring, export, deletion, retention and key lifecycle |
| Runtime and delivery | [Kubernetes](../k8s/), [infrastructure](../infra/), [workflows](../.github/workflows/), [scripts](../scripts/), [local compose](../docker-compose.yml) | IAM, ingress/proxy trust, network isolation, secrets, CI trust, artifacts, publishing and rollback |
| Existing validation | [backend tests](../backend/Biatec.Chat.Tests/), [end-to-end tests](../e2e/), package and app tests | Reproduce baseline, identify mocks, independently exercise missing negative cases |

Assess published dependencies as consumed here: pinned versions, vulnerability reachability,
build integrity, interfaces and malicious-response behavior. A complete audit of upstream smart
contracts, Biatec OIDC/MCP internals, wallet custody, cloud-provider internals or external websites
is **not** included without a separately authorized SOW. Explicitly record these assurance gaps;
do not claim their security from a successful mock integration.

Review all implemented chains and network selectors. Give Algorand the full financial test suite.
For EVM/Bitcoin or other specified but absent functionality, verify unreachability/feature gates
and record the gap; if enabled, include chain-specific signing, replay and intent-binding tests.

## 5. Required methodologies

Use the following complementary methods. No single method substitutes for another. Pin standard
versions and tool versions at kickoff; record changes during the engagement. The numbered
references in section 13 are starting points, not a substitute for an agreed control inventory.

| Method | Required procedure | Required evidence / exit condition |
| --- | --- | --- |
| M1: threat-led architecture review | Draw data-flow diagrams and trust boundaries; use STRIDE plus financial-abuse attack trees. Model anonymous users, other tenants, hostile host JS, malicious content/tool responses, compromised service identities and insiders. Trace each asset to preventive and detective controls. | Versioned diagrams, attacker prerequisites, abuse cases and control owners; every high-impact path mapped to tests or an explicit limitation |
| M2: source code security audit (white-box) | Section 6 in full: read every security-critical module, trace untrusted sources to sensitive sinks and all authorization/signing paths, run pinned SAST/SCA/secret scanning and triage every hit by hand. Use ASVS 5.0.0 Level 2 throughout; add applicable Level 3 requirements for identity, vault, consent, financial authorization and administrative controls. Review cryptography and state transitions beyond lint/SAST. | Code-review ledger (section 6.4) covering 100% of security-critical files, exact versioned requirement IDs, source locations at audited commit, positive/negative tests; no blanket Level 3 claim for a selectively assessed system |
| M3: black-box and gray-box web/API testing | Run a timeboxed discovery phase without source assumptions, then credentialed tests with two independent tenants, two users per tenant, anonymous sessions and operator roles. Derive routes from runtime/OpenAPI and source, not just the UI. Use WSTG and the API Security Top 10. | Route/method/role/tenant matrix; sanitized request/response evidence; both horizontal and vertical authorization checks |
| M4: invariant, property and state-machine testing | Model preview -> confirm -> consent -> sign -> submit, plus enrollment, revocation and Safe proposal states. Generate malformed and boundary inputs, reordered/duplicate requests, parallel operations and controlled dependency failures. Minimize failing cases. | Reproducible seeds, sequences, expected invariants, observed effects, final persisted state and assertions at the signing/submission boundary |
| M5: AI adversarial evaluation | Test direct/indirect prompt injection, multilingual/encoded instructions, poisoned KB/token metadata/tool results, fabricated capabilities and cross-session retrieval. Combine deterministic hostile tool-call fixtures with repeated approved live-model trials. | Versioned corpus; model ID, parameters, prompts/config digests, guardrail version, repetitions, success numerator/denominator and business effect; not just screenshots of model text |
| M6: mobile static/dynamic assessment | Map MASVS requirements to MASTG tests. Review Flutter plus Android/iOS native code; exercise signed release builds on physical devices and instrumented test devices. Validate hardware-backed operations, not just Dart mocks. | OS/device/build matrix, keystore/attestation observations, tamper and lifecycle tests; unavailable device coverage explicitly not tested |
| M7: infrastructure and supply-chain assessment | Review IaC, rendered manifests, effective runtime permissions and CI trust. Run approved local SCA, secret and container scans; manually validate reachability. Trace source -> build -> image/package -> deployed digest. | SBOM in SPDX or CycloneDX format, pinned artifacts, provenance gaps, IAM/RBAC paths, validated findings; suppressed scanner hits justified |
| M8: controlled adversarial scenarios and detection | Chain independently established weaknesses inside the ROE, using synthetic data and bounded privileges. Exercise malicious-host-to-financial-action, cross-tenant access and compromised-builder scenarios. Coordinate with defenders to test detection and response. | End-to-end timeline, prevented/permitted steps, alert delivery/triage observations and cleanup evidence; no assumed permission for stealth or persistence |
| M9: independent reproduction and retesting | A second assessor re-runs the complete harness (section 5a) and reproduces Critical/High findings, or independently validates evidence where rerunning is unsafe. After remediation, repeat the original case and related bypass/regression variants against a new pinned build. | Reviewer sign-off, finding-by-finding retest ledger, new revision and evidence; a code diff alone cannot close a finding |

The web/API Top 10 and LLM Top 10 are prioritization aids, not complete verification standards.
Use OAuth Security BCP RFC 9700 for identity review and NIST AI 600-1 for evaluation/governance
risks. Formal verification is not implicitly included: use state models/property tests here;
propose a separate formal proof scope if warranted and distinguish proof assumptions from tests.

## 5a. Repeatability requirements

Every result in a report must be reproducible by a different assessor, on a different machine,
from the pinned inputs alone. The following are hard requirements; a report that does not meet
them is returned as incomplete.

### Pinned inputs

Record, in the report's run manifest (section 9.2, appendices) and in the harness README:

- repository commit (full SHA) and worktree state; container image and package digests; the
  mobile build IDs; the configuration and feature-flag digests (values redacted, digest kept);
- lockfiles as committed (`pnpm-lock.yaml`, `packages.lock.json` or the .NET equivalent,
  `pubspec.lock`), the .NET SDK, Node/pnpm and Flutter versions, and the OS/device matrix;
- every tool with exact version, ruleset version and invocation options (SAST, SCA, secret
  scanner, fuzzers, proxy, SDKs used for independent transaction decoding);
- the Bedrock model IDs, parameters (`maxTokens`, temperature, top-p), system prompt digest,
  guardrail version and the adversarial corpus version;
- seeds for every randomized or property-based test, and the clock values used for
  time-boundary tests;
- the versions of all standards used (section 13).

### Harness layout and contract

Deliver a sanitized harness at `audits/harness/<auditor-tag>/<report-basename>/`, where
`<report-basename>` is the report filename without `.md`. The harness must contain:

- `README.md`: prerequisites, the one command that runs everything, the one command that runs a
  single `TEST-###`, expected runtime, and the mapping from test IDs to scripts;
- one entry per coverage-matrix row (section 8), named by its `TEST-###` ID, that exits non-zero
  when the observed behavior differs from the recorded expectation;
- fixtures and controlled doubles for Biatec OIDC, MCP, the builder sidecar, chain nodes and the
  model (deterministic hostile tool-call fixtures), with no live third-party dependency unless
  the ROE authorizes it and the test is marked `live`;
- the adversarial corpus (M5) as versioned data files, never only as prose;
- no secrets, no real tokens, no customer data; synthetic markers only.

The harness runs from a clean checkout of the audited commit with `docker compose up -d` for
Redis and PostgreSQL and the repository's documented test commands. If a test needs a physical
device, a live model or an authorized staging environment, mark it `device`, `live` or
`staging`, state exactly what it needs, and provide the recorded evidence so the rest of the
suite still runs without it.

Deterministic tests must yield the same Pass/Fail on re-run. Non-deterministic tests (live
model trials) report a fixed number of repetitions, the numerator/denominator and the spread,
and the re-run must fall inside the recorded spread or be flagged as a drift finding.

### Independent re-run before delivery

Before a draft or final report is delivered, a second assessor who did not author the tests
re-runs the full harness from the pinned inputs and records the result in the report
("Re-run ledger": test ID, runner, date, machine, outcome, deviations). Deviations are either
fixed in the harness or reported as findings in their own right; they are never silently
re-labelled.

### Diff-driven re-audit

Each subsequent cycle:

1. takes the previous cycle's `audited_commit` as `baseline_commit`;
2. lists the changed files (`git diff --name-only <baseline>..<audited>`), the changed
   dependencies (lockfile diff) and the changed infrastructure (rendered-manifest and workflow
   diff), and includes the lists in the report;
3. re-runs the complete previous harness against the new revision (regression), and marks each
   previously passing test that now fails as a new finding;
4. performs the full section 6 code audit on every changed or new security-critical file, and
   re-reads unchanged security-critical files whose dependencies or callers changed;
5. re-runs the full adversarial corpus, because model behavior changes without code changes;
6. carries every `TEST-###` and finding ID forward unchanged; new tests and findings get new IDs.
   IDs are never reused across cycles;
7. updates the [risk register](risks.md) (section 12) before the report is accepted.

A "nothing changed" cycle still re-runs the harness and the corpus, refreshes SCA/SBOM and
checks the expiry dates in the risk register.

## 6. Mandatory source code security audit

The code audit is the backbone of the engagement. It is manual, complete for the modules below,
and independent of whether any dynamic test found a problem. Automated tools are inputs to it,
not substitutes.

### 6.1 Security-critical modules

Every file under the following paths is security-critical and must be read in full by a named
reviewer at the audited commit. Add to this list any file reached while tracing a trust boundary.

| Module | Path | Why it is critical |
| --- | --- | --- |
| API host, middleware, routes | [backend/Biatec.Chat.Api/](../backend/Biatec.Chat.Api/) | Authentication, session cookies, CORS/CSRF, tenant derivation, forwarded headers, rate limits, SSE, admin API |
| Domain, policy and vault | [backend/Biatec.Chat.Core/](../backend/Biatec.Chat.Core/) | Capability gates, caps, confirmation window, consent, signing path, vault encryption, evidence chain, workers |
| Tool definitions and model orchestration | [backend/Biatec.Chat.Tools/](../backend/Biatec.Chat.Tools/) | Tool schemas, untrusted-content handling, prompt assembly, tool-loop bounds |
| Builder sidecar | [builders/sidecar/](../builders/sidecar/) | Unsigned group construction via MCP/libraries, payload validation, read/build/submit boundary |
| Widget | [packages/chat-widget/](../packages/chat-widget/) | Markdown/HTML rendering, popup and `postMessage` origin checks, transport, consent UX, storage |
| Wire contracts | [packages/chat-protocol/](../packages/chat-protocol/) | Schema strictness, versioning, what can be forged by a host |
| Consent protocol | [packages/consent-protocol/](../packages/consent-protocol/) | Canonicalization, signature verification, binding of request/subject/client/group hash |
| Evidence verifier | [packages/evidence-verifier/](../packages/evidence-verifier/) | Hash/signature/anchor verification, error-to-success confusion |
| Authenticator | [apps/biatec_authenticator/](../apps/biatec_authenticator/) (Dart and Android/iOS native) | Key generation, attestation, biometric gating, storage, deep links, transport |
| Mock OIDC and test doubles | wherever the repository keeps them | Must not leak into production builds; mock-only behavior must be gated |
| Delivery | [.github/workflows/](../.github/workflows/), [scripts/](../scripts/), [k8s/](../k8s/), [infra/](../infra/), [docker-compose.yml](../docker-compose.yml), Dockerfiles | Script injection, permissions, pinned actions, secrets, image provenance, securityContext, network policy |
| Configuration | `appsettings*.json`, environment templates, Helm/Kustomize values | Default-on dangerous settings, mock flags, key providers, allow-lists |

Files outside these paths are reviewed by risk-based sampling with a documented population and
selection rationale. Generated code, vendored code and lockfiles are reviewed for provenance,
not line by line.

### 6.2 Mandatory code-level checks

Apply each check to every security-critical module and record the result per file in the ledger.
"Not applicable" needs a one-line reason.

1. **Authorization on every entry point**: route, SSE stream, worker, admin and diagnostic
   endpoint; tenant and subject scoping on every data access; no object reference without an
   ownership check (CWE-285, CWE-639, CWE-862).
2. **Token and session handling**: tokens never serialized to the browser, URLs, logs, errors or
   evidence; cookie attributes; rotation and absolute lifetime; subject/client/tenant confusion
   (CWE-522, CWE-613, CWE-384).
3. **Signing and submission path**: backend-to-OIDC signing only; never MCP `signTransaction`;
   group ID recomputed and bound; intent fields compared field by field; forbidden rekey/close
   fields rejected; idempotency of confirm/submit under retries (CWE-345, CWE-362).
4. **Untrusted-input validation at every trust boundary**: host page, model output, tool
   results, builder responses, KB content, token metadata, on-chain notes, admin uploads;
   strict schemas; size, depth and recursion bounds (CWE-20, CWE-1284).
5. **Injection**: SQL (including raw queries through the ORM), command, header, log, template,
   markdown/HTML in the widget, prompt assembly (CWE-89, CWE-78, CWE-79, CWE-117).
6. **SSRF and egress**: every outbound URL built from input; allow-lists; redirects; x402 and
   DNS-proof paths (CWE-918).
7. **Cryptography**: algorithm choices, key sizes, nonce/IV uniqueness, associated data,
   constant-time comparisons, canonicalization before signing, randomness sources, key lifecycle
   and rotation, KMS versus local key provider (CWE-327, CWE-323, CWE-330).
8. **Secrets and configuration**: no secrets in code, config, images, workflows or logs;
   dangerous defaults; mock flags off by default; feature gates enforced server-side (CWE-798, CWE-1188).
9. **Concurrency and state**: cap reservations, pending slots, confirm windows and consent
   consumption under parallel requests and multiple replicas; Redis/PostgreSQL transaction
   boundaries; sweeper and retry-worker races (CWE-362, CWE-367).
10. **Error handling and fail-safe**: dependency outage, Redis loss, model throttling, builder
    failure and verifier errors must fail closed for security decisions and must not produce
    success-shaped output (CWE-636, CWE-755).
11. **Logging, privacy and evidence**: no personal data or secrets in logs; evidence chain
    inputs; erasure paths across stores, replicas and caches (CWE-532, CWE-359).
12. **Deserialization and parsing**: JSON/CBOR/transaction decoding with hostile input;
    polymorphic deserialization; duplicate keys and numeric ambiguity (CWE-502, CWE-1288).
13. **Dependencies**: reachable vulnerable code paths, not just advisory counts; pinned
    versions; integrity; unsupported runtimes; install-time scripts (CWE-1395).
14. **Mobile-native**: hardware-backed key use, non-exportability, biometric gating, attestation
    challenge handling, local storage, screenshots, deep links, debug/release differences.
15. **Delivery**: workflow `permissions`, fork/untrusted PR handling, expression injection,
    unpinned actions, secret scope, image signing/provenance, container user and capabilities,
    network policy and RBAC.

### 6.3 Tooling (inputs to manual review)

Run, with pinned versions and rulesets recorded in the manifest:

- SAST for C#/.NET and TypeScript/JavaScript (for example CodeQL and Semgrep with the
  security rulesets), `dart analyze` plus a Flutter-aware SAST, and a Kubernetes/IaC/workflow
  linter;
- SCA across .NET, pnpm, Flutter/native and container layers, producing an SPDX or CycloneDX
  SBOM;
- secret scanning over the current tree and, where authorized, history;
- container image scanning on the exact digests deployed.

Every tool hit is triaged by hand to confirmed, not reachable, false positive or needs runtime
check, with the reason recorded. Suppressions are listed in the report. A tool's silence is not
evidence of absence.

### 6.4 Code-review ledger (required in every report)

| File (repository-relative) | Commit | Reviewer | Lines | Checks applied (6.2 numbers) | Result | Finding / test IDs | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `backend/Biatec.Chat.Core/...` | `<12-char short>` | `<initials>` | `<n>` | 1,2,3,4,9,10 | Reviewed / Reviewed with findings / Sampled / Not reviewed | `BCHAT-00x`, `TEST-0xx` | |

Exit criteria for the code audit: 100% of files under section 6.1 paths are `Reviewed` or
`Reviewed with findings`; each has at least one named reviewer; every Critical/High code finding
has a second reviewer; every tool hit is triaged; the ledger reconciles with the coverage matrix.

## 7. Mandatory technical test objectives

For each item record preconditions, method, expected result, actual result and evidence ID, and
add it to the harness as a `TEST-###` entry. Every security-critical control needs at least one
valid-use and one rejected-use case; exercise boundary and concurrency variants wherever state
or money is involved.

### 7.1 Identity, tenant isolation and browser boundary

- Prove access, refresh and ID tokens never reach browser responses, URLs, redirect locations,
  HTML, JS, storage, SSE events, errors, logs or evidence exports. Review logout as well as login.
- Test PKCE S256, state/nonce single use and session binding, exact redirect validation,
  issuer/audience/resource checks, JWKS rotation, expiry, malformed JWTs and session fixation.
- Test missing/narrowed `sign` and `manage-limits` scopes and scope elevation. `rekey` and
  unsupported capabilities must remain unavailable. Refresh must not extend absolute session
  lifetime or confuse subjects, clients, tenants or concurrent token rotations.
- Check cookie attributes, logout, idle and absolute expiry, refresh failures, sweeper races,
  pending revocation and Redis loss. Verify actual upstream revocation support separately from
  deletion of local vault entries; document the residual lifetime of previously stolen tokens.
- Test tenant derivation, forwarded host/proto handling behind ingress, path-mounted deployments,
  origin/client registration, exact CORS allow-lists, CSRF, missing/null Origin, popup source/origin
  checks, login/elevation navigation guards and open redirects.
- Exercise direct identifiers across tenants/users for previews, conversations, evidence,
  settings, safes and admin operations. Test the API directly even when the UI hides an action.
- Audit markdown/HTML and link handling, unsafe navigation, message forgery and confused consent
  displays. Shadow DOM and the Confirm button do not isolate the widget from hostile host JS;
  quantify what a malicious host can do below phone-approval thresholds.

### 7.2 Financial integrity, signing and business logic

- Trace every financial capability from registration and model invocation through preview,
  confirmation, consent, signing, verification and broadcast. Signing must go directly from
  backend to OIDC `POST /wallet/{network}/{address}/sign`, never MCP `signTransaction`.
- Verify builders are the approved MCP/library integrations, not hand-assembled backend groups.
  Inject malformed or malicious builder responses using local doubles; independently decode
  transactions and verify source ownership, destination, amount, asset decimals, network/genesis,
  fees, group order, application IDs and forbidden rekey/close fields.
- Prove the exact transaction intent remains bound across preview, phone facts, signed bytes
  and submission. Change one field at a time; include hidden extra transactions and signed-return
  substitutions. For Algorand independently recompute the ordered group ID with the SDK.
- Exercise the specification's 600-second minimum remaining confirmation window at just below,
  exactly at and just above the boundary, with a controlled clock. Rebuilds need new confirmation;
  landed steps must not be rebuilt or resubmitted. Check stale prices, slippage and chain state.
- For enabled Bitcoin paths, verify PSBT input values/scripts and canonical intent commitments,
  not an assumed stable unsigned txid; apply the specified expiry and changed-fee/input rules.
- Test double-clicks, replay, retry-after-timeout, concurrent confirms across replicas, partial
  submission and ambiguous network outcomes. Observe signatures, submissions and durable cap
  reservations, not merely HTTP errors; retries must not create duplicate financial effects.
- Test threshold/cap boundaries, rounding, overflow, negative/zero/large values, precision loss,
  unknown assets, stale or missing valuation and rollover periods. Use declared policy values,
  not auditor assumptions; cover per-user, per-client, per-tenant and Safe-specific counters.
- Exercise Safe propose/approve/execute separately: expected payload version, threshold/signers,
  proposal replacement, separately previewed funding, approval valuation and execute rate caps.
  Distinguish the approver's wallet spend from authorization of a Safe's spend.
- Test missing devices, always-ask, pause and analyze/prepare modes at every relevant entry point.
  If required consent is unsupported or ignored, submission must be refused, not downgraded.
- Assess x402 if enabled: decoded amount versus advertised price, payee/network binding,
  repeated challenges and SSRF/redirect boundaries. If disabled, prove bypass requests are rejected.

### 7.3 Consent protocol and mobile authenticator

- Verify consent binds request ID, subject, client, network/group or limits-change hash, decision,
  expiry, binding code and displayed authorization details. Mutate each binding independently.
- Validate canonicalization and signature verification with independent vectors: duplicate keys,
  numeric/Unicode ambiguity, unsupported algorithms, wrong keys, missing fields and expired keys.
- Test CIBA polling intervals, cancellation, denial, replay, expiry, challenge substitution,
  first-use/cooling-off requirements and consumption only when the authorized action proceeds.
  An elevated action must not elevate the whole session or grant extra scopes.
- Assess trustworthy client display names/logos, unverified-client messaging, push payload
  minimization and authenticated pending-request retrieval. The trusted phone card must derive
  from independently decoded facts, not arbitrary chat text or an unverified client description.
- On Android and iOS physical devices verify native key generation, non-exportability, signing
  authorization, attestation/challenge validation, biometric cancellation, lock-state behavior,
  backup/restore, reinstall, device loss, revocation, recovery and adding a second device.
- Review local storage/logs, screenshots/app switcher exposure, deep links, inter-app access,
  transport validation and release/debug differences. Separate ordinary-device risk from
  attacks requiring root/jailbreak; document expected behavior on unsupported hardware.
- Test lowering versus raising limits, trusted-client changes, enrollment prerequisites and
  recovery-code presentation. Never use real recovery material as report evidence.

### 7.4 AI, tools, public chat and resource abuse

- Treat the model, host context, KB, on-chain notes, token metadata and tool results as untrusted.
  Test attempted instruction laundering, tool-output manipulation, cross-tenant context/cache
  leakage, sensitive-data extraction, unauthorized tool calls and oversized/recursive tool output.
- Force forbidden tool calls with a deterministic test double. Verify tier, scope, schema,
  source ownership, feature flag, agent mode and pause checks independently of model behavior.
  A refusal in natural-language output alone is not an authorization control.
- Record both model-level behavior and downstream effects. Count a security breach only when
  a defined boundary is crossed; distinguish harmful proposals, blocked attempts and executed
  actions. Report repeated-trial variability and false positives on benign tasks.
- Assess operator KB upload/reload permissions, content provenance, size/type bounds, stale
  replicas and retrieval isolation. Guardrails and untrusted-data delimiters are defense in
  depth; test policy gates with guardrails disabled or unavailable in the isolated environment.
- Test public continuation-token replay/adoption, anonymous/authenticated transitions, stream
  cancellation, bounded tool loops, token budgets, per-tenant/global breakers and throttling.
  Evaluate approved Redis-outage and multi-replica scenarios for fail-open cost/abuse exposure.

### 7.5 Infrastructure, dependencies and delivery

- Review actual IAM, Kubernetes RBAC, service accounts, secret access and network reachability.
  Verify least privilege between API, builder, data stores and deployment identities.
- Validate ingress TLS, trusted proxies, path routing, CORS/header behavior, egress and SSRF
  controls, body/stream limits, container privileges, image provenance and exposed diagnostics.
- Assess vault master-key access and rotation, optional KMS versus deployed secret provider,
  encryption/nonces/associated data, backups, Redis/PostgreSQL authentication and restore paths.
  Do not credit a configured-off KMS provider as protection in the actual deployment.
- Review workflow permissions, fork/untrusted PR boundaries, script injection, pinned actions,
  package/image publishing, environment approvals, deployment secrets, promotion and rollback.
  Assess build scripts and dependency installation without running untrusted lifecycle scripts
  in an environment containing credentials.
- Inventory .NET, npm/pnpm, Flutter/native, container and transitive dependencies. Include SBOM,
  local secret scanning (authorized history as well as current tree), reachable advisories,
  integrity/provenance and unsupported runtimes. Do not report every scanner hit as exploitable.
- Test named operator identity, privilege separation, onboarding DNS proofs, domain takeover
  conditions, admin policy changes, key rotation and signed administrative audit records.

### 7.6 Evidence, privacy and operational recovery

- Independently validate hashes, canonicalization, signature inputs, JWKS trust/rotation,
  ordering, truncation detection and cross-conversation substitution. A signed log does not
  prove that the original financial action was authorized.
- Verify Merkle proofs/anchors against an independently trusted chain source where supplied.
  Test missing, delayed and inconsistent anchors and legacy formats. Identify unanchored windows
  and the trust assumptions when both the bundle and verification keys come from the operator.
- Check export authorization and offline verifier behavior on invalid, incomplete and partially
  verifiable bundles. Errors must not become success-shaped verification output.
- Test erasure/retention against source, replicas, caches, backups and retained receipts.
  Identify whether remaining subject identifiers permit linkage; distinguish payload deletion
  from anonymization. Validate configured retention against an owner-approved data policy;
  obtain legal advice for regulatory conclusions rather than inventing them.
- In isolation test restart, partial DB/Redis outages, restore, clock skew and key loss/rotation.
  Verify alerting for rejected signing, consent anomalies, budget abuse and audit-chain failures;
  record observed response capability and agreed recovery objectives.

### 7.7 Documented limitations that require independent confirmation

The current architecture/threat documentation describes mock-only OIDC consent APIs, deferred
revocation/shorter refresh lifetimes, hardware keystore code not yet validated on devices, and
partial evidence anchoring/export integration. These are **leads and assurance limitations, not
new audit findings or assumed current facts**. Recheck them at the audited revision. Each one is
already carried in the [risk register](risks.md); confirm, re-rate or close the entry.

For every external dependency/fallback record: expected contract, current production capability,
mock behavior, configuration gate, fallback, residual risk, responsible external owner and
evidence. Where the fallback matrix says "None", verify the feature stays disabled against the
real service. A mock pass never establishes real-device, real-IdP or production assurance.

## 8. Coverage, evidence and finding quality

Maintain the coverage matrix inside the report, with artifact links for detailed test output:

| Test ID | Asset / control / standard version + ID | Threat / prerequisite | Method | Environment + revision | Expected / observed | Result | Harness entry | Evidence / finding ID |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| TEST-001 | Fill with exact control | Fill | M1-M9 | Fill | Fill | Pass / Fail / Partial / Not tested / Not applicable | `audits/harness/<tag>/<report>/TEST-001` | Fill |

- "Pass" requires observed evidence of the stated control in the stated environment. Source-only
  analysis can pass a source-review objective, not a runtime or hardware objective.
- "Partial" and "Not tested" must state the reason, impact on assurance, required access and
  follow-up owner/date. "Not applicable" needs a rationale approved by the technical lead.
- Report counts and denominators by surface, methodology and criticality. Applicable controls =
  Pass + Fail + Partial + Not tested. Completion percentage = (Pass + Fail) / applicable controls;
  disclose partial coverage separately. Never omit untested controls to inflate the percentage.
- Resolve every mandatory objective to a row. Inventory all security-sensitive routes and
  capabilities; fully exercise signing/consent paths rather than sampling them. Any sampling of
  lower-risk variants needs a documented population, selection rationale and approval.
- Every row has a harness entry or an explicit `device` / `live` / `staging` marker with
  recorded evidence (section 5a).
- Manually validate tool output. Distinguish confirmed vulnerabilities, design risks, observed
  control gaps and unconfirmed hypotheses. Put hypotheses and rejected false positives in a
  separate appendix, with needed evidence; never present them as confirmed exploits.
- Group duplicate instances by root cause without hiding affected endpoints or tenants.
  Preserve stable finding IDs across drafts, vendors' joint reports, retests and cycles.
- Record negative results and effective controls as well as failures. Explain what was tried,
  why it failed, and what remains unproven.

## 9. Mandatory Markdown report contract

### 9.1 Location and time-sortable filename

Every delivered draft, final, retest and addendum must be a UTF-8 `.md` file directly under
[`audits/reports/`](reports/). A PDF, dashboard, spreadsheet or scanner output is supplemental,
not a replacement. Deliver a consolidated final report even if specialists submit separate files.

Use exactly:

```text
YYYYMMDDTHHMMSSmmmZ_<auditor-tag>_<git-short>_<kind>_vNN.md
```

- Timestamp: UTC publication time, fixed-width including three millisecond digits. Lexical
  filename order is publication order, regardless of the auditor's timezone.
- `auditor-tag`: stable registered organization/team tag, 2-32 lowercase ASCII letters/digits/
  hyphens, beginning with a letter or digit. Map it to named assessors and reviewer in the report.
- `git-short`: output of `git rev-parse --short=12 <audited-commit>` (12 or more characters
  when Git needs them for uniqueness). This is the **last commit actually assessed**, not a
  later report-only commit, moving branch tip, or remediation commit that was not retested.
- `kind`: `draft`, `final`, `retest` or `addendum`.
- `vNN`: two-digit revision, `v01` through `v99`, within the engagement; do not reuse a filename.
  Publish corrections with a new timestamp/revision and a `supersedes` link; never silently
  overwrite delivered results. Split before exhausting the revision range.

Illustrative filenames only, not evidence that these revisions were audited:

```text
20261022T063000000Z_example-labs_a1b2c3d4e5f6_final_v01.md
20261105T140215123Z_example-labs_b2c3d4e5f6a7_retest_v02.md
```

Filename shape (also validate that the timestamp is a real UTC date):

```regex
^[0-9]{8}T[0-9]{9}Z_[a-z0-9][a-z0-9-]{1,31}_[0-9a-f]{12,40}_(draft|final|retest|addendum)_v(0[1-9]|[1-9][0-9])\.md$
```

Capture provenance from a clean, pinned checkout before testing:

```powershell
git rev-parse --verify HEAD
git rev-parse --short=12 HEAD
git status --porcelain=v1
(Get-Date).ToUniversalTime().ToString("yyyyMMdd'T'HHmmssfff'Z'")
```

Empty status output means clean at that instant, not that a deployment matches it. Record base
and end commits for a range, container/package digests, mobile build IDs, configuration/feature
flag digests and deployed revision separately. Do not put secret values in configuration evidence.
If a dirty build is unavoidable, mark it explicitly, preserve an approved sanitized patch and
its digest, and scope conclusions to that artifact; do not describe it as a clean commit audit.
For multiple target revisions, use the primary assessed repository revision in the filename and
map every test/finding to its actual build. A retest names its newly tested revision.

### 9.2 Required report skeleton

Use this structure, filling every field or providing an explicit reason it is unavailable.
Metadata placeholders below are not actual assessment results.

````markdown
---
title: "Biatec Chat independent security assessment"
engagement_id: "<sponsor-issued ID>"
report_id: "<unique immutable ID>"
cycle: "<cycle number, 1 for the first cycle>"
kind: "final"
revision: "v01"
classification: "Confidential - security sensitive"
published_at_utc: "<ISO 8601 UTC timestamp matching filename>"
test_started_at_utc: "<timestamp>"
test_ended_at_utc: "<timestamp>"
auditor_tag: "<registered-tag>"
auditor_organization: "<legal name>"
assessors: ["<name and role>"]
independent_reviewer: "<name>"
harness_rerun_by: "<name of the second assessor who re-ran the harness>"
repository: "scholtz/biatec-chat"
audited_commit: "<full 40-character SHA>"
audited_short_commit: "<git short hash matching filename>"
baseline_commit: "<previous cycle's audited_commit; same as audited_commit for cycle 1>"
worktree_state: "<clean or dirty; explain dirty artifacts below>"
target_environment: "<local / staging / production-read-only; exact inventory below>"
harness_path: "audits/harness/<auditor-tag>/<report-basename>/"
roe_reference: "<approved SOW/ROE ID, revision and approval date>"
supersedes: null
retests_report: null
risk_register_updated_at_utc: "<timestamp of the audits/risks.md update for this report>"
disclosure_approval: "<restricted delivery only, or approval reference>"
---

# Executive summary
Business objectives, loss/exposure scenarios, effective controls, material limitations,
counts by severity and status, release recommendation and decisions required.

# Scope, authorization and provenance
In/out-of-scope inventory, test window, roles/accounts (no credentials), revisions,
deployment/artifact digests, configuration/flags, third-party permissions, assumptions.
Diff from baseline (changed files, dependencies, infrastructure) for cycle 2 onwards.

# Architecture and threat model
Assets, trust/data flows, attacker models, abuse paths and external dependencies.

# Methodology and coverage
Pinned standards/tools, manual versus automated work, matrix from section 8,
baseline failures, tests run/not run, sampling and effects of limitations.

# Source code audit
Code-review ledger from section 6.4, tool runs and triage, suppressions, exit-criteria status.

# Findings summary
| ID | Severity | CVSS v4 score/vector or justified N/A | Confidence | Affected asset | Business impact | Status | Owner | Target date | Risk ID |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |

# Detailed findings
One section per stable ID, using section 9.3 of the commissioning instructions.

# Attack chains and effective controls
Separate observed results from simulated assumptions; reference finding/test IDs.

# Remediation and release decision
Prioritized work, compensating controls, dependencies, responsible owners,
target dates, unresolved risk and named acceptance decisions.

# Retest ledger
Original ID and report, old/new commits, original/variant tests, evidence,
status, remaining risk and independent reviewer. State "not yet retested" if initial.

# Re-run ledger
Test ID, runner, date, machine, outcome and deviations from the independent harness re-run.

# Risk register changes
Entries added, re-rated, closed or expired in audits/risks.md as a result of this report.

# Limitations and residual risk
Missing environments/devices, mock-only results, unavailable access, excluded suppliers,
untested controls, point-in-time limitations and expiry/review date for accepted risk.

# Appendices
Run manifest (section 5a pinned inputs), evidence manifest (IDs/digests/access references),
test inventory, SBOM/provenance, tool versions/options, false positives/unconfirmed
hypotheses, sources, report revision history, cleanup record and
assessor/reviewer/sponsor delivery acknowledgments.
````

The narrative and matrices must be understandable without a vendor portal. Use relative links
to delivered sanitized artifacts and persistent restricted evidence IDs where access is needed.
Use repository-relative source paths plus line numbers and immutable commit links; line numbers
without a revision are not sufficient. A report can legitimately contain zero confirmed findings,
but it must still contain coverage, the code-review ledger, limitations, evidence and residual risk.

### 9.3 Required fields for each finding

1. Stable ID (for example `BCHAT-001`), concise title, category, affected components/endpoints,
   full source/build revision and exact source locations.
2. Status and confidence: High / Medium / Low with evidence-based rationale, separate from
   severity. Specify confirmed vulnerability, design risk or control gap.
3. Severity and CVSS v4.0 score **with full vector**, scoring nomenclature (`CVSS-B`, `CVSS-BT`,
   `CVSS-BE` or `CVSS-BTE`) and metric rationale. Use justified N/A for non-vulnerability
   observations rather than inventing a score. Identify CWE and versioned standard IDs when applicable.
4. Attacker prerequisites, access/role, trust boundary, reachability, required configuration,
   affected population and whether any upstream assumption is mock-only.
5. Root cause, expected behavior versus observed behavior, and sanitized deterministic
   reproduction steps with tools/versions, fixtures and minimal requests or test code. The
   reproduction is a harness entry (`TEST-###`) that fails while the finding is open and passes
   once it is fixed.
6. Evidence IDs and integrity hashes; actual observed outcome, repeated-trial results where
   relevant, and clear separation between demonstrated and potential impact.
7. Technical impact and business risk: unauthorized value movement, blast radius, affected
   assets/tenants, credential lifetime, privacy, downtime/cost and evidence integrity.
   Label any financial loss estimate with assumptions, bounds and uncertainty.
8. Concrete root-cause remediation, temporary containment, tradeoffs, owner, dependencies,
   target date and exact verification/regression tests required to close it.
9. Discovery/notification timestamps, independent reviewer, related findings/attack chains,
   and retest history including fix commit, tested build, result and remaining limitations.
10. Risk register entry (`RISK-###`) the finding maps to, created if none exists.

Never paste live secrets or personal data into reproduction steps. Keep sensitive exploit
details in the restricted copy; public summaries require sponsor-approved disclosure timing.

## 10. Severity, prioritization and remediation

Use FIRST CVSS v4.0 for technical severity: Critical 9.0-10.0, High 7.0-8.9, Medium 4.0-6.9,
Low 0.1-3.9, None 0.0. Informational observations may have no CVSS score. Provide the score
and vector together, and explain any departure between technical severity and business priority.
CVSS is not a complete business-risk or expected-financial-loss measure.

Prioritize confirmed unauthorized signing, consent bypass, cross-tenant credential access,
privileged supply-chain compromise and broad sensitive-data exposure by actual prerequisites
and blast radius, not by labels alone. Findings needing administrator control must not be
presented as unauthenticated attacks. A chained scenario must cite its component findings and
must not double-count independent losses.

Proposed response objectives, ratified in the SOW:

| Priority | Notification | Containment / decision | Remediation objective |
| --- | --- | --- | --- |
| Critical | Within 1 hour of confirmation | Immediate escalation; agreed containment within 24 hours | Fix and independently retest before affected release/use resumes |
| High | Within 1 business day | Owner and plan within 2 business days | Within 14 calendar days and before affected release unless explicitly accepted |
| Medium | Weekly update at latest | Owner and plan within 5 business days | Within 30 calendar days |
| Low | Draft report at latest | Include in prioritized backlog | Within 90 calendar days |

Deadlines are sponsor requirements for this engagement, not claims that a cited standard mandates
them. Do not close a finding as fixed merely because a deadline passed or a patch was merged.
Permitted lifecycle states: Open, In remediation, Ready for retest, Fixed (verified),
Partially fixed, Not fixed, Cannot retest, Risk accepted, False positive (with reviewer rationale).
Risk acceptance needs a named accountable owner, justification, compensating controls and expiry;
it does not change the technical finding to "Fixed". Every risk acceptance is recorded in the
[risk register](risks.md) with its expiry date.

## 11. Delivery, acceptance and retesting

Deliver a kickoff plan, weekly progress/coverage updates, urgent notifications, draft report,
consolidated final Markdown report, the harness, the risk register update, technical readout and
two retest reports. Include actionable regression cases and sanitized supporting artifacts; agree
licensing so Biatec can retain and run the tests. Do not require a continuing vendor subscription
to read results or run the harness.

The CTO accepts the **audit deliverable** only when:

- [ ] Every agreed asset and objective appears in the coverage matrix, including incomplete work.
- [ ] Every applicable critical-path control is tested or listed as an explicit release blocker;
      material missing access is not disguised as a successful assessment.
- [ ] The code-review ledger shows every security-critical file (section 6.1) reviewed by a
      named reviewer, and every tool hit triaged.
- [ ] Confirmed findings have reproducible evidence, affected revisions, severity rationale,
      remediation and closure tests; Critical/High findings have independent review.
- [ ] The harness exists at the declared path, runs from a clean checkout of the audited commit,
      and the independent re-run ledger shows it was executed by a second assessor.
- [ ] No untriaged scanner output or unsupported exploit assertions remain in the findings list.
- [ ] The exact filename, metadata, source/artifact provenance, evidence manifest and secure
      delivery requirements are satisfied; secrets and personal data are absent.
- [ ] A second assessor has checked factual consistency, counts, links, redaction and conclusions.
- [ ] Mock/dependency/device limitations and residual risk are explicit and the release
      recommendation follows the evidence.
- [ ] The risk register is updated: every open finding, accepted risk, untested control and
      mock-only gap has an entry with an owner and a review date.
- [ ] Owners and dates are assigned; both retest rounds and cleanup/retention obligations are scheduled.

Acceptance of a complete adverse report is not approval to release the product. Recommend:

- **No-go** where Critical/High financial, identity or tenant-boundary risk remains uncontained,
  or critical production/device/dependency assurance is missing.
- **Conditional go** only with explicit restricted scope, compensating controls, named risk
  acceptance and expiry; never substitute a waiver for legally required controls.
- **Go within assessed scope** only when the agreed release gates are met, with residual
  limitations and the point-in-time nature of the assessment stated.

For each retest pin the remediation build and configuration, repeat the original reproduction,
test sibling endpoints/variants, and run relevant valid-use regressions. Record observed results,
not a developer assertion. Retain original IDs and link the initial report; new weaknesses get
new IDs. Changed deployment or upstream behavior outside the tested delta requires explicit
additional scope. If access is unavailable, use "Cannot retest", never "Fixed".

## 12. Risk register

[`audits/risks.md`](risks.md) is the living register of security risks for Biatec Chat. It
outlives any single report: reports are point-in-time, the register is current state. The
register's own header explains its format, ID scheme, rating scale and statuses.

Rules binding on every audit cycle:

- **Every** open or risk-accepted finding, every control marked Partial or Not tested, every
  mock-only assurance gap and every excluded supplier maps to exactly one `RISK-###` entry.
  Create the entry if it does not exist; otherwise update it. Finding IDs and test IDs are
  recorded on the entry.
- Risk IDs are never reused or renumbered. A closed risk stays in the register with its
  closure evidence.
- The auditor proposes the rating and treatment; the CTO (or a named delegate) assigns the
  owner and decides acceptance. An accepted risk carries an expiry date; an expired acceptance
  reverts the status to Open at the next cycle and is reported as such.
- The register is updated in the same change as the report that justifies the update, and the
  report's "Risk register changes" section lists what changed.
- Between cycles, engineering may add entries (for example a newly discovered dependency
  issue) and mark them "Identified, not yet audited". The next cycle confirms or closes them.
- The register never contains secrets, exploit details beyond a one-line description, or
  customer data. Detail lives in the restricted report.

## 13. Research basis and pinned references

Sources researched on 2026-10-22. These instructions synthesize the sources; the scope,
deadlines, repeatability requirements and acceptance gates are Biatec-specific procurement
requirements. Use the pinned edition below, or document an agreed newer edition at kickoff. For
living websites, record the accessed date and release/commit used so results remain reproducible.

| Reference | Application to this engagement |
| --- | --- |
| [NIST SP 800-115, Technical Guide to Information Security Testing and Assessment (2008)](https://csrc.nist.gov/pubs/sp/800/115/final) | Planning, authorized assessment methods, execution, analysis and mitigation; use its ROE approach, supplemented with modern application/cloud/AI controls |
| [OWASP WSTG v4.2, Reporting](https://wstg.owasp.org/v4.2/5-Reporting/) | Executive and technical audiences, scope/limitations, report revision history, evidence, remediation, retest context and protected distribution; guidance, not mandatory certification rules |
| [OWASP ASVS 5.0.0 (May 2025), pinned release](https://github.com/OWASP/ASVS/tree/v5.0.0) | Versioned, testable web/backend requirements; Level 2 baseline plus explicitly selected Level 3 controls |
| [OWASP Code Review Guide v2](https://owasp.org/www-project-code-review-guide/) | Structure of the manual source code audit; checklist supplement to section 6, not a replacement |
| [OWASP API Security Top 10 (2023)](https://owasp.org/API-Security/editions/2023/en/0x11-t10/) | API object/function authorization, resource consumption, business-flow and external-service risks |
| [OWASP MASVS](https://mas.owasp.org/MASVS/) and [MASTG](https://mas.owasp.org/MASTG/) | Mobile control inventory and practical device testing; pin the release/test IDs at kickoff, not obsolete MASVS level labels |
| [OWASP Top 10 for LLM Applications (2025)](https://genai.owasp.org/resource/owasp-top-10-for-llm-applications-2025/) | Prompt injection, disclosure, unsafe output handling, excessive agency, retrieval and resource-abuse scenarios |
| [NIST AI 600-1, Generative Artificial Intelligence Profile (July 2024)](https://www.nist.gov/publications/artificial-intelligence-risk-management-framework-generative-artificial-intelligence) | Contextual AI risk governance, evaluation design, measurement and limitations; companion to AI RMF, not a penetration-testing certification |
| [RFC 9700, OAuth 2.0 Security Best Current Practice (January 2025)](https://www.rfc-editor.org/rfc/rfc9700.html) | OAuth threat assumptions, redirect/client/token handling and current security practices |
| [FIRST CVSS v4.0 specification](https://www.first.org/cvss/v4.0/specification-document) and [user guide](https://www.first.org/cvss/v4.0/user-guide) | Reproducible technical severity with score/vector/nomenclature; distinguish vulnerability severity from business risk |
| [MITRE CWE](https://cwe.mitre.org/) | Root-cause classification, not a replacement for exploit evidence or severity analysis |
| [SLSA v1.0](https://slsa.dev/spec/v1.0/) | Build provenance levels for the source -> build -> artifact trace in M7; report the observed level, do not claim one |

Where product protocol claims reference CIBA, RFC 9396, RFC 9470, RFC 8785, FAPI or attestation
drafts, obtain the exact editions cited in the [specification](../PRODUCT-DESCRIPTION.md) and
[consent protocol](../packages/consent-protocol/), map applicable clauses, and record deviations.
Do not infer conformance from terminology or from a test against this repository's own mock.
