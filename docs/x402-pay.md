# x402.pay (feature flag, off by default)

`x402.pay` lets the assistant pay an x402-protected resource with a USDC transfer. It is behind three gates, all off by
default: the platform flag `X402:Enabled`, the tenant's `x402Enabled`, and the tool gate (authenticated tier, `openid`
scope to build, `sign` to sign).

## Flow

1. The model calls `x402.pay { network, from, resourceUrl }`. `from` must be one of the user's own addresses.
2. The backend checks the URL against the SSRF policy, requests it, and expects `402` with `accepts[]`.
3. It asks the builder sidecar (`POST /v1/build/x402`) for one USDC asset transfer and decodes the built group. The
   preview shows the decoded transfer, never the seller's advertised price. A group that is not exactly one plain
   transfer of the advertised asset, to the advertised payee, by the user, for at most the advertised amount, is refused.
4. The user confirms the preview; signing goes to Biatec OIDC as for every other capability.
5. After signing, the confirm flow does not broadcast through Biatec MCP for this capability: `X402SettlementHandler` calls `X402PayService.PayAsync`, which has the facilitator verify and settle the signed group (this is the broadcast), then fetches the resource with
   `X-PAYMENT`. The confirm response carries the content as `resourceContent` (untrusted seller data). Verify refused: nothing paid, preview failed. Settle unknown: step stays submitted and is reconciled on chain. Settled but no content: step lands with `errorCode: resource_failed`.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `X402__Enabled` | `false` | Platform flag. Nothing x402 is registered when false. |
| `X402__AllowedHosts__0..n` | empty (deny all) | Exact hosts or `*.example.com` |
| `X402__AllowedPorts__0..n` | `443` | |
| `X402__FacilitatorUrl` | unset | Required when enabled, unless the mock is opted into; startup fails otherwise |
| `X402__UseMockFacilitator` | `false` | Explicit opt-in to the in-process mock (no funds move), for demos and tests only |
| `X402__MaxAmountBaseUnits` | `5000000` | Cap per payment (5 USDC) |
| sidecar env `X402_ENABLED=true` | unset | The sidecar only serves the `x402` builder when set |

## SSRF policy

https only, no credentials or fragments, allowed ports only, DNS names only (no IP literals), host on the allowlist,
every resolved address public (re-checked at connect time against DNS rebinding), redirects never followed, no proxy,
response bodies capped at 64 KB. Seller text is untrusted data.

Only USDC is supported (mainnet 31566704, testnet 10458941). Seller side is out of scope.

## Running the testnet demo

1. `docker compose up -d` (Postgres, Redis).
2. Set `X402__Enabled=true`, `X402__AllowedHosts__0=<seller host>`, and either `X402__FacilitatorUrl=<algo-safe-x402-facilitator URL>` or, for a demo that moves no funds, `X402__UseMockFacilitator=true`.
3. Point the seller at a resource that answers `402` with an Algorand `exact` option on `algorand-testnet` (USDC asset 10458941) and returns content when `X-PAYMENT` is present.
4. Ask the assistant to pay for that URL on testnet, review the preview, confirm, and approve in the authenticator.

After settlement, a failure to deliver the resource (oversized body, DNS rebinding) is reported as settled with no content, never as "nothing was paid". The `ConfirmService` x402 settlement tests need Postgres and run in CI.

## Verification status

The acceptance criterion "with the flag on, a demo shop resource is paid on testnet" is **not verified end to end**. It is
covered by unit and integration tests against a mock facilitator and a stub resource server only. No demo seller was built
and no testnet run against `algo-safe-x402-facilitator` has been recorded; that needs an external seller, a facilitator and
a funded testnet wallet. The DB-backed ConfirmService x402 tests need Postgres (`docker compose up -d`).

If the `X402:Enabled` flag is turned off after an `x402.pay` preview was created, confirming it fails closed
(`settlement_unavailable`) and nothing is broadcast.
