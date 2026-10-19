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
5. `X402PayService.PayAsync` has the facilitator verify and settle the signed group, then fetches the resource with
   `X-PAYMENT`. Wiring this into the confirm flow is not part of this task.

## Settings

| Key | Default | Meaning |
|---|---|---|
| `X402__Enabled` | `false` | Platform flag. Nothing x402 is registered when false. |
| `X402__AllowedHosts__0..n` | empty (deny all) | Exact hosts or `*.example.com` |
| `X402__AllowedPorts__0..n` | `443` | |
| `X402__FacilitatorUrl` | unset | Unset uses the in-process mock facilitator (no funds move) |
| `X402__MaxAmountBaseUnits` | `5000000` | Cap per payment (5 USDC) |
| sidecar env `X402_ENABLED=true` | unset | The sidecar only serves the `x402` builder when set |

## SSRF policy

https only, no credentials or fragments, allowed ports only, DNS names only (no IP literals), host on the allowlist,
every resolved address public (re-checked at connect time against DNS rebinding), redirects never followed, no proxy,
response bodies capped at 64 KB. Seller text is untrusted data.

Only USDC is supported (mainnet 31566704, testnet 10458941). Seller side is out of scope.
