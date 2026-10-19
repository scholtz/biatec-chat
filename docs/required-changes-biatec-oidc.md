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
