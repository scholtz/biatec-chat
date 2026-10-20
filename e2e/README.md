# e2e: the hackathon demo script as an automated test (T-059)

Runs steps 1 to 8 of the demo script in [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) ("Demonstration Script"), as
adapted in [docs/hackathon-demo.md](../docs/hackathon-demo.md), in the **"always ask" fallback mode** with two identities.

```
pnpm --filter e2e test         # the run; needs only Node 22.18+ (no Docker, no network, no AWS)
pnpm --filter e2e typecheck
```

Every run writes to `e2e/out/` (git-ignored): `demo-run.log.md` (the run log), `run.json` (per step data), `evidence-alice.json`,
`evidence-bob.json` and `evidence-jwks.json` (so `node packages/evidence-verifier/cli.mjs verify ... --jwks ...` (after `pnpm --filter evidence-verifier build`) can re-check them by hand). A reviewed
copy of one run is kept in [runs/mock-run.log.md](runs/mock-run.log.md).

## What the run does

| Demo step | Signing calls (all ask the phone in the fallback) |
| --- | --- |
| 1 Alice creates the 2-of-2 safe | `safe.create` (her first action) |
| 2 Fund ALGO, opt-in proposal | ALGO payment, `safe.propose`, `safe.approve` (2 extra prompts) |
| 3 Bob approves and executes | `safe.approve` (his first action), `safe.execute` (extra) |
| 4 Fund the safe with 2000 EURS + 2000 USDC | `wallet.transfer`; wrong binding code first, then a bait-and-switch after approval |
| 5 Top up, propose and approve the liquidity | ALGO top-up, `safe.propose`, `safe.approve` (about 4000 EUR, pool application on the card) |
| 6 Bob approves (2 of 2) | `safe.approve` |
| 7 Execute | `safe.execute` |
| 8 Evidence | export both sessions' bundles, verify offline, line up with the run |

For each signing call the harness does what the backend and the phone do: POST the unsigned group to Biatec OIDC
`/wallet/{network}/{address}/sign`, take the 401 step-up challenge (`auth_req_id`, binding code for the widget), let a scripted
authenticator open the request, check the card, type the code and sign a receipt with the device key, redeem the CIBA token,
and sign again with the single-use consent token. Alice is the mock's `carol` (active device, always ask) and Bob is the mock's
`alice` (active device), both set to "always ask" for the Biatec Chat client with first use pending.

Asserted: all 12 calls end signed; Alice is asked 9 times and Bob 3 times; the phone card carries the same group hash as the
preview; the card never contains the binding code; a wrong code is refused; a different group is refused with
`group_hash_mismatch` after approval and does not spend the consent; the consent token cannot be replayed; each session's
evidence bundle verifies against the published JWKS with the offline verifier (`packages/evidence-verifier`); every consent
receipt verifies against the mock's Biatec OIDC JWKS, and its approved hash, the signed group hash and the transaction ids
agree with the run; a tampered payload, a dropped entry and a foreign JWKS are all detected.

## Known gaps (this is not a testnet run yet)

- **No real Algorand testnet.** Unsigned groups are the mock's JSON transaction descriptors (not msgpack), "signing" is a
  `signed:` prefix, and the transaction ids are derived hashes, not confirmed on chain. Steps 1 to 7 are therefore not proven
  against algod, `algo-safe`, `biatec-concentrated-liquidity-amm` or the EURS/USDC pool.
- **The .NET backend is not in the loop.** Bedrock (the chat model), Biatec MCP, the builders sidecar, PostgreSQL and Redis are
  not started. The chat sentences are logged, not parsed by a model. The backend's own flows are covered by its xunit tests
  (`SafeProposalFlowTests`, `LiquidityTests`, `ConsentOrchestratorTests`, `EvidenceExportTests`); this run does not replace them.
- **The evidence bundles come from a reference recorder** in `src/evidence.ts` (same entry kinds, hashing, salts and signatures
  as the spec and the offline verifier), not from `GET /session/evidence`. On-chain anchors are absent, so anchor inclusion proofs
  are not checked.
- **The widget and browser are not driven** (no Playwright, no popup, no cookie, no countdown). Two browsers are two sessions here.
- **The phone is scripted** (authenticator automation is out of scope). The mock sums the amounts of a group in the first
  asset's unit, so the step 4 card shows `4000 EURS` where the real card lists each transfer.
- **Only the "always ask" mode** is run; testnet valuation mode (where the 7 extra prompts disappear) cannot be exercised because
  neither the mock nor the real Biatec OIDC prices testnet assets.
- Time is real (no clock control), so the 24 hour new-device delay and the 300 s consent expiry are not exercised.

## Running it against the real thing later

1. Follow [docs/hackathon-demo.md](../docs/hackathon-demo.md) "Before the demo": `docker compose up -d`, the backend with the
   real Biatec OIDC issuer and Bedrock access (`hackathon` SSO profile, region `eu-central-1`, Claude 4.5 models), funded
   testnet accounts, two enrolled and activated phones, first-use state reset.
2. Drive the script by hand in two browsers; the manual phone steps are marked in `docs/hackathon-demo.md` and in the table above.
3. For step 8, export both sessions (`GET /session/evidence`), fetch the JWKS the bundle names, and check:
   `node packages/evidence-verifier/cli.mjs verify <bundle.json> --jwks <jwks.json>`; then compare the receipt group hashes and the
   transaction ids with the explorer. `src/demo.ts` `crossCheck` is the same comparison in code.
4. To automate it, replace `MockOidcHarness` (`src/oidc.ts`) by calls to the chat backend's `/chat/messages`, `/chat/previews/{previewId}/confirm` and
   `/session/evidence` with a signed-in cookie per identity; the assertions in `test/demo.test.ts` stay valid.
