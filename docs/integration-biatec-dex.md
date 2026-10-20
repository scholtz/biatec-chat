# Embedding Biatec AI in Biatec DEX

How the chat widget (`@biatec/chat-widget`, shown to users as **Biatec AI**) goes into the Biatec DEX frontend,
[`scholtz/BiatecDEX`](https://github.com/scholtz/BiatecDEX). The specification is
[PRODUCT-DESCRIPTION.md](../PRODUCT-DESCRIPTION.md); the general integration guide is
[integration-widget.md](integration-widget.md); the look is in [design/README.md](design/README.md).

This repository never edits BiatecDEX. This file is the hand-off: the changes listed under "Host changes" are made in
the BiatecDEX repository by its maintainers, against the widget API this repo provides.

**Status:** the host API used below is built: `theme: 'host'`, `mode`, `useBiatecChat()` and `BiatecChatInline`
(T-062, T-063), Shadow DOM isolation and the Biatec AI tokens (T-062). Every snippet in this file is copied into the
BiatecDEX-like playground page (`packages/chat-widget/playground/dex.html`, T-068), which compiles it against the built
package and runs it. The Slovak catalog, the conversation and action cards, the Activity timeline and the permissions
dialog are built as well; none of them needs a host change.

## What BiatecDEX looks like to the widget

Read from `scholtz/BiatecDEX` `main` on 2026-10-20:

| Aspect | BiatecDEX | What the widget does |
| --- | --- | --- |
| Framework | Vue 3, Vite, Pinia, vue-router, PrimeVue 5 (Aura preset, prefix `p`), Tailwind 4 with `tailwindcss-primeui`, primeicons | Mounts its own Vue app into a Shadow DOM root, so Tailwind preflight and PrimeVue styles cannot reach it |
| Dark mode | `useTheme()` toggles the class `p-dark` on `<html>`; dark is the default; stored in `localStorage['biatec-theme']` | `theme: 'host'` watches that class with a `MutationObserver` and switches `data-theme` live |
| Tokens | `src/assets/app.css`: `--brand #2DD4A7` (dark) / `#0EA371` (light), `--page-bg #070B14`, frosted `--surface-card`, `--radius .85rem`, `--radius-lg 1.15rem` | The Biatec AI tokens were derived from these values, so no override is needed. Widget tokens live inside the shadow root and never collide with the DEX's `--radius-lg` or `--shadow-lg` |
| Font | Stack starting with `Inter` (Inter not loaded; Roboto loaded from Google Fonts) | Declares the same stack; never loads fonts itself |
| i18n | `vue-i18n`, locales en, sk, pl, hu, it, ru, zh, ko, de, es; `getCurrentLocale()` and `setLocale()` in `src/i18n` | Ships English and Slovak. Every other locale falls back to English. `setLocale()` follows the DEX language switch |
| Wallet | `@txnlab/use-wallet-vue` and the ARC-76 `algorand-authentication-component-vue` session | **Not used by the widget.** Biatec AI signs in with Biatec OIDC in its own popup and signs through the backend. The two sessions are independent; the widget never reads the DEX wallet |
| Overlays | PrimeVue automatic z-index: overlays (Select, menus, popovers) from 1001, dialogs and toasts from 1101 | Every fixed widget layer uses `z-index: var(--bc-z)`, default `1000`, so all PrimeVue layers open above the floating widget |
| Licence | PrimeVue 5 needs `license: import.meta.env.PRIMEVUE_LICENSE`, otherwise it shows a fixed "Invalid PrimeUI License" banner at the bottom right | Not affected; without the key the banner covers part of the launcher (the playground shows it unless `PRIMEVUE_LICENSE` is set) |

## Host changes in BiatecDEX

### 1. Register the plugin (`src/main.ts`)

After `app.use(PrimeVue, …)`:

```ts
import { BiatecChat } from '@biatec/chat-widget'
import { getCurrentLocale } from '@/i18n'

app.use(BiatecChat, {
  backendUrl: import.meta.env.VITE_BIATEC_CHAT_URL || 'https://chat.biatec.io',
  clientKey: 'biatec-dex',     // must be listed on the Biatec tenant
  locale: getCurrentLocale(),
  theme: 'host',               // follows .p-dark on <html>
  mode: 'floating',
})
```

No stylesheet import is needed: the styles are injected into the shadow root. `VITE_BIATEC_CHAT_URL` lets staging
builds point at a staging backend; add it to the DEX's `env.d.ts` as an optional string. `'biatec-dex'` is the
production key of the Biatec tenant; a local backend's dev tenant accepts only `dev`, which is what the playground
uses (the one line where it differs from this snippet).

### 2. Keep language in sync (`src/i18n/index.ts`)

```ts
import { useBiatecChat } from '@biatec/chat-widget'

export const setLocale = async (locale: SupportedLocale) => {
  // existing code …
  useBiatecChat().setLocale(locale)   // unknown locales fall back to English inside the widget
}
```

`useBiatecChat()` is a plain function, not a component composable: it is safe in this module and before the widget is
mounted. Regional tags are reduced to the language (`sk-SK` → `sk`).

### 3. Page context (trading and liquidity routes)

Pass what the user is looking at, so suggestions and answers fit the page. It is untrusted data to the backend and is
sent only with a message, so it never leaves the page before the user opened the panel:

```ts
useBiatecChat().setPageContext(`Biatec DEX · ${network} · pair ${base}/${quote} · pool app ${poolAppId}`)
```

Clear it when leaving those routes:

```ts
useBiatecChat().setPageContext(undefined)
```

### 4. "Ask Biatec AI" entry points

A ghost link under the order card (`src/components/TradingComponents/MarketOrder.vue`) and the add-liquidity form
(`src/components/LiquidityComponents/AddLiquidity.vue`):

```ts
useBiatecChat().ask(`Swap ${amount} ${quote} to ${base} on Biatec DEX`)
useBiatecChat().ask(`Add liquidity to the Biatec DEX ${base}/${quote} pool`)
```

`ask` opens the panel and sends the prompt as a visible user message. Nothing is executed: the assistant answers with
an action preview the user still has to review and confirm (and, if not signed in, a sign-in card). It returns a
promise of what happened, which the DEX can turn into a toast:

| Result | Meaning |
| --- | --- |
| `'sent'` | The prompt is in the conversation as the user's message |
| `'busy'` | A reply is still streaming; nothing was sent and the panel says so |
| `'empty'` | The prompt was blank |
| `'unavailable'` | No widget is mounted (or `mode: 'inline'` without a `BiatecChatInline`); nothing was sent |

The design puts the Biatec AI chip (16 px) next to the label. The package exports it as `BiatecAiChip`; it works
outside the widget root (its colours fall back to the Biatec green and ink), and the DEX can override them with
`--color-brand-primary`, `--color-brand-on` and `--color-text-secondary` on an ancestor. Put it beside the link
button, hidden from assistive technology, rather than in the button's `#icon` slot: the chip draws "AI" as visible
SVG text, and inside the button that text is missing from the button's accessible name, which axe reports as a
serious `label-content-name-mismatch` (WCAG 2.5.3):

```vue
<span class="inline-flex" aria-hidden="true"><BiatecAiChip :size="16" /></span>
```

### 5. Optional inline mode

On wide trading layouts the widget can sit in a column next to the order card instead of floating:

```vue
<BiatecChatInline class="w-full" />   <!-- minmax(360px, 420px) column; same session as the floating widget -->
```

Import it with `import { BiatecChatInline } from '@biatec/chat-widget'`. It renders into its own shadow root and shares
the floating widget's conversation. Only one instance renders at a time: while the inline panel is mounted the floating
launcher hides. With `mode: 'inline'` there is no launcher at all and `ask` works only while a `BiatecChatInline` is
mounted.

### 6. Position, Content Security Policy and hosting

- The floating launcher sits 24 px from the bottom right. If it covers DEX controls, move it from `app.css` with
  `[data-biatec-chat-host] { --bc-offset-bottom: 88px; }` (and `--bc-offset-right`). `--bc-z` can be set the same way.
- `connect-src https://chat.biatec.io`, and popups to `https://chat.biatec.io` and `https://oidc.biatec.io` allowed.
  The widget injects one `<style>` element into its shadow root, so `style-src` needs the same allowance PrimeVue's
  runtime theme already needs.
- `dex.biatec.io` (and any staging origin) is in the Biatec tenant's `allowedOrigins`, and `biatec-dex` in its
  `clientKeys`. Without that the widget falls back to the anonymous public transport (see integration-widget.md).

## Known limitations

The three found with the playground are fixed in the widget (T-077): Esc pressed in or while a host modal dialog
(`[role=dialog][aria-modal=true]` or an open modal `<dialog>`) is open is left to that dialog, the full-screen Tab trap
yields while such a dialog is open, and the widget root's `lang` is the language actually shown (English for German),
not the requested one (`packages/chat-widget/src/components/BiatecChatRoot.vue`). A host dialog that is not marked
modal in one of those ways is not recognised.

## Trying it without the DEX

`pnpm --filter @biatec/chat-widget dev`, then open `/dex.html`. The page has the DEX's PrimeVue Aura setup, Tailwind 4,
the `app.css` token values, a theme toggle that flips `.p-dark`, a language switch (en, sk, de), a pair header, an order
card with "Ask Biatec AI about this order", a PrimeVue Dialog (cog button) and Toast, and a "Wide layout" switch that
mounts `BiatecChatInline`. The backend is `http://localhost:5000` unless `VITE_BIATEC_CHAT_URL` is set (empty counts
as unset), and the client key is the dev tenant's `dev` unless `VITE_BIATEC_CHAT_CLIENT_KEY` is set. The dev tenant
allows the origin `http://localhost:5173`, so keep Vite's default port to test sign-in and previews; without a backend
the panel shows the prompt and an error reply. Query parameters for screenshots: `?theme=light|dark`,
`?lang=en|sk|de`, `?wide`, `?open`, `?dialog`; `?theme` and `?lang` apply to that load only and are not stored.

`pnpm --filter @biatec/chat-widget build && pnpm --filter @biatec/chat-widget typecheck:playground` compiles the page,
and so every snippet above, against the built `dist/index.d.ts`. `test/dex-integration.test.ts` runs the same host code
against PrimeVue in jsdom and fails when a snippet line here is missing from the playground.

## Checks before shipping in BiatecDEX

- [ ] Toggle light/dark in the DEX header: the widget follows without reload.
- [ ] Switch the DEX language to Slovak, then to German: the widget shows Slovak, then English.
- [ ] Open a PrimeVue dialog while the widget is open: the dialog is on top, and Esc and Tab stay with the dialog.
- [ ] Mobile width (< 640 px): the open widget is full screen, the launcher does not cover the DEX bottom controls.
- [ ] "Ask Biatec AI" on the order card opens the panel with the prompt, and no action runs without Confirm.
- [ ] Network tab: no OAuth token or code reaches DEX scripts; the DEX wallet session is untouched by Biatec AI sign-in.
