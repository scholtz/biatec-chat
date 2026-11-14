# Threat model

Companion to [architecture.md](architecture.md) and to the "Threat Model Highlights" of
[PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md), which wins on any conflict. The table is honest about what the code on
`main` enforces today, so each control has a status:

- **Implemented**: code and tests on `main`. The file is named.
- **Partial**: the building block exists, the part that makes it effective end to end does not.
- **Planned**: specified, not written. It is not a control yet.
- **Depends on Biatec OIDC**: enforced by another product; this repo only relies on it. Until it ships we run the fallback in
  [required-changes-biatec-oidc.md](required-changes-biatec-oidc.md), and the residual risk is stated.

## Assets and trust boundaries

| Asset | Where it lives | Who may touch it |
| --- | --- | --- |
| Access, refresh and ID tokens | Redis vault, encrypted | Backend only. Never the browser, logs or evidence |
| Wallet keys | Biatec OIDC | Not in this product at all |
| Tenant OIDC client secrets | Secret store | Backend process; the database holds only a `secret://` reference |
| Evidence signing key and anchor account key | Evidence key store, secret store | Backend; the public keys are published at `/.well-known/evidence-jwks.json` |
| Transcripts and evidence | PostgreSQL | Backend; users export their own |
| The user's funds | On chain | Moved only by a transaction group Biatec OIDC signs |

Boundaries: the host page and its scripts are untrusted relative to the user's funds; the model is untrusted relative to policy;
content from outside the user (knowledge-base pages, token names, on-chain notes, page context) is untrusted data. The only
decision points for spending are Biatec OIDC (limits, consent threshold, first-use, step-up) and the user's phone.

## Threats and controls

