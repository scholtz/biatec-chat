# Biatec Chat

**Conversational, consent-gated access to self-custody finance on Algorand.**

Biatec Chat is an embeddable AI assistant that lives in the bottom-right corner of Biatec web properties (Biatec Web, Biatec DEX) and, through an npm package, any third-party Vue 3 site. Visitors can ask the assistant to explain the products and the blockchain. Signed-in users can ask it to act: transfer funds between their own self-custody addresses, swap assets, add liquidity to a Biatec DEX pool, or create and govern a Biatec Safe shared account. Every action that moves user funds flows through the user's own Biatec OIDC identity, the user's own spending limits, and, above a configurable threshold, an explicit approval on the user's phone in the Biatec Authenticator app.

The assistant never holds keys, never sees long-lived tokens, and never acts outside what the user has authorized.

* Chat that discovers capabilities for anyone, and executes only for authenticated, consenting users
* A backend that keeps every credential out of the browser and every transaction in an evidence log
* A mobile authenticator and an open consent protocol so banks and other third parties can integrate without asking permission, yet users cannot be tricked by impostors

---

## Why This Product

Biatec already has the hard parts of self-custody finance:

- **Biatec OIDC** (`https://oidc.biatec.io`) is an OpenID Connect provider and a self-custody wallet API. Users sign in with Google or Microsoft, their Algorand/EVM/Bitcoin keys are derived and encrypted into their own cloud storage, and third-party clients can request transaction signing under user-controlled daily, weekly, and monthly spending limits.
- **Biatec MCP** (`https://mcp.biatec.io/tools`) exposes that wallet to AI assistants over the Model Context Protocol as an OAuth 2.1 resource server. It builds, signs, and submits payments, opt-ins, swaps, bridges, multisig, and Bitcoin transactions, chained as three separate steps so an unsigned transaction can be inspected before it is ever broadcast.
- **Biatec DEX** (BiatecCLAMM) is a concentrated-liquidity AMM on Algorand, shipped as the `biatec-concentrated-liquidity-amm` npm package with `clammAddLiquiditySender`, `clammSwapSender`, and `clammRemoveLiquiditySender` helpers.
- **Algo Safe** is a policy-driven smart account with M-of-N signer groups, transaction proposals, spending limits, and an x402 payment facilitator for the agent economy.
- **Biatec Web** is a Vue 3 + PrimeVue marketing and product site deployed to Kubernetes.

What is missing is the front door. Today a user has to know which product does what, where the buttons are, and how a transaction group is assembled. Biatec Chat turns that into a conversation, and does it without weakening any of the security properties the underlying services already enforce.

---

## Product Vision

Biatec Chat should feel like talking to a knowledgeable, careful banker who happens to be available on every Biatec page.

- A visitor should get accurate answers about what Biatec offers, without being asked to sign in.
- A signed-in user should be able to say "swap 100 USDC to EURS" or "add liquidity to the EURS/USDC pool" and see a clear preview of exactly what will happen on-chain before anything is signed.
- A high-value action should pause and appear on the user's phone, with the same amount, asset, and destination the chat showed, for an approval that cannot be faked by a malicious website or a compromised browser tab.
- A team should be able to create a Biatec Safe in chat, have one member propose a transaction, and have another member approve it from their own session.
- Every one of those actions should leave a tamper-evident trail: what was asked, what the model proposed, what the user consented to, and which transaction landed on-chain.

Longer term, the same backend, consent protocol, and authenticator serve agents that are not chat at all: scheduled automations, agent-to-agent payments over x402, and third-party banks or fintechs that want Biatec users to approve their requests on the same trusted device.

---

## Products In This Monorepo

| Product | Package / project | Stack | Purpose |
| --- | --- | --- | --- |
| Chat widget | `@biatec/chat-widget` (npm) | Vue 3, TypeScript, Vite library build | Floating chat icon and chat panel injected into Biatec Web, Biatec DEX, and any Vue 3 host |
| Chat backend | `Biatec.Chat.Api` | ASP.NET Core 10 (.NET), AWS Bedrock, Redis, PostgreSQL | OAuth 2 BFF, model orchestration, tool execution via Biatec MCP, session recording, evidence log, consent orchestration |
| Authenticator | `biatec_authenticator` | Flutter (iOS, Android) | Device-bound approval of high-value actions and sign-in; reference implementation of the Biatec Consent Protocol |

Shared pieces:

- `packages/chat-protocol`: the typed JSON contract between widget and backend (messages, suggestions, action previews, consent status), generated from the backend's OpenAPI document.
- `packages/consent-protocol`: the Biatec Consent Protocol specification, JSON schemas, test vectors, and a conformance test suite that any third-party integrator can run.
- `docs/`: architecture, threat model, integration guides, hackathon demo script.

---

## Core Concepts

### Session Tiers

Every chat session is in exactly one tier. The tier determines which tools the model may call and which prompts the backend will refuse.

