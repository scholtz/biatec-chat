# Biatec Chat: presentation and pitch prep

Working notes for the hackathon pitch. [PRODUCT-DESCRIPTION.md](PRODUCT-DESCRIPTION.md) is the specification; this file
only frames it for an audience. Code freeze: Tuesday 20 October 2026, 13:30.

## One-line position

**The AI assistant that can actually move money, and still can't be tricked into moving it wrongly.** Every competing
assistant has to choose between being useful and being safe. Biatec Chat's design removes that choice.

> Biatec Chat is an AI financial assistant that can act on your behalf: every action is previewed, bound to a hash,
> approved on your phone when it matters, and recorded in a tamper-evident trail. Banks and fintechs can plug into the
> same consent network using open standards, without asking us for permission.

## The problem

Biatec already has the hard pieces: a self-custody wallet with login (Biatec OIDC), an AI tool interface (Biatec MCP), a
DEX (BiatecCLAMM) and multisig smart accounts (Algo Safe). What's missing is a front door. Today a user has to know which
product does what and how a transaction is put together. Biatec Chat turns all of that into a conversation: "swap 100
USDC to EURS", "add liquidity to the EURS/USDC pool", "create a safe for me and Bob".

## The business case

### 1. It turns AI into a revenue channel, not a support cost

- **The same widget covers the whole funnel.** An anonymous visitor gets education with no sign-up. Asking for an action
  brings up a sign-in card. After that the user can transfer, swap, add liquidity or run a Safe. Most fintech chatbots
  stop at the FAQ.
- **Every chat action becomes on-chain volume** (DEX swaps, liquidity deposits, Safe treasury moves), so the assistant
  feeds the products that earn fees.
- **Users never see a seed phrase.** They sign in with Google or Microsoft, and keys are derived and encrypted into the
  user's own cloud storage. That removes the biggest onboarding barrier in crypto.

### 2. Security is what makes it sellable to regulated buyers

- **What you see is what you sign.** The preview, the phone approval and the on-chain transaction are tied together by
  one group hash. If one byte changes, the action is refused.
- **The chat backend can't be the weak point.** Spending limits, the phone-approval threshold (default €1,000),
  cooling-off rules and receipt checks are all enforced by Biatec OIDC. A hacked website, browser tab or even the chat
  server can't skip the phone for high-value actions.
- **Approval fatigue is designed out.** The user must type a code shown only in the requesting app into the phone. They
  can't reflexively tap "approve" on a request they didn't start.
- **New-client and first-use cooling-off:** a newly registered app can't spend anything without phone approval for its
  first 24 hours.
- **No tokens in the browser and no keys on the phone.** XSS has nothing to steal, and a lost phone can't drain the
  account.

### 3. It gives users real control over the AI

- **Agent modes:** "Analyze only", "Prepare for approval" (the default), plus **Pause** and **Revoke all access**, all in
  one dialog.
- Lowering permissions takes effect instantly. Raising them needs an explicit "I understand" step. Every change is logged
  as evidence.
- This matches the EU AI Act's human-oversight principle: the model proposes, a human decides, and the server enforces
  it. Tier and scope checks run on the server regardless of what the model says.

### 4. Its evidence trail can't be rewritten, even by the operator

- Every message, preview, consent and transaction ID goes into a hash-chained log, anchored on Algorand every 10
  minutes.
- Phone-signed consent receipts can be checked offline by anyone with the public keys.
- Business value: cheaper dispute handling, audit-ready records and proof of what the customer agreed to.

### 5. It works as a B2B platform, not just a widget

- **Multi-tenant white-label:** a bank points `chat.examplebank.com` at the backend and gets its own limits, approval
  thresholds, feature flags and AI budget caps.
- **Predictable unit economics:** Bedrock spend is capped per tenant and globally. A circuit breaker drops anonymous
  traffic to cached answers when a budget runs out, so a bot flood can't run up the AI bill, and signed-in users keep
  reserved capacity.
- **Distribution is built in:** it's an npm package for any Vue 3 site, and the anonymous tier can run on any page.

### 6. It builds a consent network with network effects

- Third-party banks and fintechs can self-register and request approvals from Biatec users without asking Biatec.
- Brands can't be impersonated: until a client chains to a recognised trust anchor (OpenID Federation), the phone shows
  it as "Unverified".
- A public conformance test suite lets integrators validate before going live.
- Every new integrator makes Biatec Authenticator more valuable to users, and every new user makes the network more
  attractive to integrators. The authenticator becomes a shared trusted approval device for many providers.

### 7. It's built on standards banks already use

CIBA, RFC 9396 (Rich Authorization Requests), RFC 9470 (step-up), FAPI 2.0, PSD2 dynamic linking (the approval is bound
to amount and payee), FIDO2 device keys, OpenID Federation and Kantara consent receipts. Integration is mapping to known
standards, not adopting a proprietary protocol, which shortens bank procurement and security review.

