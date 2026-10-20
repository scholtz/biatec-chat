# Hackathon demo: two people, one safe, phone approvals

The script from [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) ("Demonstration Script") adapted to what this repository
runs. The spec wins on any conflict. Code freeze is **Tuesday 20 October 2026, 13:30**.

## Which mode this script uses

Biatec OIDC today cannot price testnet assets, has no consent (CIBA) endpoints and does not issue device-bound approvals (see
[required-changes-biatec-oidc.md](required-changes-biatec-oidc.md), rows 1 to 4 and 7). So the script is written for the
**fallback mode: "always ask"**:

- Both demo identities are configured with "always ask" for the Biatec Chat client and have **no spending limits** (a configured
  limit would make Biatec OIDC refuse to sign on testnet). Every signing call therefore asks the phone, including the steps that
  would be below threshold under testnet valuation. Those extra prompts are marked **(extra)** below.
- Until the real consent endpoints ship, the phone side runs against the **mock Biatec OIDC** (`packages/consent-protocol/mock-oidc`)
  and the authenticator, not the production service. Say so out loud during the demo.
- Safe and DEX pool calls are unknown application calls to Biatec OIDC, which the fallback treats as above threshold, so the phone
  card shows the decoded transfers and the application id.

## Honest status: what the repo can demonstrate today

The full script needs parts that are still on the board. Check the [board](../kanban/BOARD.md) before presenting; do not claim
a step the code cannot do.

| Script area | Needs | Status when this page was written |
| --- | --- | --- |
| Sign-in in a popup, session cookie, no token in the browser | Widget popup handling, `/auth/*` | Backend built and tested against a mock OIDC; widget popup handling planned |
| Chat that streams answers, tool use, anonymous sign-in card | Orchestrator, SSE endpoint, knowledge base | In progress / planned |
| Previews bound to a group hash with countdown | `ActionPreviewService` | Built (library); card UI planned |
| Scope elevation to `sign` | `/auth/elevate` | Built |
| Confirm, sign at Biatec OIDC, submit | Confirm-and-sign task | Planned |
| Safe create, propose, approve, execute, pool liquidity | Sidecar builders and capability executors | Builders in progress, executors planned |
| Phone card, binding code, receipts | Authenticator, consent orchestrator, real or mock OIDC | Mock OIDC built; authenticator scaffold only; backend orchestrator planned |
| Evidence chain, JWKS, anchoring | Evidence log, anchoring | Log, JWKS, verifier and anchoring built; export bundle and conversation wiring planned |

What can be shown live today, without any planned part: the architecture, `GET /health`, the login and elevation flow against the
mock OIDC, the tool gate refusing a wallet call in the anonymous tier (tests), the hash-chained evidence log and its JWKS, the
Merkle anchor on testnet if the evidence account has been funded, and the consent protocol against the mock (receipt with device
signature, binding code, wrong-code attempts, group-hash mismatch refused).

## Before the demo

1. Region `eu-central-1` only; SSO profile `hackathon`; no credentials in any file or slide. Bedrock models: Opus 4.5 for signed-in
   sessions, Sonnet 4.5 for anonymous ones. The 50 requests per minute limit is shared by the whole team, so rehearse off-peak and
   keep one presenter on the model at a time.
2. Start dependencies and the backend:

   ```sh
   docker compose up -d                                   # Redis 7, PostgreSQL 16
   dotnet run --project backend/Biatec.Chat.Api           # Development seeds the Biatec Chat and localhost tenants
   pnpm --filter mock-oidc start                          # consent side, http://127.0.0.1:8089
   pnpm --filter @biatec/chat-widget dev                  # widget playground
   # once T-068 lands, /dex.html shows the widget inside a Biatec DEX-like page
   ```

3. Two browsers, two identities (Alice, Bob), two phones with the authenticator, each enrolled and activated **at least a day
   before** so the new-device 24 hour delay has passed. Reset first-use state for both identities, or use fresh demo identities,
   so the phone prompts match the script.
4. The Biatec Chat tenant client must already be registered at Biatec OIDC (an operator step today, so the new-client cooling-off
   does not apply). The first-use rule still makes each user's first action phone-approved; that is a feature of the demo.
5. Algorand testnet. Fund Alice with testnet ALGO, EURS and USDC. The demo EURS/USDC pool is deployed with verification class 0 so a
   freshly created safe (no identity record) can use it.
6. Optional: set `Evidence__Anchoring__Enabled=true`, supply the evidence account mnemonic from the secret store (never in a file),
   and fund that testnet account, so the anchor transaction exists before step 8. See `backend/Biatec.Chat.Api/Evidence/Anchoring/README.md`.