| Tier | How it is reached | What the assistant may do |
| --- | --- | --- |
| **Anonymous** | Widget loads, no sign-in | Capability discovery only: explain Biatec products, Algorand, DEX concepts, fees, how to get started. No account data, no transaction building, no price-sensitive advice beyond public facts. |
| **Authenticated** | User completes Biatec OIDC login (PKCE + authorization code) through the backend | Everything above, plus read own addresses and balances, build and preview transactions, sign and submit transactions within the user's own spending limits, create safes and proposals. |
| **Consent-elevated** | A specific action exceeds a policy threshold and the user approves it in Biatec Authenticator | That one action, with the exact parameters the user approved, for a short validity window. Elevation is per action, never per session. |

An anonymous session that receives an action request is not escalated silently. The assistant explains that the request needs sign-in, shows what the action would do in general terms, and offers the sign-in button. The backend enforces this independently of the model: in the anonymous tier the tool list sent to the model contains no wallet tools, and any tool call that slips through is rejected server-side.

### Capabilities And Intents

A capability is a named, versioned thing the assistant can do. Each capability declares the minimum tier, the scopes it needs on the Biatec OIDC access token, whether it moves funds, and how its value is computed for the consent threshold.

Initial capability set:

| Capability | Tier | Scopes | Moves funds | Backed by |
| --- | --- | --- | --- | --- |
| `explain.product`, `explain.blockchain` | Anonymous | none | no | Model knowledge plus a curated knowledge base of Biatec docs |
| `wallet.listAddresses`, `wallet.balance` | Authenticated | `openid` | no | Biatec MCP `listAlgorandAddresses`, `getCryptoAddress`, `getCryptoBalance` |
| `wallet.transfer` | Authenticated | `sign` | yes | Biatec MCP `createPaymentTransaction` or `createBitcoinTransaction`, `signTransaction`, `submitTransactionToBlockchain` |
| `dex.swap` | Authenticated | `sign` | yes | Biatec MCP `createSwapTransaction` (quote aggregation across Biatec Router and other providers) |
| `dex.addLiquidity`, `dex.removeLiquidity` | Authenticated | `sign` | yes | `biatec-concentrated-liquidity-amm` transaction builders, signed through Biatec OIDC `POST /wallet/{network}/{address}/sign` |
| `safe.create`, `safe.addSignerGroup` | Authenticated | `sign` | yes (app funding) | `algo-safe` npm library |
| `safe.propose` | Authenticated | `sign` | no (proposal only) | `algo-safe` proposal builder |
| `safe.approve`, `safe.execute` | Authenticated | `sign` | yes on execute | `algo-safe` `approveProposal` / `executeProposal` |
| `limits.view` | Authenticated | `openid` | no | Biatec OIDC `GET /wallet/limits` |
| `limits.update` | Consent-elevated | `manage-limits` | no, but raises risk | Biatec OIDC `PUT /wallet/limits` |

Capabilities deliberately excluded from chat in version 1: anything requiring the `rekey` scope, asset creation, bridge transactions, and any action on an address the user has not explicitly listed as their own. These can be added later as separate capabilities with their own consent rules.

### Action Preview

Every capability that moves funds produces an **action preview** before anything is signed. The preview is a structured object rendered by the widget as a card, not free text from the model:

- Human-readable intent ("Swap 100 USDC for at least 91.80 EURS on Biatec DEX")
- Network and genesis ID
- Source address and destination (or pool application ID)
- Assets, amounts, decimals, and the fiat value used for policy decisions
- Fees and slippage settings
- Simulation result from `algod` simulate where available
- Which spending-limit bucket the action counts against and how much headroom remains
- Whether the action needs authenticator approval and why

The user confirms the preview in the widget. For consent-elevated actions, the identical preview is what appears on the phone. The backend hashes the canonical preview and binds every later step (consent, signing request, submitted transaction) to that hash.

### Evidence Record

Every session produces an append-only evidence record. Each entry is hashed with the previous entry's hash so a transcript cannot be edited after the fact without detection.

Recorded per session:

- Session metadata: tier changes, client application, widget version, timestamps, coarse geolocation if the host site provides it
- Every user message and assistant message, including the model's tool calls and tool results (secrets redacted by schema, not by regex)
- Every action preview, its canonical hash, and the user's confirmation
- Every consent request and consent receipt from the authenticator, including the device key identifier and signature
- Every signing request to Biatec OIDC and its outcome, including spending-limit decisions
- Every submitted transaction ID, confirmation round, and final on-chain status

Evidence is exportable by the user as a signed JSON bundle and is the basis for dispute handling, audits, and the hackathon demonstration.

---

## Architecture

