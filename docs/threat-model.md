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
| 2 | Malicious host page drives the widget to act | The widget shares the page's JavaScript realm, so the Confirm click is not a boundary against the host. Real boundaries: Biatec OIDC spending limits and consent threshold; tenant-level soft cap (planned); user option "always ask" | Partial | The soft cap and the confirm path are not built. Today nothing can sign, so there is no spend to steal; once signing lands the exposure is below-threshold spend inside OIDC's limits |
| 3 | Cross-site request forgery against state-changing endpoints | Custom header plus tenant origin allow-list plus registered client key on every non-GET; a missing `Origin` is refused; exact-origin credentialed CORS, never a wildcard | Implemented | `Security/CsrfMiddleware.cs`, `Security/CorsMiddleware.cs`, tests `Tenancy/TenancyTests.cs` |
| 4 | Login CSRF (attacker binds their account to the victim's session) | `state` is single use and bound to the cookie session; a callback whose state belongs to another session is rejected | Implemented | `Auth/AuthStateStore.cs`, `Auth/AuthEndpoints.cs`, `Auth/AuthTests.cs`. Must never be relaxed |
| 5 | Forced login or forced scope elevation from a hostile page | `/auth/login` and `/auth/elevate` are navigations: guarded by `Sec-Fetch-Site/Dest/Mode` checks and by a single-use popup nonce created by a script call that itself passes the CSRF checks; the OIDC consent screen names the scope | Implemented | `Auth/AuthEndpoints.cs`, `Auth/Elevation/`. `/auth/callback` is exempt from the cross-site check by design and protected by state binding and PKCE |
| 6 | Authorization code interception or replay | Authorization code with PKCE S256 on every login, `nonce` validated on the ID token, `resource` indicator on every token request, new session id after login (fixation defence) | Implemented | `Auth/OidcClient.cs`, `Auth/IdTokenValidator.cs` |
| 7 | Scope creep, `rekey` requested | `/auth/elevate` accepts exactly `sign` or `manage-limits`; every elevation asks for the union of held scopes plus the new one; scope check before every signing or limits call | Implemented | `Auth/Elevation/ElevationScopes.cs`, `Core/Capabilities/ToolGate.cs` (`CheckSigningScope`). Replacing the old refresh token at the IdP needs RFC 7009 (Biatec OIDC) |
| 8 | Prompt injection through documents, token names, on-chain notes, page context | Tier and scope gates are enforced server-side in `ToolGate` on every tool call: unknown, tier-too-low, scope-missing, feature-off and schema-invalid calls are rejected and recorded as policy events, whatever the model produced. Anonymous requests list only `requestSignIn` and `kb.search` | Partial | The gate and the registry are built. Wrapping tool results as untrusted data and the conversation loop that calls the gate are in the orchestrator task (in progress) |
| 9 | Model proposes a wallet action in the anonymous tier | No wallet tool exists in the anonymous tool list, and the gate rejects any other call | Implemented (library) | `Core/Capabilities/`, tests `Capabilities/ToolGateTests.cs`. End-to-end tier test suite is planned |
| 10 | Stolen vault or Redis dump | Tokens are sealed with AES-GCM, a fresh data key and nonce per seal, wrapped by KMS in AWS; the session id is associated data so a blob copied to another session fails to open; the PKCE verifier in Redis is sealed too | Implemented | `Vault/`. KMS key and role separation is a deployment concern (planned with the infrastructure tasks); locally a dev key is used |
| 11 | Backend compromise | Keys never leave Biatec OIDC, which enforces limits, threshold, first-use and cooling-off. The backend cannot sign. Residual: an attacker with the vault decryption role gets active sessions' refresh tokens and can spend below threshold within each user's limits | Depends on Biatec OIDC | Window is the IdP refresh lifetime: **30 days today**, no revocation endpoint (fallback rows 14 and 18). Shrinks to 8 hours only once Biatec OIDC ships per-client lifetimes and revocation |
| 12 | Tokens outlive the session | Sweeper every minute revokes then deletes idle (15 min) and expired (8 h) sessions; Redis TTL is only a backstop; failed revocation parked in `PendingRevocation` and retried; the sweeper leaves a session it cannot read | Implemented | `Sweeper/`, `Auth/Logout/Revocation.cs`, [security notes](security-notes-logout-and-revocation.md). Revocation at the IdP is a no-op until `Oidc:Revocation:Url` is set |
| 13 | ID token exposed through the logout redirect | Logout is a top-level navigation with `Sec-Fetch-*` checks, `Cache-Control: no-store` and `Referrer-Policy: no-referrer` | Implemented | `Auth/Logout/LogoutEndpoints.cs` |
| 14 | Hostile tenant onboarding (claiming a host or origin it does not control) | DNS TXT challenge on the exact host, origins on the backend's registrable domain and each proven by its own TXT, a host belongs to one tenant, rate limit and body cap on `/onboarding/*`, operator approval until confidential-client registration exists | Implemented | `Onboarding/`, `docs/required-changes-biatec-oidc.md`. Challenge is not bound to the requester (documented); per-target-host concurrency limit deferred |
| 15 | Impostor tenant borrows Biatec's name | Tenant is derived from `Host` only; a self-onboarded tenant is created with `TrustAnchorVerified = false` (only the seeded Biatec tenant is verified) | Partial | The flag and seed exist; the operator-only raise rules for the soft cap are planned with the soft cap; display on the phone is Biatec OIDC's (fallback: Biatec's own clients pinned verified) |
| 16 | Replay of a consent | Receipts bound to the group hash, `auth_req_id`, client id and a 300 s expiry, spent by exactly one signing call | Depends on Biatec OIDC | Verified against the mock (`packages/consent-protocol/mock-oidc`) and the spec vectors; the real endpoints do not exist yet, so above-threshold signing is unavailable against real OIDC |
| 17 | Group altered after the user confirmed it | The preview is derived from the group; the group hash is the binding value across preview, consent and signing; Biatec OIDC refuses a mismatch | Partial | Preview service, hashing and expiry are built and tested (`Previews/`, `Canonical/`). The signing call and the OIDC-side check are not |
| 18 | Stale preview confirmed | Preview carries its expiry; Confirm is refused below 600 s (also server-side); an expired preview is rebuilt as a new linked preview with a new hash; landed steps are never rebuilt | Implemented | `Core/Previews/` |
| 19 | Transcript tampering by an outsider | Per-entry salted payload hash, chained entry hash over conversation id and position, ES256 signature over `{entryHash, kid, signedAt}`; offline verifier; retired keys stay published | Implemented | `Core/Evidence/`, `Evidence/Jwks/`. Wiring from the conversation flow and the export bundle are planned |
| 20 | Transcript tampering by the operator | Heads of open conversations are batched into a Merkle root and written to an Algorand transaction note every 10 minutes from a public evidence account; notes carry a sequence number, the previous anchor and gap markers; the verifier resolves every `anchorRef` from the chain, not from the bundle | Partial | `Evidence/Anchoring/`: the job, broadcaster, gap handling and verifier are built and tested against a fake algod. Disabled by default, **no recorded testnet run**, not called from the conversation flow, and bundle integration is open. `anchorRef` holds an anchor id derived from the transaction id, not the transaction id itself (documented deviation) |
| 21 | Personal data kept too long, erasure | Anonymous 30 days; payload deletion keeps the chain verifiable | Planned | Retention jobs and user erasure are not built |
| 22 | Abuse of the anonymous endpoint | Rate limits per IP/origin, continuation tokens, adoption rules | Planned | `/public/chat` does not exist yet |
| 23 | Bedrock cost and throttling abuse | `maxTokens` always set, jittered backoff on throttling, a bounded number of concurrent streams, Claude 4.5 only; spend capped per tenant and globally per window, with a breaker that answers anonymous sessions from cached answers or a refusal and keeps a reserved share for authenticated sessions | Implemented | `Model/BedrockModelClient.cs`, `Budget/BudgetedModelClient.cs`. The cap is checked before a call, so calls in flight can overshoot it by their own cost; a Redis outage fails open. The 50 requests per minute limit is shared by the whole team |
| 24 | Secrets in config, logs or the repo | Secrets come from environment or the secret store (`secret://` references); the database never holds a secret; vault contents are never logged | Implemented | `Auth/OidcClient.cs`. A real Secrets Manager or Kubernetes secret store implementation is not written; dev uses configuration |
| 25 | Builder sidecar abused as a signing or build oracle | Shared secret header compared in constant time on `/v1/*`, builders cannot see the secret, errors return a message only | Implemented | `builders/sidecar/src/app.ts`. It never holds keys; it only returns unsigned transactions |

## Controls this product deliberately does not own

The phone approval, the consent threshold, spending limits, first-use and cooling-off rules, the device key, and the
pairwise subject are all Biatec OIDC's and the authenticator's. The chat backend can ask for more consent (`requireConsent`)
but never for less. Anything in the table marked "depends on Biatec OIDC" is only as strong as the shipped change; until then the
fallbacks apply:

| Missing OIDC change | Fallback now | What it costs |
| --- | --- | --- |
| Consent (CIBA, step-up, receipts) | Mock only | Above-threshold signing is unavailable against real OIDC |
| Valuation of Safe and DEX pool calls | Unknown application calls are treated as above threshold | The phone is asked more often |
| Test network valuation | Demo identities set to "always ask" with no limits | Extra phone prompts in the demo |
| Revocation endpoint, 8 h refresh | Destroy the vault entry, token ages out | 30 day residual window at the IdP |
| Pairwise `sub` | Public `sub` | Deletion cannot unlink retained receipts |
| Origin-aware slots | Slots and pauses apply per client for all Biatec sites | One compromised Biatec site can occupy the others' slots |

## Out of scope for version 1

`rekey`, asset creation, bridge transactions, and any source address the user's identity does not control are excluded from chat.
Sender-constrained tokens (DPoP) are a roadmap item, not a current claim.

## How to check the claims

- Backend: `dotnet test backend/Biatec.Chat.slnx` (Redis and PostgreSQL via `docker compose up -d`).
- Widget: `pnpm --filter @biatec/chat-widget test`. Consent protocol and mock OIDC: `pnpm --filter mock-oidc test` and `pnpm --filter @biatec/consent-protocol test`.
- Each row names the file that implements it; the matching tests sit in `backend/Biatec.Chat.Tests` under the same module name.