| # | Threat | Control | Status | Where / residual risk |
| --- | --- | --- | --- | --- |
| 1 | XSS on a host page steals tokens | No token ever reaches the browser. The cookie is `__Host-` prefixed, `HttpOnly`, `Secure`, `SameSite=Lax`, host-only | Implemented | `Sessions/SessionService.cs`. Fund-moving actions above threshold additionally need the phone, which is **depends on Biatec OIDC** and mock only today |
| 2 | Malicious host page drives the widget to act | The widget shares the page's JavaScript realm, so the Confirm click is not a boundary against the host. Real boundaries: Biatec OIDC spending limits and consent threshold; tenant-level soft cap per user and rolling hour; safe approval and execution caps; the reduced cap for users without an active device; agent mode and pause; user option "always ask" | Implemented | `Caps/`, `Tools/Caps/` (`SpendCaps.cs`, `NoDevice/`), `Agent/`, `Chat/Confirm/`. Residual: below-threshold spend inside OIDC's limits and the soft cap. The soft cap counts the backend's own predicted value until Biatec OIDC returns `valuedEur` (fallback row 11) |
| 3 | Cross-site request forgery against state-changing endpoints | Custom header plus tenant origin allow-list plus registered client key on every non-GET; a missing `Origin` is refused; exact-origin credentialed CORS, never a wildcard | Implemented | `Security/CsrfMiddleware.cs`, `Security/CorsMiddleware.cs`, tests `Tenancy/TenancyTests.cs` |
| 4 | Login CSRF (attacker binds their account to the victim's session) | `state` is single use and bound to the cookie session; a callback whose state belongs to another session is rejected | Implemented | `Auth/AuthStateStore.cs`, `Auth/AuthEndpoints.cs`, `Auth/AuthTests.cs`. Must never be relaxed |
| 5 | Forced login or forced scope elevation from a hostile page | `/auth/login` and `/auth/elevate` are navigations: guarded by `Sec-Fetch-Site/Dest/Mode` checks and by a single-use popup nonce created by a script call that itself passes the CSRF checks; the OIDC consent screen names the scope | Implemented | `Auth/AuthEndpoints.cs`, `Auth/Elevation/`. `/auth/callback` is exempt from the cross-site check by design and protected by state binding and PKCE |
| 6 | Authorization code interception or replay | Authorization code with PKCE S256 on every login, `nonce` validated on the ID token, `resource` indicator on every token request, new session id after login (fixation defence) | Implemented | `Auth/OidcClient.cs`, `Auth/IdTokenValidator.cs` |
| 7 | Scope creep, `rekey` requested | `/auth/elevate` accepts exactly `sign` or `manage-limits`; every elevation asks for the union of held scopes plus the new one; scope check before every signing or limits call | Implemented | `Auth/Elevation/ElevationScopes.cs`, `Core/Capabilities/ToolGate.cs` (`CheckSigningScope`). Replacing the old refresh token at the IdP needs RFC 7009 (Biatec OIDC) |
| 8 | Prompt injection through documents, token names, on-chain notes, page context | Tier and scope gates are enforced server-side in `ToolGate` on every tool call: unknown, tier-too-low, scope-missing, feature-off and schema-invalid calls are rejected and recorded as policy events, whatever the model produced. Anonymous requests list only the anonymous tools (`requestSignIn`, `kb.search`, `market.snapshot`). Tool results and page context reach the model wrapped as `<untrusted_data>`, screened by Bedrock Guardrails when enabled | Implemented | `Core/Capabilities/ToolGate.cs`, `Orchestration/ToolExecution.cs`, `Orchestration/PromptBuilder.cs`, `Model/Guardrails/ToolResultGuard.cs`; tests `Policy/`. The model can still be talked into proposing an allowed action; the user's review, Confirm and the OIDC-side controls are what stop it |
| 9 | Model proposes a wallet action in the anonymous tier | No wallet tool exists in the anonymous tool list, and the gate rejects any other call | Implemented | `Core/Capabilities/`, tests `Capabilities/ToolGateTests.cs` and the tier policy suite `Policy/TierPolicyGateTests.cs`, `Policy/TierPolicyOrchestrationTests.cs` |
| 10 | Stolen vault or Redis dump | Tokens are sealed with AES-GCM, a fresh data key and nonce per seal, wrapped by the key provider; the session id is associated data so a blob copied to another session fails to open; the PKCE verifier in Redis is sealed too | Implemented | `Vault/`. Stage and production use the `Secret` provider: the master key is a GitHub environment secret written into the cluster secret `biatec-chat-generated` (k8s/README.md), so whoever can read that secret and the Redis data can open the vault. The `Kms` provider exists but is not deployed; locally a dev key is used |
| 11 | Backend compromise | Keys never leave Biatec OIDC, which enforces limits, threshold, first-use and cooling-off. The backend cannot sign. Residual: an attacker with the vault decryption role gets active sessions' refresh tokens and can spend below threshold within each user's limits | Depends on Biatec OIDC | Window is the IdP refresh lifetime: **30 days today**, no revocation endpoint (fallback rows 14 and 18). Shrinks to 8 hours only once Biatec OIDC ships per-client lifetimes and revocation |
| 12 | Tokens outlive the session | Sweeper every minute revokes then deletes idle (15 min) and expired (8 h) sessions; Redis TTL is only a backstop; failed revocation parked in `PendingRevocation` and retried; the sweeper leaves a session it cannot read | Implemented | `Sweeper/`, `Auth/Logout/Revocation.cs`, [security notes](security-notes-logout-and-revocation.md). Revocation at the IdP is a no-op until `Oidc:Revocation:Url` is set |
| 13 | ID token exposed through the logout redirect | Logout is a top-level navigation with `Sec-Fetch-*` checks, `Cache-Control: no-store` and `Referrer-Policy: no-referrer` | Implemented | `Auth/Logout/LogoutEndpoints.cs` |
| 14 | Hostile tenant onboarding (claiming a host or origin it does not control) | DNS TXT challenge on the exact host, origins on the backend's registrable domain and each proven by its own TXT, a host belongs to one tenant, rate limit and body cap on `/onboarding/*`, operator approval until confidential-client registration exists | Implemented | `Onboarding/`, `docs/required-changes-biatec-oidc.md`. Challenge is not bound to the requester (documented); per-target-host concurrency limit deferred |
| 15 | Impostor tenant borrows Biatec's name | Tenant is derived from `Host` only; a self-onboarded tenant is created with `TrustAnchorVerified = false` (only the seeded Biatec tenant is verified); a soft cap above the platform default needs the operator's trust-anchor attestation | Implemented | `Tenancy/`, `Tools/Caps/CapPolicy.cs` (`SoftCapPolicy.ValidateOnSave`), `Admin/TenantPolicyService.cs`. Display on the phone is Biatec OIDC's (fallback: Biatec's own clients pinned verified); no automatic federation check exists |
| 16 | Replay of a consent | Receipts bound to the group hash, `auth_req_id`, client id and a 300 s expiry, spent by exactly one signing call | Partial | The backend verifies every receipt itself before an action resumes (T-238, `Consent:VerifyReceipts`, on by default; `ConsentReceiptService`) and the receipt is evidence. The server-side half (step-up on the signing endpoint, reserve and spend, enforcement) is not deployed to the real Biatec OIDC (issues 25 to 28 open), so it is verified against the mock (`packages/consent-protocol/mock-oidc`) and the spec vectors only. Consent enforcement is prepared here and switched off (`Caps__*` in `k8s/stage`, [consent-rollout.md](consent-rollout.md)); until Biatec OIDC enforces it, above-threshold signing against real OIDC is not offered and the backend's own caps and no-device rule apply |
| 17 | Group altered after the user confirmed it | The preview is derived from the group; the group hash is the binding value across preview, consent and signing; the backend recomputes the group id before signing and checks the signed group against the preview; Biatec OIDC refuses a mismatch | Partial | Built and tested: `Core/Previews/`, `Core/Canonical/`, `Chat/Confirm/`, `Tools/Signing/SignedGroupVerifier.cs`. The OIDC-side hash check **depends on Biatec OIDC** (fallback row 8: none) and exists only in the mock |
| 18 | Stale preview confirmed | Preview carries its expiry; Confirm is refused below 600 s (also server-side); an expired preview is rebuilt as a new linked preview with a new hash; landed steps are never rebuilt | Implemented | `Core/Previews/` |
| 19 | Transcript tampering by an outsider | Per-entry salted payload hash, chained entry hash over conversation id and position, ES256 signature over `{entryHash, kid, signedAt}`; offline verifier; retired keys stay published | Implemented | `Core/Evidence/`, `Evidence/Jwks/`, `Evidence/Keys/`, recording in `Evidence/Recording/`, export bundle at `GET /session/evidence` (`Evidence/Export/`), verifier `packages/evidence-verifier` |
| 20 | Transcript tampering by the operator | Heads of open conversations are batched into a Merkle root and written to an Algorand transaction note every 10 minutes from a public evidence account; notes carry a sequence number, the previous anchor and gap markers; the verifier resolves every `anchorRef` that is an anchor transaction id from the chain, not from the bundle (rows written before migration `EvidenceAnchorRefTxId` keep a GUID `anchorRef`, which it skips) | Partial | `Evidence/Anchoring/`: the job, broadcaster, gap handling and verifier are built and tested against a fake algod, enabled in `k8s/base` (testnet) once the evidence-account secret exists. A conversation's first entry carries `anchorRef`; `resume` and `end` entries and the inclusion proofs in the export bundle are not wired, and **no testnet run is recorded** |
| 21 | Personal data kept too long, erasure | Hourly retention job: payloads and message content after 5 years, anonymous conversations after 30, records after 7 years; user erasure of their own conversations; payload deletion keeps the chain verifiable | Implemented | `Retention/` (`Retention` settings), `POST /session/erase`. Public `sub` is used, so deletion cannot unlink retained receipts (fallback row 15) |
| 22 | Abuse of the anonymous endpoint | Rate limits per salted IP hash and per Origin, single-use continuation tokens, adoption rules, idle close after 30 minutes, Bedrock budget breaker | Implemented | `Public/` (`PublicChat` settings), `Budget/`. Rate limits fall back to in-memory counters per replica when Redis is down |
| 23 | Bedrock cost and throttling abuse | `maxTokens` always set, jittered backoff on throttling, a bounded number of concurrent streams, Claude 4.5 only; spend capped per tenant and globally per window, with a breaker that answers anonymous sessions from cached answers or a refusal and keeps a reserved share for authenticated sessions | Implemented | `Model/BedrockModelClient.cs`, `Budget/BudgetedModelClient.cs`. The cap is checked before a call, so calls in flight can overshoot it by their own cost; a Redis outage fails open. The 50 requests per minute limit is shared by the whole team |
| 24 | Secrets in config, logs or the repo | Secrets come from environment or the secret store (`secret://` references); the database never holds a secret; vault contents are never logged | Implemented | `Auth/OidcClient.cs` (`ConfigurationSecretResolver`). In Kubernetes the secrets are GitHub environment secrets written into cluster secrets by the deploy and read as environment variables; no store that can write a secret (tenant self-service onboarding) is bound |
| 25 | Builder sidecar abused as a signing or build oracle | Shared secret header compared in constant time on `/v1/*`, builders cannot see the secret, errors return a message only | Implemented | `builders/sidecar/src/app.ts`. It never holds keys; it only returns unsigned transactions |
| 26 | Resource exhaustion with oversized bodies or by hammering the unauthenticated `/health` | Every request body is limited before any handler (`Limits:MaxRequestBodyBytes`, 64 KiB; declared lengths get 413, chunked bodies are capped by the server feature; onboarding 32 KiB and KB pages 16 KiB stay stricter). `/health` answers from a cache of `Limits:HealthCacheSeconds` (5 s) and is rate limited per remote IP; `/health/live` runs no dependency check | Implemented | `Security/RequestLimits.cs`. Residual: `/health` is still reachable through the ingress (kubelet probes use the same path); only its cost is bounded |