```mermaid
flowchart TB
    subgraph Host["Host website (Biatec Web, Biatec DEX, third-party Vue 3 site)"]
        W["@biatec/chat-widget<br/>floating icon + chat panel"]
    end

    subgraph Backend["Biatec Chat backend (ASP.NET Core 10)"]
        BFF["OAuth 2 BFF<br/>PKCE + auth code, cookie session"]
        ORCH["Conversation orchestrator<br/>tier policy, tool allow-list, action previews"]
        TOOLS["Tool executors<br/>MCP client, CLAMM builder, algo-safe builder"]
        CONSENT["Consent orchestrator<br/>Biatec Consent Protocol"]
        EVID["Evidence log<br/>hash-chained, PostgreSQL"]
        STORE["Token vault<br/>Redis, encrypted, short TTL"]
    end

    subgraph AWS["AWS"]
        BR["Amazon Bedrock<br/>Claude models"]
    end

    subgraph Biatec["Biatec services"]
        OIDC["Biatec OIDC<br/>oidc.biatec.io"]
        MCP["Biatec MCP<br/>mcp.biatec.io/tools"]
        DEX["Biatec DEX contracts<br/>config / identity / pool provider / pools"]
        SAFE["Algo Safe app<br/>+ x402 facilitator"]
    end

    subgraph Mobile["User's phone"]
        AUTH["Biatec Authenticator<br/>Flutter, device-bound key"]
    end

    CHAIN["Algorand network<br/>algod, indexer, simulate"]

    W -- "HTTPS + SameSite cookie, SSE stream" --> BFF
    BFF --> ORCH
    ORCH -- "messages + allowed tools" --> BR
    BR -- "tool calls" --> ORCH
    ORCH --> TOOLS
    ORCH --> EVID
    ORCH --> CONSENT
    BFF -- "authorization code + PKCE, refresh" --> OIDC
    BFF --> STORE
    TOOLS -- "bearer token from vault" --> MCP
    TOOLS -- "unsigned groups to sign" --> OIDC
    TOOLS -- "build add-liquidity / swap groups" --> DEX
    TOOLS -- "proposals, approvals, execution" --> SAFE
    MCP --> OIDC
    MCP --> CHAIN
    OIDC --> CHAIN
    CONSENT -- "CIBA-style push, RAR payload" --> AUTH
    AUTH -- "signed consent receipt" --> CONSENT
    CONSENT -- "consent receipt attached to sign request" --> OIDC
    DEX --> CHAIN
    SAFE --> CHAIN
```

**Boundary rules**

- The widget never receives an access token, refresh token, ID token, or authorization code. It only ever holds an opaque, HTTP-only, `Secure`, `SameSite=Strict` session cookie issued by the backend.
- The backend is the only OAuth client. It is registered with Biatec OIDC as a confidential client with PKCE enforced in addition to its client secret.
- Model calls go to Amazon Bedrock from the backend only. The widget never talks to Bedrock, and no Bedrock credential leaves the backend's AWS role.
- Transaction building for Biatec DEX and Algo Safe uses the official libraries (`biatec-concentrated-liquidity-amm`, `algo-safe`). The backend never hand-assembles transaction groups for those products.
- Signing always happens inside Biatec OIDC. The backend sends unsigned transaction groups and receives signed bytes; it never sees key material.

---

## Chat Widget (`@biatec/chat-widget`)

### Packaging

- Published to npm as `@biatec/chat-widget`, ESM and UMD builds, Vue 3 as a peer dependency.
- Installed with one plugin call in the host application:

```ts
import { createApp } from 'vue'
import { BiatecChat } from '@biatec/chat-widget'
import '@biatec/chat-widget/style.css'

createApp(App)
  .use(BiatecChat, {
    backendUrl: 'https://chat.biatec.io',
    clientKey: 'biatec-web',          // identifies the host, not a secret
    locale: 'en',
    theme: 'auto',
    suggestions: 'default',
  })
  .mount('#app')
```

- Styles are scoped under a `biatec-chat` root with CSS custom properties, so the widget does not fight the host's PrimeVue theme in Biatec Web or any other design system.
- No host-page data is read by default. A host can opt in to pass a page context string (for example the pool currently shown on Biatec DEX) that the assistant may use to pre-fill suggestions.

### User Experience

1. **Launcher**: a round chat icon in the bottom-right corner. Keyboard accessible, respects `prefers-reduced-motion`, and can be positioned by the host.
2. **Panel**: opens as a drawer on desktop and full-screen on mobile. Header shows the Biatec logo, the current tier ("Guest" or the signed-in email with the active primary address), and a sign-in or sign-out control.
3. **Sample prompts** shown on an empty session, chosen by tier and host context:
   - "Explain to me what the blockchain is and why Biatec uses Algorand."
   - "Transfer funds from my primary Bitcoin address to my secondary Bitcoin address."
   - "Add liquidity to the Biatec DEX EURS/USDC pool."
   - "Swap 50 USDC to ALGO."
   - "Create a Biatec Safe for my team with 2-of-3 approvals."
   - "What are my current spending limits?"
4. **Streaming replies** over Server-Sent Events, with tool progress shown as compact status lines ("Checking your USDC balance", "Simulating transaction group").
5. **Action preview card** with Confirm and Cancel. A consent-elevated action shows a "Waiting for approval on your phone" state with a countdown, the same short binding code that the authenticator displays, and a Cancel button.
6. **Sign-in handoff**: clicking Sign in opens Biatec OIDC in a popup or redirect. The widget never sees the OAuth parameters; it opens a backend URL that starts the flow, and polls the backend's session endpoint until the tier changes.
7. **Receipts**: after an action completes, the card shows the transaction ID with an explorer link, and a "Download evidence" link for the session.

### Accessibility And Localization