## Script

Prompt counts assume "always ask". Under the later testnet-valuation mode the **(extra)** prompts disappear.

1. **Alice** opens Biatec Web, opens the chat and says "Create a Biatec Safe for me and Bob with 2-of-2 approvals." She signs in
   (popup, no token reaches the page), pastes Bob's address when asked, reviews the preview, authorizes signing in the popup when the
   card offers it, and confirms. Her phone asks (first action through Biatec Chat). She approves; the safe is deployed. The chat shows
   the application id and the safe address.
2. Alice: "Fund the safe with 2000 EURS and 2000 USDC." The assistant explains the safe needs ALGO for its minimum balance and opt-ins
   first. Step one is an ordinary ALGO payment (about 2 ALGO) with its own preview and signing call; her phone asks **(extra)**. Then
   the assistant proposes the opt-in (zero-amount asset transfers from the safe to itself, built by `algo-safe`); Alice confirms and
   approves as first signer, and her phone asks **(extra)**.
3. **Bob** opens Biatec DEX in another browser, signs in and asks "Show me pending proposals in my safes." He sees the setup proposal,
   confirms, authorizes signing and approves as second signer; his phone asks (his first action). He then says "Execute it"; his phone
   asks again **(extra)**. The safe opts in.
4. Alice: "Now fund the safe." Her phone shows the Biatec Authenticator card with amounts, the safe address and the binding code that
   matches the widget. She types the code, approves, and the chat shows the transaction ids.
5. Alice tops up the safe with ALGO for the proposal box (preview, signing, phone **(extra)**), then asks "Propose adding all the
   safe's EURS and USDC as liquidity to the Biatec DEX EURS/USDC pool." The chat asks which pool she means, showing each pool's fee tier,
   price range and liquidity. It builds the application call group with `biatec-concentrated-liquidity-amm`, with the safe's LP-token
   opt-in as the first transaction of the payload, stores it as a proposal and shows the payload and its version. The proposal is
   created in one signing call (phone **(extra)**), then, once on chain, Alice's approval is a second call that Biatec OIDC values from
   the stored proposal. Her phone shows the amounts and the pool application id; she approves (1 of 2).
6. Bob: "Show me pending proposals." He confirms the exact payload, his phone shows the same facts and his own binding code, and he
   approves (2 of 2, threshold met).
7. Either user: "Execute the proposal." Under "always ask" the executor's phone asks once more **(extra)**. The safe executes the inner
   group atomically; the chat shows the pool position and the LP tokens held by the safe.
8. The presenter opens the evidence export for both sessions and shows that the hash chain, the consent receipt and the on-chain
   transaction ids line up, and verifies the signatures against `/.well-known/evidence-jwks.json`. Export bundles and the conversation
   wiring are on the board; if they have not landed, show the verifier tests and the anchor on chain instead.

## Showcase site on stage

For a walkthrough without the two-phone setup, open the demo site at <https://demo.stage.chat.biatec.io>: the real widget
against the stage backend, one section per capability with one-click prompts, and what is not on stage yet labelled
"Coming next". [demo-site.md](demo-site.md) lists what each section shows, the testnet setup and the post-deploy checks.

## What to say about trust

- The widget and the page it runs in are not trusted with funds. Limits, thresholds and the phone approval live at Biatec OIDC; the
  chat backend can only ask for more consent, never less.
- What the user approves on the phone is decoded from the transaction group by Biatec OIDC, not taken from the chat; the receipt
  signs the group hash, so one changed byte is refused.
- Signing never goes through Biatec MCP; it goes from the backend straight to Biatec OIDC.
- The operator cannot quietly rewrite the transcript: the heads are anchored on Algorand every 10 minutes.

## If something goes wrong

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| `GET /health` returns 503 | Redis or PostgreSQL is not up | `docker compose up -d`; check port 5432 is not held by another stack |
| Sign-in popup loops | The tenant's client is not registered at Biatec OIDC | Operator registers it; see the required-changes doc |
| `ThrottlingException` in the log | The team's shared 50 requests per minute | The client already backs off with jitter; wait, or switch the anonymous model to Sonnet |
| Preview card says it cannot be confirmed | Under 600 seconds left on the group or the session | The backend rebuilds it as a new preview with a new hash; confirm again |
| Phone shows nothing | Push is not wired yet (authenticator push task) | The mock replaces push with polling; open the pending list on the phone |
| Wrong binding code three times | Rule 2: third wrong code denies the request | Start the action again |
