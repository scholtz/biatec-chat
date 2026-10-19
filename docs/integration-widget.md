# Integrating the chat widget

How a host site embeds `@biatec/chat-widget`. The specification is [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) and
wins on any conflict. What works today is marked; see [architecture.md](architecture.md) for the full status.

**Status of the widget on `main`:** the package, plugin install, launcher, panel, theme tokens and options are built and tested.
The cookie probe, public fallback transport, streaming chat UI, sign-in popup handling, action preview and consent cards, and
the Slovak translation are planned (tasks T-040 to T-043). The rework to the **Biatec AI** design system (Shadow DOM isolation,
`theme: 'host'`, `mode`, `useBiatecChat()`, permissions and activity) is planned in tasks T-062 to T-069; the options and APIs
marked *planned* below arrive with it. For Biatec DEX specifically, see [integration-biatec-dex.md](integration-biatec-dex.md). The package version is `0.0.0` and is not published to npm yet, so
install it from the workspace until it is.

## 1. Install the plugin

Vue 3 is a peer dependency (`^3.4.0`).

```sh
# once published
pnpm add @biatec/chat-widget
# today, inside this monorepo
pnpm add @biatec/chat-widget@workspace:*
```

Register it with one `use` call and import the stylesheet:

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

The plugin mounts its own small Vue app into a `<div data-biatec-chat-host>` appended to `document.body`, so it never touches the
host's component tree or root element. A second install (hot reload, two apps) reuses the existing widget instead of adding
another. Run the playground with `pnpm --filter @biatec/chat-widget dev`.

### Options