- WCAG 2.1 AA: focus trapping in the panel, ARIA live region for streamed messages, visible focus styles, contrast-checked theme tokens.
- Localization through the same `vue-i18n` message format Biatec Web already uses, with English and Slovak at launch.

---

## Chat Backend (`Biatec.Chat.Api`)

### Responsibilities

- Terminate the browser session and act as the OAuth 2 Backend-for-Frontend (BFF).
- Run the conversation loop against Amazon Bedrock with a per-tier tool allow-list.
- Execute tools against Biatec MCP, Biatec OIDC, Biatec DEX libraries, and the `algo-safe` library.
- Compute action previews, simulate transaction groups, and apply consent policy.
- Drive the Biatec Consent Protocol towards the authenticator.
- Record sessions and evidence.
- Expose health, metrics, and an admin API for policy and knowledge-base management.

### Model Layer

- Model access is through Amazon Bedrock, so no Anthropic API key is stored anywhere. The backend's AWS role is scoped to `bedrock:InvokeModel` and `bedrock:InvokeModelWithResponseStream` on the allowed model identifiers.
- The default model is the current Claude Opus generation available on Bedrock, with the Claude Sonnet generation configured as the cost-saving option for anonymous capability-discovery sessions. Model identifiers are configuration, not code.
- Every request carries a system prompt built from three stable parts, in this order, so Bedrock prompt caching stays effective: the Biatec persona and refusal rules, the tier's capability catalogue, and the curated knowledge-base excerpts. Volatile data (balances, quotes, timestamps) goes into tool results, never into the system prompt.
- The backend owns the tool loop. The model proposes a tool call; the orchestrator validates the call against the tier allow-list and the JSON schema, executes it, and returns the result. A tool call that fails validation returns an error result to the model and is logged as a policy event.
- Prompt injection defense: content that comes from outside the user (knowledge-base pages, token names, on-chain notes, host page context) is wrapped as untrusted data in tool results. The system prompt instructs the model that such content never changes the tier or the action. The backend enforces the same rule regardless of what the model does.

### Tier Enforcement

Enforcement is layered so a model mistake cannot become a security incident:

1. **Tool visibility**: the anonymous tier's request to Bedrock contains only the `explain.*` tools and the knowledge-base search tool.
2. **Server-side gate**: every tool call is checked against the session tier, the token's scopes, and the capability registry before execution.
3. **Refusal content**: when a prompt in the anonymous tier asks for an action, the orchestrator short-circuits with a templated refusal plus the sign-in control, and logs the attempt. The model is still asked to phrase the explanation, but it cannot change the outcome.
4. **Scope check at execution**: before calling Biatec MCP or Biatec OIDC, the backend inspects the stored access token's `scope` claim. A capability whose scope is missing is refused with a message that offers to re-authorize with the needed scope.

### Tool Executors

- **MCP client**: a Model Context Protocol client that connects to `https://mcp.biatec.io/tools` with the user's bearer token. Tools are discovered at startup and mapped onto chat capabilities. The chain is always build, then preview, then sign, then submit; the backend never calls `signTransaction` without a confirmed preview.
- **DEX executor**: uses `biatec-concentrated-liquidity-amm` to resolve `configAppId`, `identityAppId`, and `poolProviderAppId` for the active network, finds the pool for the requested asset pair, builds the add-liquidity, remove-liquidity, or swap group, simulates it, and hands the unsigned group to Biatec OIDC for signing. The user's identity verification class on the Biatec Identity Provider contract is read and surfaced in the preview, because pools can require a minimum verification class.
- **Safe executor**: uses the `algo-safe` library to create a safe, add signer groups, create proposals with the exact typed payload, submit `approveProposal` app calls signed by the user's address, and call `executeProposal` once the threshold is reached. Proposal payload versions are shown in the preview so an approver always knows what they approve.
- **Limits executor**: reads and updates spending limits through Biatec OIDC. Updates are always consent-elevated.

### Session Recording And Evidence

- Transcripts and evidence entries live in PostgreSQL. Each entry stores `prevHash`, `entryHash` (SHA-256 of the canonical JSON), and a server signature with a rotating key published at a JWKS endpoint, so a user or auditor can verify a bundle offline.
- Personal data is minimized: email and addresses are stored as the OIDC `sub` plus the address strings needed for evidence. The model's tool results are stored after schema-driven redaction of anything marked `sensitive` in the tool's output schema.
- Retention is configurable per deployment. Default: full transcripts 90 days, evidence entries 7 years for fund-moving actions, anonymous sessions 30 days.
- Users can view and export their own sessions from the widget. Deletion requests remove transcripts but keep the hash chain and transaction references, because those are needed for dispute resolution.

---

## Security Model: Authorization Without Tokens In The Browser

### Flow

