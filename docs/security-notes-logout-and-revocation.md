# Security notes: logout, sweeper and token revocation

Implements the "Logout" paragraph of `PRODUCT-DESCRIPTION.md`. Code: `backend/Biatec.Chat.Api/Auth/Logout/` and `backend/Biatec.Chat.Api/Sweeper/`.

## What happens when a session ends

Logout (`GET /auth/logout`), the idle sweep and the absolute-lifetime sweep all run the same order:

1. Read the refresh token (and, for logout, the ID token) from the vault into memory.
2. Revoke the refresh token at Biatec OIDC (RFC 7009, `Oidc:Revocation:Url`). If that is not possible, park the token
   encrypted with the vault's key provider (`Vault:KeyProvider`: `Secret` in stage and production, `Kms` or `Dev`
   elsewhere) in `PendingRevocation`.
3. Delete the vault entry, then the `ChatSession` row.
4. Logout only: clear the `__Host-biatec_session` cookie and redirect to `/connect/endsession` with `id_token_hint`.

The sweeper runs every minute (`Sweeper:Interval`). It skips the idle timeout for a session with an open `ConsentRequest`
(status `Pending`, not yet expired); the absolute lifetime always applies. If the vault cannot be read, the session is left
for the next run so a token is never dropped unrevoked because Redis was briefly down.

## Known limitation until Biatec OIDC ships revocation

Biatec OIDC has no revocation endpoint yet (row 14 of `required-changes-biatec-oidc.md`). Until `Oidc:Revocation:Url` is
set, nothing is sent: every ended session's refresh token is parked and its vault entry destroyed, and the token **ages out
at Biatec OIDC (30 days today)**. A refresh token stolen from the vault before the session ended stays usable at the IdP
until then. Once the URL is configured, the retry worker (a no-op until then, apart from purging expired rows) revokes the
parked tokens. A parked token is deleted when revocation succeeds or after `Oidc:Revocation:RefreshTokenLifetime` (30 days).
The URL is ignored unless it is on the issuer's origin. Revocation of exactly the presented token (not the whole grant) is
part of row 14; the product depends on it for scope elevation.

## Scope elevation

An elevation (`/auth/elevate`) exchanges a new code for tokens, then replaces the vault entry **under the same Redis lock the refresh
uses** (`ITokenVault.ReplaceAsync`): the entry it replaces is read inside the lock, so a refresh that rotated the tokens meanwhile is
neither overwritten blindly nor left to run against the replaced entry. The refresh token it superseded is then revoked, or parked
in `PendingRevocation` like a logged-out session's, so the sweeper revokes it once a revocation URL is configured. If that last step fails
the elevation still stands (the failure is logged by type).

## ID token in the logout redirect

The spec has the browser follow RP-initiated logout with `id_token_hint`, so the ID token appears once in the `Location`
header of the logout redirect. To keep it out of script reach, logout is a top-level navigation (not a fetch/XHR response),
with `Sec-Fetch-*` checks, `Cache-Control: no-store` and `Referrer-Policy: no-referrer`. The access and refresh tokens never
leave the backend.