### 8. It covers retail and treasury from one product

- Retail: personal swaps, transfers and liquidity.
- Team, corporate and DAO treasury: an M-of-N Safe set up, proposed, approved and executed in plain conversation by
  people on separate devices.

### 9. It's ready for agent payments

- **As a buyer:** the chat can pay for x402 services on the user's behalf, within their limits. A seller can't dodge
  phone approval with a low advertised price, because the threshold is checked against the decoded transfer amount.
- **As a seller:** the backend can sell premium analysis to other AI agents over x402, a revenue stream from machine
  customers.
- Positioning: "agents paying agents under human-set policy."

### 10. Revenue lines

1. B2B SaaS / white-label licence per tenant.
2. Transaction volume and fees on Biatec DEX and Safe.
3. x402 pay-per-call premium AI analysis sold to other agents.
4. Later: fees for access to the consent network or for verified-integrator status.

## How it compares

| | Bank chatbots | Crypto AI assistants / agent wallets | **Biatec Chat** |
|---|---|---|---|
| Executes transactions | Limited or none | Yes, often with the agent holding keys | **Yes, keys stay with the user** |
| Compromised AI/backend can overspend | n/a | Often yes | **No: limits and approvals enforced outside the chat** |
| Out-of-band phone approval bound to the exact transaction | SCA, but not tied to the AI's proposal | Rare | **Yes, hash-bound with code matching** |
| Audit trail the operator can't rewrite | No | No | **Yes, anchored on-chain** |
| Open to third-party providers | No | No | **Yes, permissionless via federation** |

## Pitch cautions

- **Don't claim "the first".** Say "the AI financial assistant that can act and still can't be tricked." Judges might
  know a counterexample.
- **Separate what's demoed from what's on the roadmap:**
  - x402 payments are feature-flagged until after the hackathon.
  - Permissionless registration and several consent features depend on Biatec OIDC changes that aren't shipped yet
    (see [docs/required-changes-biatec-oidc.md](docs/required-changes-biatec-oidc.md)). The demo uses a mock or the
    documented fallback.
  - Rule-based automation is explicitly "not available yet".
  - Presenting these honestly as the roadmap is more credible than implying they're live.

## Strongest demo moments

1. Alice's phone shows the same amount, address and binding code as the chat (demonstration script, step 4).
2. The evidence export's hash chain matches the on-chain transaction IDs.
3. Possible addition: tamper with a single byte and show the signature being refused live. That's the most visual proof
   that it can't be tricked.

## Presentation plan (to fill in together)

Draft structure; adjust timings to the slot the organisers give us.

| # | Section | Goal | Time | Speaker |
|---|---|---|---|---|
| 1 | Hook | One-line position, the "useful vs safe" trade-off | 0:30 | |
| 2 | Problem | Biatec has the pieces, no front door | 0:30 | |
| 3 | Live demo | Anonymous answer → sign-in → swap preview → phone approval with matching code | 2:00 | |
| 4 | Team treasury | 2-of-2 Safe: propose, approve, execute | 1:00 | |
| 5 | Proof | Evidence export lines up with on-chain IDs; tampered byte refused | 0:45 | |
| 6 | Business | Platform, consent network, revenue lines | 0:45 | |
| 7 | Close | Roadmap (x402, third-party integrators), ask | 0:30 | |

### Prep checklist

- [ ] Confirm slot length and format (live demo allowed? screen + phone mirroring?)
- [ ] Assign speakers per section
- [ ] Testnet accounts for Alice and Bob funded; enrolled authenticator devices ready
- [ ] Recorded fallback video of the demo in case of network or testnet failure
- [ ] Slides: hook, problem, comparison table, revenue lines, roadmap
- [ ] Dry run end to end, timed, at least twice

### Likely judge questions

| Question | Short answer |
|---|---|
| What if the LLM hallucinates or gets prompt-injected? | The model can only propose. Tier and scope gates run server-side, every action needs a preview + Confirm, and limits and phone approval are enforced by Biatec OIDC, outside the chat. |
| What if your backend is hacked? | Keys never leave Biatec OIDC. Above-threshold actions still need the phone, and a modified transaction fails the hash check. Residual risk is below-threshold spend within the user's own limits. |
| Who holds the keys? | The user: derived and encrypted into their own cloud storage. We hold no keys, the browser holds no tokens. |
| How do you make money? | Per-tenant licence, DEX/Safe volume, x402 premium analysis, later consent-network fees. |
| Why would a bank use this instead of its own app? | Standards it already knows (FAPI, CIBA, PSD2 dynamic linking), no proprietary protocol, a conformance suite, and a shared trusted device its users may already have. |
| How do you control AI costs? | Per-tenant and global Bedrock budgets with a circuit breaker; cheaper models for anonymous traffic, prompt caching. |
| What's real today vs roadmap? | Be precise: see Pitch cautions above. |