```mermaid
sequenceDiagram
    participant B as Browser (widget)
    participant API as Chat backend (BFF)
    participant O as Biatec OIDC
    participant M as Biatec MCP

    B->>API: GET /auth/login (session cookie)
    API->>API: generate state, nonce, PKCE verifier; store in server session
    API-->>B: 302 to oidc.biatec.io/authorize?response_type=code&code_challenge=...&scope=openid profile email&resource=https://mcp.biatec.io/tools
    B->>O: user signs in with Google / Microsoft, reviews consent screen
    O-->>B: 302 to chat.biatec.io/auth/callback?code=...&state=...
    B->>API: GET /auth/callback?code&state (cookie)
    API->>API: verify state, bind to cookie session
    API->>O: POST /token (code, code_verifier, client_secret, resource)
    O-->>API: access_token (15 min), refresh_token (30 days), id_token
    API->>API: validate id_token nonce; encrypt tokens into vault keyed by session id
    API-->>B: 302 back to host page; cookie unchanged, tier now Authenticated
    B->>API: POST /chat/messages
    API->>M: tool call with Authorization: Bearer (from vault)
    M->>O: forwards bearer for signing
```

### Rules

- **Authorization Code + PKCE** per RFC 7636 on every login, even though the backend is a confidential client. `state` is bound to the cookie session and single-use; `nonce` is validated on the ID token.
- **Resource indicator** (RFC 8707) `resource=https://mcp.biatec.io/tools` is sent so the access token's audience matches what Biatec MCP validates locally against the Biatec OIDC JWKS.
- **No tokens in the browser.** The widget receives only an opaque session cookie: `HttpOnly`, `Secure`, `SameSite=Strict`, `__Host-` prefix, with a short idle timeout (15 minutes of inactivity) and an absolute lifetime (8 hours). Cross-site hosts that embed the widget use a first-party backend domain via a small iframe bridge, never third-party cookies.
- **Token vault.** Access and refresh tokens are encrypted at rest with a key from the deployment's KMS (AWS KMS in the reference deployment), stored in Redis keyed by session ID, and expire with the session. Refresh tokens are rotated on every use and revoked at logout through the OIDC end-session endpoint. The vault is never written to logs or evidence.
- **Minimal lifetime.** Access tokens live 15 minutes (Biatec OIDC default) and are refreshed only on demand for a tool call, never proactively. If a session is idle past its timeout, the backend revokes the refresh token rather than keeping it warm.
- **Scope minimization.** Login requests `openid profile email`. The `sign` scope is requested only when the user first confirms an action preview that needs it, through an incremental re-authorization round-trip. `manage-limits` is requested only for the limits capability. `rekey` is never requested by this product.
- **CSRF and origin.** All state-changing endpoints require a custom header plus an origin allow-list derived from the registered host sites. The widget's `clientKey` identifies the host for policy and metrics; it is not a secret and grants nothing.
- **Logout** clears the cookie, deletes the vault entry, revokes the refresh token, and performs RP-initiated logout at Biatec OIDC.
- **Secrets handling.** The OIDC client secret, Redis credentials, database credentials, and signing keys for evidence are injected from Kubernetes secrets or AWS Secrets Manager, never from `appsettings.json`.

### Threat Model Highlights

| Threat | Mitigation |
| --- | --- |
| XSS on a host page steals tokens | There are no tokens to steal; the cookie is HttpOnly and SameSite=Strict, and fund-moving actions above threshold need the phone |
| Malicious host page drives the widget to act | Action previews need an explicit user click inside the widget's own DOM; high-value actions need authenticator approval with number matching |
| Prompt injection via on-chain data or docs | Untrusted content wrapped as data; tier and scope gates enforced server-side |
| Backend compromise | Tokens are short-lived, scoped, and per session; keys never leave Biatec OIDC; spending limits are enforced by Biatec OIDC, not by the chat backend; consent receipts are verified by Biatec OIDC, not only by the chat backend |
| Replay of a consent | Receipts are bound to the preview hash, a nonce, the client ID, and a short expiry |
| Transcript tampering | Hash-chained, signed evidence entries |

---

## Biatec Authenticator And The Biatec Consent Protocol

### Goal

A mobile app, comparable in role to Microsoft Authenticator, that shows the user a clear description of a pending action and lets them approve or deny it with a device-bound key. The default policy is that any single action valued above 1000 EUR (converted at the same rates Biatec OIDC uses for spending limits) requires authenticator approval. Users can lower that threshold, enable approval for everything, or set per-client thresholds, but cannot raise it above a deployment-defined ceiling without re-enrolling the device.

The protocol must let a bank or any other third party integrate without asking Biatec for permission, while making it impossible for an impostor to present itself as that bank to the user.

### Building On Existing Standards

Rather than invent a new protocol, the Biatec Consent Protocol is a profile over standards that already solve the individual pieces:

| Need | Standard | How it is used |
| --- | --- | --- |
| Push an approval to a device that did not start the flow | OpenID Connect Client-Initiated Backchannel Authentication (CIBA), push and poll modes | The chat backend (or a third-party client) calls Biatec OIDC's backchannel authentication endpoint; Biatec OIDC notifies the authenticator |
| Describe exactly what is being approved | OAuth 2.0 Rich Authorization Requests (RFC 9396) `authorization_details` | A typed `biatec_transaction` object with network, sender, receiver, asset, amount, fiat value, and the preview hash |
| Make the human-readable message part of the signed contract | CIBA `binding_message` and PSD2 Strong Customer Authentication dynamic linking | The amount and payee are inside the signed receipt, so a changed amount invalidates the consent |
| Financial-grade hardening | FAPI 2.0 Security Profile and FAPI-CIBA | Sender-constrained tokens, signed requests, `request_expiry` defaults of 300 seconds |
| Step up only when needed | OAuth 2.0 Step-up Authentication Challenge (RFC 9470) | Biatec OIDC answers a signing request above threshold with `insufficient_user_authentication` and the required `acr`, which the chat backend fulfils through CIBA |
| Device-bound, phishing-resistant user keys | FIDO2 / WebAuthn passkeys and platform keystores (Secure Enclave, Android StrongBox) | The authenticator's receipt signing key is generated in hardware and attested at enrollment |
| Prove the app is genuine | OAuth 2.0 Attestation-Based Client Authentication (IETF draft) with Apple App Attest and Google Play Integrity | Each authenticator instance presents an attestation when enrolling and when posting receipts |
| Let third parties register without permission, yet be verifiable | RFC 7591 Dynamic Client Registration plus OpenID Federation 1.0 trust chains | Any client can self-register; display name and logo are shown to the user only if the client's entity statement chains to a trust anchor the user's deployment recognizes |
| Record AI intent in a verifiable way | Agent Payments Protocol (AP2) mandates (W3C Verifiable Credentials, SD-JWT with key binding) | The chat backend issues the equivalent of an intent mandate for the action, and the user's approval produces the equivalent of a cart mandate, both attached to the evidence record |
| Standard consent record | Kantara Consent Receipt structure | The receipt JSON follows the consent receipt field layout so it can be exported and verified by third parties |

### Flow

```mermaid
sequenceDiagram
    participant U as User
    participant W as Widget
    participant API as Chat backend
    participant O as Biatec OIDC
    participant A as Authenticator app

    U->>W: "Add 5000 EURS and 5000 USDC to the EURS/USDC pool"
    W->>API: message
    API->>API: build preview, simulate, value = 10,000 EUR > threshold
    API-->>W: preview card, state = needs phone approval
    U->>W: Confirm
    API->>O: POST /bc-authorize (authorization_details = biatec_transaction, binding_message, login_hint = sub, resource)
    O->>O: verify client, verify preview hash matches authorization_details
    O-->>A: push notification (no amounts in the push payload)
    A->>O: fetch pending request over mTLS with device key
    A->>U: show canonical card: client name (verified), amount, assets, destination, binding code
    W-->>U: show same binding code
    U->>A: approve with biometric; device key signs receipt
    A->>O: POST receipt (signed, attested)
    O-->>API: token with acr = biatec:consent and authorization_details echoed
    API->>O: POST /wallet/{network}/{address}/sign (unsigned group + consent token)
    O->>O: verify group hash == consented preview hash, check spending limits
    O-->>API: signed transactions
    API->>API: submit, record evidence with receipt
    API-->>W: receipt card with transaction ID
```

### Protocol Rules

1. **What you see is what you sign.** The authenticator renders the card only from the `authorization_details` object it fetches from Biatec OIDC over an authenticated channel, never from the push notification body or from anything the client sends directly. The receipt signs the canonical form of that object.
2. **Number matching.** Biatec OIDC generates a short binding code shown both in the requesting surface (widget, bank app, website) and in the authenticator. The user must see the same code in both places. The code is part of the signed receipt.
3. **Verified identity of the requester.** The authenticator shows a client's display name, logo, and domain only when the client's OpenID Federation entity statement chains to a trust anchor. Unverified clients are shown as "Unverified application" with their registered `client_id` and redirect domain in a warning style, and the user's policy may block them entirely.
4. **Cooling-off for new clients.** A newly registered client cannot request approvals above a low amount for its first 24 hours and until the user has approved at least one small action from it.
5. **Short validity.** A consent is valid for 300 seconds by default and for exactly one signing request. Biatec OIDC rejects a signing request whose group hash differs from the consented preview hash.
6. **No secrets on the phone beyond the device key.** The authenticator holds a hardware-backed key pair and an enrollment identifier. It holds no wallet keys, no cloud-storage tokens, and no long-lived OAuth tokens. Losing the phone cannot drain the account.
7. **Enrollment is itself consented.** Enrolling a new authenticator device requires approval from an existing enrolled device, or, for the first device, a fresh full Biatec OIDC login with the identity provider's own multi-factor step.
8. **Receipts are verifiable offline.** A receipt contains the signed `authorization_details`, the binding code, the client's entity identifier, the device key ID, the timestamp, and the user's `sub`. Anyone with the Biatec OIDC JWKS and the enrolled device's public key can verify it.
9. **Deny is as loud as approve.** A denial produces a signed denial receipt and immediately cancels the pending action in the requesting surface. Three denials from a client in a day block that client for the user.
10. **Rate and amount limits are enforced by Biatec OIDC.** The authenticator approval never bypasses spending limits; it is an additional requirement on top of them.

### Authenticator App (Flutter)