| Option | Required | Default | Meaning |
| --- | --- | --- | --- |
| `backendUrl` | yes | | Base URL of your chat backend. Trailing slashes are removed. Must be same-site with the host page to get the authenticated tier |
| `clientKey` | yes | | A label for the host, not a secret. The backend checks it against the tenant's registered keys on the cookie transport |
| `locale` | no | `en` | Message language. English now; Slovak is planned |
| `theme` | no | `auto` | `auto`, `light` or `dark`; *planned:* `host` follows the host's dark class on `<html>` |
| `hostDarkClass` | no | `p-dark` | *Planned.* The class `theme: 'host'` watches (PrimeVue's `darkModeSelector`, as in Biatec DEX and Biatec Web) |
| `mode` | no | `floating` | *Planned.* `floating` (launcher bottom-right), `inline` (fills a host column; or mount `BiatecChatInline`) or `workspace` (full screen) |
| `suggestions` | no | `default` | `default` or your own list of sample prompts |
| `pageContext` | no | none | Opt-in string describing the page (for example the pool being shown). Nothing is read from the host page unless you set it. The assistant treats it as untrusted data |
| `autoMount` | no | `true` | Set `false` to provide the options only and mount `BiatecChatRoot` yourself |

The plugin throws at install if `backendUrl` or `clientKey` is missing.

### Styling

The widget's look is the Biatec AI design system ([design/README.md](design/README.md)); dark is the default. Today styles are
scoped under a `biatec-chat` root with CSS custom properties (`packages/chat-widget/src/styles.css`). *Planned (T-062):* the
widget renders into a Shadow DOM root on its host element, so host CSS such as Tailwind preflight or a PrimeVue theme cannot reach
it, and the styles are injected there (no stylesheet import needed). Restyle it only through CSS custom properties on the host
element: `--color-*`, `--font-*`, `--radius-*`, `--space-*`, `--shadow-*`, and `--bc-z` for the stacking order (default 1000,
below PrimeVue overlays). The widget never loads web fonts; it uses Inter when the host provides it.

### Driving the widget from the host (planned, T-063)

```ts
import { useBiatecChat } from '@biatec/chat-widget'

const chat = useBiatecChat()
chat.ask('Add liquidity to the Biatec DEX EURS/USDC pool') // opens the panel and sends a visible user message
chat.setPageContext('Biatec DEX · testnet · pair EURS/USDC') // untrusted data, sent only after the panel is opened
chat.setTheme('host'); chat.setLocale('sk'); chat.open(); chat.close()
```

`ask` never executes anything: actions still end in a preview the user reviews and confirms.

### Accessibility

The launcher is a keyboard-accessible button, the stylesheet honours `prefers-reduced-motion`, and the panel is a labelled dialog that
moves focus into the panel on open. Full WCAG 2.1 AA coverage (focus trap, live region for streamed messages) arrives with the chat
UI task.

## 2. Choose how the backend is hosted

The session cookie is host-only (`__Host-biatec_session`, `Path=/`, no `Domain`) and must be first-party to your page. The widget
never relies on third-party cookies. That gives three modes:

| Mode | Backend host | Tier | Notes |
| --- | --- | --- | --- |
| Biatec sites | `chat.biatec.io`, pages on `web.biatec.io` and `dex.biatec.io` | Authenticated | Same-site but cross-origin: credentialed CORS with the exact origin echoed. The apex `biatec.io` is not an allowed origin |
| Third party | Your own subdomain, for example `chat.example-bank.com`, pointed at the Biatec Chat backend | Authenticated | The backend is multi-tenant by `Host` header. `backendUrl` must be same-site with your page. Path-based reverse proxying is not supported |
| Unregistered or cross-site host | Any backend's public endpoint | Anonymous only | Planned: `POST {backendUrl}/public/chat` does not exist yet |

## 3. Register your host as a tenant (third-party mode)

The tenant onboarding API is built (`backend/Biatec.Chat.Api/Onboarding/`). All routes are cookie-less, rate limited per remote
IP and capped at 32 KiB bodies.

1. Ask for the DNS challenges for your host and origins:

   ```http
   POST /onboarding/challenges
   Content-Type: application/json

   {
     "host": "chat.example-bank.com",
     "signInPageUrl": "https://www.example-bank.com/login",
     "allowedOrigins": ["https://www.example-bank.com"],
     "clientKeys": ["example-web"]
   }
   ```

   The response lists the `TXT` records to publish (names start with `_biatec-chat-challenge.`, values with
   `biatec-chat-verification=`): one for the backend host and one for each origin that is not the host or a name beneath it.
   A sibling or apex name is never inferred from a subdomain proof.
2. Publish the records, point the CNAME of the host at the backend, then call `POST /onboarding/tenants` with the same body.
3. Every allowed origin must be on the same registrable domain as the backend host. Other domains are rejected with an explanation,
   because the cookie could never be first-party there.
4. Today `POST /onboarding/tenants` answers `202 pending-operator-approval` and stores nothing, because Biatec OIDC cannot register
   confidential clients dynamically yet. An operator adds the client to Biatec OIDC's static list, puts its secret in the secret
   store and calls `POST /onboarding/operator/tenants` (bearer operator key). The DNS proofs are re-checked first. Details and the
   fallback table are in [required-changes-biatec-oidc.md](required-changes-biatec-oidc.md).

TLS for tenant hostnames (ACME with cert-manager on Kubernetes) belongs to the deployment tasks and is not in the repo yet.

## 4. What the backend enforces for you

Integrators do not need to implement these, but should know them:

- **No tokens in the page.** The widget only ever holds the opaque cookie. Sign-in runs in a popup the backend controls.
- **CSRF.** Every state-changing request needs the `X-Biatec-Chat` header, an `Origin` in your allow-list and a registered
  `X-Biatec-Client-Key` (the `clientKey`). Credentialed CORS echoes the exact origin and never uses a wildcard.
- **Sessions.** 15 minutes idle, 8 hours absolute. The widget calls `GET /session` when the panel first opens and
  `POST /session/keepalive` while an action is pending.
- **Scopes.** Sign-in asks for `openid profile email`. The `sign` scope is requested only when an action first needs it, through a
  second popup. Only `sign` and `manage-limits` can ever be requested that way.
- **Tier.** Anonymous visitors can learn about Biatec and Algorand but get no wallet tools. The backend rejects any wallet call from
  them, whatever the model says.

## 5. Checklist before going live

- [ ] Backend host is a subdomain on the same registrable domain as your pages.
- [ ] DNS TXT proofs published for the host and every origin; tenant row created (operator step today).
- [ ] `clientKey` listed on the tenant and passed in the plugin options.
- [ ] Your Content Security Policy allows `connect-src` and popup navigation to the backend host.
- [ ] You pass `pageContext` only with data you are happy to send to the backend and the model.
