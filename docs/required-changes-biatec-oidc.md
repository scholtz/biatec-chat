# Required changes to Biatec OIDC (fallbacks in use)

Changes this product needs in Biatec OIDC (see "Required Changes To Biatec OIDC" in `PRODUCT-DESCRIPTION.md`). This file lists
only the ones where biatec-chat currently runs on the documented fallback.

## Confidential-client registration for tenants (T-009)

- **Needed:** confidential-client dynamic registration for tenants that pass domain verification (today `POST /register` issues public clients only).
- **Fallback in use:** an operator adds each tenant's client (`Tenant.OidcClientId`, authorization-code grant, S256 PKCE, `resource` indicator
  `https://mcp.biatec.io/tools`, redirect URI `Tenant.RedirectUri`) to Biatec OIDC's static client list. The client secret is supplied to the
  backend as configuration `Secrets:<path of Tenant.ClientSecretRef>` (for example `Secrets__biatec-chat__oidc-client-secret`), never in the repo.
- **Status:** the `biatec-chat` client registration is not confirmed to exist on oidc.biatec.io. `/auth/login` has been tested only against the in-repo
  mock OIDC (`backend/Biatec.Chat.Tests/Auth/MockOidc.cs`); the stage run needs the registration first.

## Tenant onboarding registration (T-045)

- **Needed:** the same confidential-client dynamic registration (RFC 7591) as above, called by `POST /onboarding/tenants` once the DNS TXT proofs pass.
- **Fallback in use:** `IOidcClientRegistrar` defaults to `NoDynamicRegistration`, so the endpoint answers `202 pending-operator-approval` and stores nothing.
  An operator adds the static client, puts the secret in the secret store, then calls `POST /onboarding/operator/tenants` (bearer `Onboarding:OperatorKey`)
  with the `oidcClientId` and a `secret://` reference; the DNS proofs are re-checked before the tenant row is written.
- **Also missing:** a real `ISecretStore` (AWS Secrets Manager or Kubernetes secrets); only the interface and a throwing default exist.