- Screens: enrollment, pending approvals list, approval card, approval history, device management, trusted clients and per-client thresholds, security settings (biometric requirement, threshold, blocked clients).
- Push through Firebase Cloud Messaging and Apple Push Notification service, with the notification carrying only a request identifier. All content is fetched from Biatec OIDC.
- Device key in Secure Enclave or Android StrongBox through platform channels; attestation collected at enrollment and refreshed periodically.
- Offline mode: shows the last known state and refuses to approve without a live fetch, so a stale card can never be signed.
- Accessibility: large-type card, screen reader labels for amounts and names, no color-only warnings.

### Permissionless Third-Party Integration

A bank, a fintech, or any other service can integrate in three steps without contacting Biatec:

1. Publish an OpenID Federation entity configuration at its domain and obtain a subordinate statement from any trust anchor the deployment recognizes (initially Biatec's own anchor and, as they become available, national or EU-level financial trust lists).
2. Self-register a client with Biatec OIDC through RFC 7591 Dynamic Client Registration, declaring `backchannel_token_delivery_mode` and the `authorization_details_types` it will use.
3. Call the backchannel authentication endpoint with `authorization_details` and a `binding_message`, display the returned binding code to the user, and poll or receive the token.

The `packages/consent-protocol` conformance suite exercises these steps against a mock Biatec OIDC so integrators can validate before going live.

---

## Biatec Safe In Chat And The Hackathon Demonstration

### Safe Capabilities

Chat can create a Biatec Safe (an Algo Safe deployment) and manage it through the `algo-safe` library:

- Create a safe with a name, network, and an initial signer group (for example 2-of-3 with three addresses collected in chat).
- Add or change signer groups through governed proposals, so admin changes follow the same approval path as payments.
- Propose a transaction: payment, asset transfer, or a DEX add-liquidity application call group, stored as the exact ordered typed payload.
- Approve a proposal from a different user's session; the approval is a signed `approveProposal` app call from that user's address, carrying the `expectedPayloadVersion` so a changed payload invalidates the approval.
- Execute once the threshold is met; the safe emits the payload as one atomic inner transaction group.
- Show the proposal queue, approval progress, membership epoch, and expiry rounds in a card.

### Demonstration Script

The fintech hackathon demo uses two browsers, two Biatec OIDC identities, and one phone:

1. **Alice** opens Biatec Web, opens the chat, and asks "Create a Biatec Safe for me and Bob with 2-of-2 approvals." She signs in, confirms the preview, and the safe is deployed. The chat shows the application ID and the safe address.
2. Alice asks "Fund the safe with 2000 EURS and 2000 USDC." The value exceeds her threshold. Her phone shows the Biatec Authenticator card with the amounts, the safe address, and the binding code matching the widget. She approves. The chat shows the transaction IDs.
3. Alice asks "Propose adding all the safe's EURS and USDC as liquidity to the Biatec DEX EURS/USDC pool." The chat builds the application call group with `biatec-concentrated-liquidity-amm`, stores it as a proposal, and shows the payload preview and version.
4. **Bob** opens Biatec DEX in another browser, opens the chat, and asks "Show me pending proposals in my safes." He sees Alice's proposal with the exact payload, confirms, and signs the approval app call.
5. Either user asks "Execute the proposal." The safe executes the inner group, and the chat shows the pool position and the LP tokens held by the safe.
6. The presenter opens the evidence export for both sessions and shows the hash chain, the consent receipt, and the on-chain transaction IDs lining up.

### x402 And The Agent-To-Agent Economy

Algo Safe already ships an x402 facilitator (`algo-safe-x402-facilitator`, a Hono server exposing `/verify`, `/settle`, and `/supported` for the `exact` scheme on Algorand), an x402 client, and a demo shop. Biatec Chat uses them in two ways:

- **Chat as a buyer.** A capability `x402.pay` lets the assistant pay for an x402-protected resource on the user's behalf, within the user's spending limits and, above threshold, with authenticator approval. The payment is a safe-governed or wallet-signed USDC transfer built by the `algo-safe` library and settled through the facilitator.
- **Chat backend as a seller.** Premium capabilities (for example deep portfolio analysis) can be exposed as x402-protected endpoints so another agent can pay the Biatec Chat backend for them, with the same facilitator verifying and settling.

This is the bridge from "a human chats with an agent" to "agents pay agents under human-set policy," and it is what the Algo Safe spending-limit and signer-group model was designed for.

---

## Data Model (Backend)

| Entity | Key fields | Notes |
| --- | --- | --- |
| `ChatSession` | id, tier, clientKey, oidcSub (nullable), createdAt, lastActivityAt, absoluteExpiresAt | Cookie maps to this id |
| `Message` | sessionId, seq, role, content, toolCalls, toolResults, modelId, usage | Content stored after redaction |
| `ActionPreview` | id, sessionId, capability, canonicalJson, hash, fiatValueEur, needsConsent, status | Status: proposed, confirmed, consented, signed, submitted, confirmed-on-chain, failed, cancelled |
| `ConsentRequest` | id, previewId, authReqId, bindingCode, expiresAt, status, receipt | Receipt stored verbatim |
| `EvidenceEntry` | sessionId, seq, kind, payload, prevHash, entryHash, signature, signedAt | Append-only |
| `TokenVaultEntry` | sessionId, encryptedAccessToken, accessExpiresAt, encryptedRefreshToken, scopes | Redis, TTL bound to session |
| `Policy` | scope (global, client, user), consentThresholdEur, maxThresholdEur, blockedClients | User-level values come from Biatec OIDC where they exist |

---

## Repository Layout

```
biatec-chat/
├── packages/
│   ├── chat-widget/              @biatec/chat-widget (Vue 3 library)
│   ├── chat-protocol/            shared TypeScript types generated from backend OpenAPI
│   └── consent-protocol/         Biatec Consent Protocol spec, schemas, conformance tests
├── backend/
│   ├── Biatec.Chat.Api/          ASP.NET Core 10 host (BFF, orchestrator, tools, evidence)
│   ├── Biatec.Chat.Core/         domain model, policies, canonicalization, hashing
│   ├── Biatec.Chat.Tools/        MCP client, DEX executor, Safe executor
│   └── Biatec.Chat.Tests/        unit and integration tests with mocked OIDC, MCP, Bedrock
├── apps/
│   └── biatec_authenticator/     Flutter app
├── docs/
│   ├── architecture.md
│   ├── threat-model.md
│   ├── integration-widget.md
│   ├── integration-third-party-consent.md
│   └── hackathon-demo.md
├── k8s/                          deployment manifests (main, stage) following Biatec conventions
└── PRODUCT-DESCRIPTION.md
```

---

## Non-Functional Requirements

- **Latency**: first streamed token within 2 seconds for anonymous answers; action preview within 5 seconds including simulation.
- **Availability**: backend stateless except Redis and PostgreSQL; horizontally scalable; graceful degradation to anonymous tier if Biatec OIDC is unreachable, with a clear message.
- **Observability**: OpenTelemetry traces across widget request, Bedrock call, MCP call, and OIDC call; metrics for refusals per tier, consent approvals and denials, and policy violations.
- **Testing**: contract tests for the widget-backend protocol; policy tests that assert the anonymous tier can never reach a wallet tool; replay tests for the evidence hash chain; conformance tests for the consent protocol; end-to-end test of the hackathon script on Algorand testnet.
- **Compliance posture**: data minimization, user export and deletion, evidence retention aligned with financial record-keeping, and a published security contact.

---

## Research Anchors

- Biatec OIDC and Biatec MCP: [AlgorandGoogleDriveAccount](https://github.com/ludovit-scholtz/AlgorandGoogleDriveAccount). Authorization code with PKCE, RFC 7591 dynamic registration, RFC 8707 resource indicators, `sign` / `manage-limits` / `rekey` scopes, 15-minute access tokens, 30-day refresh tokens, daily/weekly/monthly spending limits in a user-chosen currency, and a three-step build, sign, submit MCP tool chain.
- Biatec DEX contracts and library: [BiatecCLAMM](https://github.com/scholtz/BiatecCLAMM). `biatec-concentrated-liquidity-amm` npm package, config, identity, and pool provider applications, verification classes on the identity provider.
- Algo Safe: [algo-safe](https://github.com/ludovit-scholtz/algo-safe). Signer groups, typed proposals, membership epochs, `approveProposal` with payload versions, atomic inner-group execution, x402 facilitator, client, and shop.
- Biatec Web: [BiatecWeb](https://github.com/scholtz/BiatecWeb). Vue 3, Vite, PrimeVue, Pinia, vue-router, vue-i18n, Kubernetes deployment.
- Standards: OpenID Connect CIBA Core; OAuth 2.0 Rich Authorization Requests (RFC 9396); OAuth 2.0 Step-up Authentication Challenge (RFC 9470); PKCE (RFC 7636); Resource Indicators (RFC 8707); Dynamic Client Registration (RFC 7591); FAPI 2.0 Security Profile and FAPI-CIBA profile; OpenID Federation 1.0; OAuth 2.0 Attestation-Based Client Authentication (IETF draft); FIDO2 / WebAuthn; Agent Payments Protocol (AP2) mandates; Kantara Consent Receipt; x402 payment protocol.

---

## Success Criteria

Biatec Chat succeeds when:

1. A visitor on Biatec Web can ask what Biatec is and get a correct answer without signing in, and is politely refused when asking to move funds.
2. A user can sign in through Biatec OIDC without any token ever reaching the browser, verified by inspecting network traffic and storage.
3. A user can transfer between their own Bitcoin addresses, swap on Biatec DEX, and add liquidity to the EURS/USDC pool from chat, each with a preview that matches what lands on-chain.
4. An action above the threshold cannot complete without a matching approval in Biatec Authenticator, and a modified transaction is rejected by Biatec OIDC even with a valid earlier approval.
5. Two users can create a Biatec Safe, propose, approve, and execute an add-liquidity transaction entirely from chat.
6. Every session exports a signed evidence bundle whose hash chain and transaction IDs verify independently.
7. A third party can pass the consent-protocol conformance suite and request approvals from Biatec users without any manual onboarding by Biatec, while appearing as "Unverified" until it chains to a recognized trust anchor.
