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

Every task on the [board](../kanban/BOARD.md) is merged. The limits are outside this repository: do not claim a step the
code cannot do against the service you are pointing at.

| Script area | Needs | Status on `main` |
| --- | --- | --- |
| Sign-in in a popup, session cookie, no token in the browser | Widget popup handling, `/auth/*` | Built; works on stage against stage Biatec OIDC |
| Chat that streams answers, tool use, anonymous sign-in card | Orchestrator, `POST /chat/messages` (SSE), `POST /public/chat`, knowledge base | Built |
| Previews bound to a group hash with countdown | `ActionPreviewService`, preview cards | Built |
| Scope elevation to `sign` | `/auth/elevate` | Built |
| Confirm, sign at Biatec OIDC, submit | `POST /chat/previews/{previewId}/confirm` | Built |
| Safe create, propose, approve, execute, pool liquidity | Sidecar builders and capability executors | Built |
| Phone card, binding code, receipts | Authenticator, consent orchestrator, real or mock OIDC | Built against the mock OIDC only: real Biatec OIDC has no consent endpoints, and the authenticator has not been run on a device |
| Evidence chain, JWKS, export, anchoring | Evidence log, `GET /session/evidence`, anchoring | Built; the bundle has no anchor inclusion proofs yet, and no testnet anchor run is recorded |

What can be shown live: everything on the [demo site](demo-site.md) marked "Works" (stage backend, stage Biatec OIDC,
testnet, no phone approval), `GET /health`, the tool gate refusing a wallet call in the anonymous tier, the hash-chained
evidence log, its export and JWKS with the offline verifier, the Merkle anchor on testnet if the evidence account has been
funded, and the consent protocol against the mock (receipt with device signature, binding code, wrong-code attempts,
group-hash mismatch refused), which `pnpm --filter e2e test` runs as steps 1 to 8 of the script ([e2e/README.md](../e2e/README.md)).
The two-phone script below needs the consent side; against stage it stops at the first above-threshold approval.

## Before the demo

1. Region `eu-central-1` only; SSO profile `hackathon`; no credentials in any file or slide. Bedrock models: Opus 4.5 for signed-in
   sessions, Sonnet 4.5 for anonymous ones. The 50 requests per minute limit is shared by the whole team, so rehearse off-peak and
   keep one presenter on the model at a time.
2. Start dependencies and the backend:

   ```sh
   docker compose up -d                                   # Redis 7, PostgreSQL 16, builders sidecar (:3100)
   dotnet run --project backend/Biatec.Chat.Api           # Development seeds the Biatec Chat and localhost tenants
   MOCK_TEST_HELPERS=1 pnpm --filter mock-oidc start                          # consent side, http://127.0.0.1:8089
   pnpm --filter @biatec/chat-widget dev                  # widget playground; /dex.html shows it in a Biatec DEX-like page
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
   transaction ids line up, and verifies the signatures against `/.well-known/evidence-jwks.json` with
   `pnpm --filter evidence-verifier build` (once), then `node packages/evidence-verifier/cli.mjs verify <bundle.json> --jwks <jwks.json>`. Each user downloads the bundle from a receipt
   or the Activity panel (`GET /session/evidence`).

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
| Phone shows nothing | Push needs the Firebase `--dart-define` options, and the mock never sends to FCM (it only records `GET /mock/push-outbox`) | Open the pending list on the phone; it polls every 5 seconds |
| Wrong binding code three times | Rule 2: third wrong code denies the request | Start the action again |
