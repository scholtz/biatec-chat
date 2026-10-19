# Integrating the chat widget

How a host site embeds `@biatec/chat-widget`. The specification is [PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md) and
wins on any conflict. What works today is marked; see [architecture.md](architecture.md) for the full status.

**Status of the widget on `main`:** the package, plugin install, launcher, panel, theme tokens and options are built and tested.
The cookie probe, public fallback transport, streaming chat UI, sign-in popup handling, action preview and consent cards, and
the Slovak translation are planned (tasks T-040 to T-043). The rework to the **Biatec AI** design system (Shadow DOM isolation,
`theme: 'host'`, `mode`, `useBiatecChat()`, permissions and activity) is planned in tasks T-062 to T-069; `theme: 'host'`, `mode`, `BiatecChatInline`
and `useBiatecChat()` are built (T-062, T-063); options marked *planned* below are not. For Biatec DEX specifically, see [integration-biatec-dex.md](integration-biatec-dex.md). The package version is `0.0.0` and is not published to npm yet, so
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
| `theme` | no | `auto` | `auto`, `light`, `dark` or `host` (follows the host's dark class on `<html>`) |
| `hostDarkClass` | no | `p-dark` | The class `theme: 'host'` watches (PrimeVue's `darkModeSelector`, as in Biatec DEX and Biatec Web) |
| `mode` | no | `floating` | `floating` (launcher bottom-right), `inline` (no launcher; mount `BiatecChatInline` in a host column) or `workspace` (launcher, panel opens full screen). See [Modes and the host API](#modes-and-the-host-api) |
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

### Modes and the host API

The widget has three modes, chosen with the `mode` option (`'floating' | 'inline' | 'workspace'`; any other value falls back to
`floating`). `ChatMode`, `ChatTheme`, `BiatecChatOptions`, `BiatecChatApi` and `AskResult` are exported as types.

| Mode | What the user sees | Notes |
| --- | --- | --- |
| `floating` (default) | Launcher bottom-right; it opens a floating panel that can be minimized to a bar, expanded to the workspace frame and closed | Below 640 px wide an open panel is full screen |
| `workspace` | The same launcher, but the panel opens full screen with the Activity aside shown | Closing returns to the launcher |
| `inline` | No launcher and no floating panel. A panel appears only where you mount `BiatecChatInline` | Never goes full screen and does not close on Esc; its header has no Expand |

While a `BiatecChatInline` is mounted, the floating launcher and panel hide in every mode, and the inline panel shares the floating
widget's conversation and session. Unmounting it brings the launcher back (not in `mode: 'inline'`).

#### `BiatecChatInline`

A component that renders the widget as a panel in your layout, for example a column next to an order form. It is rendered into its
own Shadow DOM root, like the floating widget, so host CSS and widget CSS never mix. It takes two optional props:

| Prop | Meaning |
| --- | --- |
| `options` | `BiatecChatOptions`, used only when the plugin is not installed (see below) |
| `services` | Transport and auth overrides for tests |

```vue
<script setup lang="ts">
import { BiatecChatInline } from '@biatec/chat-widget'
</script>

<template>
  <div class="trade-page" style="display:grid;grid-template-columns:1fr minmax(360px,420px);gap:24px">
    <OrderForm />
    <BiatecChatInline />   <!-- height and width come from your column -->
  </div>
</template>
```

Install the plugin with `mode: 'inline'` so that nothing floats and the inline panel is the only UI:

```ts
createApp(App)
  .use(BiatecChat, { backendUrl: 'https://chat.biatec.io', clientKey: 'biatec-dex', mode: 'inline' })
  .mount('#app')
```

Options come from the plugin. If the plugin is not installed, pass them as the `options` prop; without either, the component logs a
warning and renders nothing. The probe for an existing session (`GET /session`) runs when the inline panel mounts.

`BiatecChatRoot` is also exported, for a host that sets `autoMount: false` and mounts the floating widget itself with
`<BiatecChatRoot :options="..." />`. Then import `@biatec/chat-widget/style.css`; the auto-mounted widget needs no stylesheet.

#### `useBiatecChat()`

`useBiatecChat()` returns the host API. It is not bound to a component, so call it from any module or handler, including before the
widget is mounted. It always returns the same object. The calls go to the most recently mounted widget.

| Call | Does |
| --- | --- |
| `open()` | Opens (or restores from the minimized bar) the panel and moves focus to the composer. The first open starts the session probe; nothing touches the network on page load. If called before a widget is mounted, the first widget opens as soon as it mounts |
| `close()` | Closes the panel back to the launcher and cancels an early `open()` |
| `ask(prompt): Promise<AskResult>` | Opens the panel and sends `prompt` as a normal, visible user message. Resolves to an `AskResult` |
| `setPageContext(text?)` | Sets the opt-in page context sent with later messages. `undefined`, `null` or a blank string clears it, and also overrides the `pageContext` plugin option |
| `setTheme(theme)` | `'auto' \| 'light' \| 'dark' \| 'host'`, applied without remounting. An unknown value logs a warning and is ignored |
| `setLocale(locale)` | Switches the widget language without remounting. Unknown locales fall back to English (only English ships today). An empty or non-string value is ignored |

`ask` resolves to one of:

| Result | Meaning |
| --- | --- |
| `sent` | The prompt is now a visible user message in the conversation. Only this result means something was sent |
| `busy` | A reply is still streaming, or another `ask` is starting. Nothing was sent and the panel shows a short notice |
| `empty` | The prompt was blank or not a string |
| `unavailable` | No widget is mounted, or `mode: 'inline'` and no `BiatecChatInline` is mounted. The prompt could not be shown, so it was not sent (a console warning is logged) |

```ts
import { useBiatecChat } from '@biatec/chat-widget'

const chat = useBiatecChat()

// Page context is untrusted data; it is attached to each message at send time, so it never leaves the page before
// the user has opened the panel and a message is sent.
chat.setPageContext('Biatec DEX · testnet · pair EURS/USDC')

const result = await chat.ask('Add liquidity to the Biatec DEX EURS/USDC pool')
if (result === 'busy') {
  // The assistant is still answering the previous question; offer a retry instead of queueing.
} else if (result === 'unavailable') {
  // Mount BiatecChatInline (inline mode) or install the plugin first.
}

chat.setTheme('host')   // follow the host's dark class on <html> (see hostDarkClass)
chat.setLocale('sk')    // falls back to English until the Slovak catalog ships
chat.open()
chat.close()
```

`ask` never executes anything: actions still end in a preview the user reviews and confirms. The playground
(`packages/chat-widget/playground/main.ts`) drives every call from buttons; run it with `pnpm --filter @biatec/chat-widget dev`.

#### Known limitations

- An inline panel mounted before the floating root has its own session, not a shared one.
- An early `open()` in `mode: 'inline'` (before a `BiatecChatInline` is mounted) is dropped; the inline panel is always shown while mounted.
- When a floating widget already exists, the `options` prop of `BiatecChatInline` is ignored in favour of the floating widget's options.
- An `ask` made during the first session probe still sends if the user closes the panel meanwhile.
- Esc closes the Activity aside even when the aside has no content yet (the Activity timeline arrives with T-066).
- Theme and viewport-size watching is duplicated between the floating and the inline root (a small cost, no visible effect).
- `ask` is not queued: while a reply streams it returns `busy`.
- `setPageContext` takes a plain string and is sent as the product field of the chat request's host context; there is no structured form yet.

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