## Evidence signing keys: the Secret store is one master

`Evidence:KeyStore=Secret` (the Kubernetes default while no AWS KMS key exists) derives every evidence signing key with HKDF from the one
`Evidence:Secret:MasterKey` and the key id. Rotation therefore changes which key signs new entries but gives no recovery from a leaked
master: whoever holds it can derive every past and future key and forge entries whose `signedAt` falls in a key's lifetime. The anchoring
job (a Merkle root of conversation heads written to Algorand, threat 20) would bound back-dating, but it is only partly built and has no
recorded testnet run, so do not count on it yet. For production prefer `Evidence:KeyStore=Kms` (asymmetric keys the backend can use but
not export); independent secrets per key id would also help but are **not implemented**. The backend logs a warning each time the Secret
store is built (expected on the current Kubernetes deployment, which has no KMS key). Protect the master like the vault master key: a
Kubernetes secret, never in git or config maps.

## Controls this product deliberately does not own

The phone approval, the consent threshold, spending limits, first-use and cooling-off rules, the device key, and the
pairwise subject are all Biatec OIDC's and the authenticator's. The chat backend can ask for more consent (`requireConsent`)
but never for less. Anything in the table marked "depends on Biatec OIDC" is only as strong as the shipped change; until then the
fallbacks apply:

