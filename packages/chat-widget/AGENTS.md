# @biatec/chat-widget: UI rules for coding agents

The widget's look is the **Biatec AI** design system, imported in
[`docs/design/biatec-ai-widget/`](../../docs/design/biatec-ai-widget/README.md). Read
[`docs/design/README.md`](../../docs/design/README.md) first: it says how the design maps onto the product.
[PRODUCT-DESCRIPTION.md](../../PRODUCT-DESCRIPTION.md) wins on behaviour, the `BAI DS *.dc.html` pages win on look,
and the prototype `Biatec AI v2.dc.html` only shows layout and interaction (never port its demo logic or its
`_ds/modernist…` scaffold stylesheet, 0 radius, Archivo font or red accent).

The widget is embedded first in Biatec DEX (`scholtz/BiatecDEX`: Vue 3, PrimeVue 5 Aura, Tailwind 4, dark mode as
the `.p-dark` class on `<html>`). It must look native there and must not be affected by, or affect, the host's CSS.

## Always

- Use tokens via CSS custom properties (`--color-bg-primary`, `--color-brand-primary`, `--space-4`, `--radius-md`…).
  No raw hex or px value that a token carries.
- Dark is the default; light via `[data-theme="light"]`. Every component works in both from the same tokens.
- Inter only (Pacifico only in the "Biatec" wordmark). Never fetch fonts from the widget. 600 headings and values,
  500 labels and buttons, 400 body. Sentence case; uppercase only for 12 px data labels.
- `font-variant-numeric: tabular-nums` on every amount, balance, percentage, time and countdown.
- Price changes = arrow + sign + colour (`▲ +2.41%`). Status = colour + icon + word. Never colour alone.
- Text on green is `--color-brand-on` (`#04221A`). Never white on green.
- Radii: 6 tags · 8 buttons and inputs · 12 cards · 16 panels and the widget. Buttons 32/40/48 high; icon buttons
  have a 40 px hit area (44 px on touch), an `aria-label` and a tooltip on hover and focus.
- Lucide icons (`lucide-vue-next`), 2 px stroke, 16/20/24 px.
- Motion 150–220 ms, `cubic-bezier(.2,0,0,1)`; respect `prefers-reduced-motion`.
- 2 px focus ring `--color-border-focus`, offset 2 px. Dialogs trap focus, Esc closes, focus returns.
- Every user-visible string comes from the i18n catalogs (en, sk).
- Label the network (Testnet / Mainnet) at the point of action.

## Action safety UX

- Confirm is the only 48 px icon-led (shield-check) primary and always states the action and amount
  ("Confirm swap · 100 USDC"). It locks while submitting.
- Preview cards: dashed border until the user has confirmed, solid after. Use the fixed status set from
  `docs/design/README.md` (Preview status mapping) and never drop the label.
- Every preview shows fees, slippage or worst case, the limit bucket and whether phone approval is expected, before
  Confirm is enabled.
- Raising the agent mode (Analyze only → Prepare for approval, Resume) needs a separate confirm step with an explicit
  checkbox; lowering, Pause and Revoke are instant and reachable in one step from the header status pill.
- Log every analysis, preview, confirmation, consent result, receipt and mode change to the Activity timeline.
- The widget never handles keys, seeds or OAuth tokens and never uses the host page's wallet. Signing is done by the
  backend at Biatec OIDC. Agent mode and tier are enforced server-side; the UI only reflects them.

## Never

- Green outside the brand mark, primary action, selection and focus.
- Red/green as forecasts, hype copy, promised returns, urgency countdowns (the preview validity countdown is a
  factual expiry, not urgency: neutral colour, no flashing), flashing prices, emoji.
- Neon or glow, heavy glass, gradients beyond the `--ambient` background, robots or mascots, coin logos.