| Missing OIDC change | Fallback now | What it costs |
| --- | --- | --- |
| Consent (CIBA, step-up, receipts) | The backend verifies receipts (T-238); the server-side step-up and enforcement run against the mock only and are switched off against the real Biatec OIDC | Above-threshold signing is not offered against real OIDC until it enforces consent and the rollout of [consent-rollout.md](consent-rollout.md) is done |
| Valuation of Safe and DEX pool calls | Unknown application calls are treated as above threshold | The phone is asked more often |
| Test network valuation | Demo identities set to "always ask" with no limits | Extra phone prompts in the demo |
| Revocation endpoint, 8 h refresh | Destroy the vault entry, token ages out | 30 day residual window at the IdP |
| Pairwise `sub` | Public `sub` | Deletion cannot unlink retained receipts |
| Origin-aware slots | Slots and pauses apply per client for all Biatec sites | One compromised Biatec site can occupy the others' slots |

## The authenticator app

The authenticator (`apps/biatec_authenticator`) is the second factor for fund-moving actions and a general two-factor code
generator. Conformance to OWASP MASVS v2.0 Level 2 is in [AUTHENTICATOR-SECURITY.md](AUTHENTICATOR-SECURITY.md); the test brief is
[AUTHENTICATOR-PENTEST-BRIEF.md](AUTHENTICATOR-PENTEST-BRIEF.md). Statuses are the same four words as above, judged on the Dart code and
its tests: the native code has not run on a device (open item O-1 in the security document).

### Assets

| Asset | Where it lives | Who may touch it |
| --- | --- | --- |
| Two-factor seeds | Vault file in the app's private folder, each record sealed with AES-256-GCM under a hardware key that needs a strong biometric | The vault code, after a biometric. Never shown again, never in a backup, never on a server |
| Device (receipt-signing) key | Secure Enclave or StrongBox, non-exportable | Signs a receipt only after a biometric and the binding code |
| Device token and enrollment record | Keychain or encrypted preferences, device-bound | The app; sent to Biatec OIDC with a proof of possession of the device key |
| Approval history | Secure storage on the phone | The app; redacted of anything not needed to read a receipt |
| The user's attention at the approval card | The screen | What the card shows must be what Biatec OIDC decoded, not what the requester says |

### Attackers and abuse cases

| # | Threat | Control | Status | Where / residual risk |
| --- | --- | --- | --- | --- |
| A1 | **Stolen unlocked phone** (the thief gets the app open) | The app locks after 0, 30 s, 1 or 5 min in the background, on screen lock, and after 2 min idle; every approval, deletion and security change asks for a fresh biometric; the vault key opens only for 30 s after one | Implemented | `lib/lock/`, `test/lock/`. Residual: during the 30 s after an unlock the vault key can be used by code in the app process (open item O-9) |
| A2 | **Stolen locked phone** | Nothing opens without the fingerprint or face: no PIN or password path, no fallback, hardware keys that need the biometric and die when it changes, seeds not in backups | Implemented | `lib/lock/`, `lib/totp/vault/`. Residual: the operating system's lockout policy decides how many biometric tries an attacker gets; a coerced user can be made to unlock |
| A3 | **Malware with an accessibility service** reads the screen or taps Approve | The approval card shows facts from Biatec OIDC with a binding code the user types; a tap alone cannot approve; the secure area is hidden from other windows (`FLAG_SECURE`, overlay hiding on Android 12+); the unlock and approval need the biometric, which a service cannot supply | Partial | `test/platform_hardening_test.dart`. Residual: a service can read what the card shows and the binding code on screen; Android 10 and 11 cannot hide overlays, so a partly covered tap on Approve is refused instead (`test/security/touch_guard_test.dart`) |
| A4 | **Overlay and tapjacking** over the Approve button | Overlays hidden on Android 12+; touches through an obscuring window dropped on older versions; the amount and recipient sit on the same screen as the button | Implemented | `MainActivity.kt`, `lib/security/touch_guard.dart`. Residual: an accessibility service dispatches gestures the system trusts; not run on a device (O-1) |
| A5 | **Screen capture and recording**, screen mirroring | `FLAG_SECURE`, no recents thumbnail, a capture-aware cover on iOS; nothing is readable while locked | Implemented | `MainActivity.kt`, `ScreenCover.swift`, hardening test |
| A6 | **Rooted or jailbroken device**, debugger, Frida, emulator, repackaged app | Detectors fail closed in release builds (the secure area does not open and says a check failed, naming nothing); re-checked on resume and before each approval; the signing certificate is compared with the release digest | Partial | `lib/integrity/`, `test/integrity/`. Residual: a determined attacker bypasses client-side checks; attestation is built but not yet sent (T-234); the real defence is the hardware key and the biometric, not detection |
| A7 | **Network attacker with a trusted certificate authority** (a corporate or rogue CA, a user-installed CA) | Public-key pinning to Biatec OIDC with a backup pin; user CAs not trusted in release; no cleartext | Implemented | `lib/security/pinning/`, [AUTHENTICATOR-PINNING.md](AUTHENTICATOR-PINNING.md). Residual: a compromised pinned key until rotation |
| A8 | **Malicious QR code or link** (a crafted `otpauth://` or a QR that is not one) | One strict parser: length limits, only `totp` and `hotp`, bounded digits and periods, weak secrets refused, look-alike and direction-changing characters refused; the account is shown and confirmed before it is saved; duplicates are refused | Implemented | `lib/totp/core/otp_uri.dart`, `lib/codes/add/`. Residual: a QR for the right service but the attacker's seed is indistinguishable from a real one: the user must scan from the service's own page |
| A9 | **Poisoned backup file** (a crafted file the user is tricked into restoring) | AES-256-GCM under an Argon2id key, the header authenticated, a size and account-count limit, parameter floor and ceilings, the same account validation as a scan, every account shown before it is added, one generic failure for every refusal | Implemented | `lib/totp/backup/`, `lib/codes/backup_screen.dart`. Residual: a weak passphrase chosen by the user (the app refuses the weakest and says a lost one cannot be recovered); a stolen file can be guessed against offline at the cost of one Argon2id derivation per guess |
| A10 | **Phishing request that looks like a bank** | Biatec OIDC decodes the request, so the card shows its facts, never the requester's text; the client name is marked unverified unless Biatec OIDC verified it; the binding code ties the card to the session; a first-use and cooling-off policy is Biatec OIDC's | Partial | `lib/approvals/`. Residual: client verification (OpenID Federation) is a Biatec OIDC change, [required-changes](required-changes-biatec-oidc.md); until then every non-Biatec client shows as unverified |
| A11 | **Clipboard snooping** of a copied code | The clip is marked sensitive (Android 13+), local-only and expiring on iOS, and cleared after 30 s if it still holds the code | Implemented | `lib/codes/sensitive_clipboard.dart`. Residual: other apps on older Android versions can read the clipboard during the 30 s |
| A12 | **Device clock wrong or set back** to replay or hide codes | The Codes tab warns above 5 s of difference from the server `Date` header, and hides time-based codes when the clock is before the latest time seen | Implemented | `lib/codes/server_clock.dart`, `OtpGenerator.clockIsPlausible` |
| A13 | **Supply chain**: a vulnerable or malicious dependency | A committed lockfile that `pub get` must not change, an OSV scan and an SBOM in CI, pinned actions, signed release builds | Implemented | `.github/workflows/ci.yml`, `docs/AUTHENTICATOR-RELEASE-CHECKLIST.md` |

## Out of scope for version 1

`rekey`, asset creation, bridge transactions, and any source address the user's identity does not control are excluded from chat.
Sender-constrained tokens (DPoP) are a roadmap item, not a current claim.

## How to check the claims

- Backend: `dotnet test backend/Biatec.Chat.slnx` (Redis and PostgreSQL via `docker compose up -d`).
- Widget: `pnpm --filter @biatec/chat-widget test`. Consent protocol and mock OIDC: `pnpm --filter mock-oidc test` and `pnpm --filter @biatec/consent-protocol test`.
- Each row names the file that implements it; the matching tests sit in `backend/Biatec.Chat.Tests` under the same module name.
- Authenticator: `cd apps/biatec_authenticator && flutter test`, `dart run tool/gates.dart` and `dart run tool/matrix_check.dart`; the rows of
  the authenticator section name their files and tests, and the matrix is in [AUTHENTICATOR-SECURITY.md](AUTHENTICATOR-SECURITY.md).
