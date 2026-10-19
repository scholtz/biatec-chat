# Widget design: Biatec AI design system

The visual design for the chat widget is the **Biatec AI** design handoff in
[`biatec-ai-widget/`](biatec-ai-widget/README.md) (exported from Claude Design, imported unchanged except that its
agent rule sheet moved to [`packages/chat-widget/AGENTS.md`](../../packages/chat-widget/AGENTS.md)).

Order of precedence when sources disagree:

1. [PRODUCT-DESCRIPTION.md](../../PRODUCT-DESCRIPTION.md) wins on **behaviour**: tiers, capabilities, action
   previews, consent, signing, evidence, security invariants.
2. The design-system pages `biatec-ai-widget/design/BAI DS *.dc.html` and the tokens in
   `biatec-ai-widget/tokens/` win on **look**: colour, type, spacing, radius, components, copy voice.
3. The prototype `biatec-ai-widget/design/Biatec AI v2.dc.html` shows layout and interaction. Its demo logic,
   simulated data and scaffold stylesheet (`_ds/modernist…`) are not ported.

Open any `*.dc.html` file directly in a browser to inspect it (no build step; `support.js` must stay next to it).

## How the design maps to the product

The prototype is a generic crypto-trading assistant with simulated trades. Biatec Chat executes real, consent-gated
actions through Biatec OIDC. This table is the agreed translation; the specification section "Visual Design" restates
it as requirements.

| Design concept | In Biatec Chat |
| --- | --- |
| Product name "Biatec AI", chip symbol, "Biatec" wordmark | User-facing name of the widget is **Biatec AI**. Package, backend and repo keep the Biatec Chat name. Inside partner hosts only the chip is used |
| Tokens aligned with BiatecDEX `src/assets/app.css` | Widget tokens are the handoff tokens. Dark is default; `theme: 'host'` follows BiatecDEX's `.p-dark` class on `<html>` |
| Web Component + Shadow DOM for isolation | The widget stays a Vue 3 plugin but renders into a Shadow DOM root on its host element; hosts override `--color-*`, `--font-*`, `--radius-*`, `--space-*`, `--shadow-*` on that element |
| Modes floating / inline / workspace, minimized bar | Plugin option `mode`; floating is the default; below 640 px an open panel is full screen |
| Header status pill "Status: Analyze only ▾" | Header shows tier (Guest, or email and primary address) with Sign in / Sign out; the status pill shows the **agent mode** and opens the permissions dialog |
| Autonomy 01 Analyze only | Agent mode `analyze`: fund-moving capabilities are withheld from the model and rejected server-side. The only mode in the anonymous tier |
| Autonomy 02 Prepare for approval | Agent mode `prepare` (default when signed in): today's behaviour. Every action needs Confirm in the widget and, above the threshold, approval in Biatec Authenticator |
| Autonomy 03 Rule-based automation | Shown, disabled, labelled "Not available yet". The spec requires a confirmation per action; automation is long-term vision |
| Automation limits (max order, daily allocation, stop at loss…) | Replaced by the user's Biatec OIDC spending limits (daily, weekly, monthly) from `limits.view`; lowering through `limits.update` where the tenant has `manage-limits` |
| Pause / Resume agent | Agent `paused` flag: behaves as `analyze` and disables Confirm on open previews, until resumed. Server-enforced |
| Revoke all access | Cancels every open preview, then signs out (the backend logout, which deletes the vault entry and revokes or ages out the tokens) |
| Raising autonomy needs a confirm step; lowering is instant | Same, for `analyze` → `prepare` and for Resume |
| TradeProposalCard (dashed until approved, solid after) | The action preview card, bound to `ActionPreview` from `@biatec/chat-protocol`, with the status set below |
| "Approve demo trade · $100.00" (48 px, shield icon) | Confirm button, the only 48 px icon-led primary, states action and amount ("Confirm swap · 100 USDC") |
| Review trade dialog with acknowledgement checkbox | Review step before Confirm answering the five questions: what happens, permissions involved, order details, financial implications, how to stop it |
| Downside scenario "If BTC falls 10%…" | Shown when the preview carries a fiat value; otherwise omitted |
| Confirmation card with receipt id | Receipt card: transaction IDs with explorer links and "Download evidence" |
| Activity aside | Client-side timeline of this session's events (analysis, previews, confirmations, consent, receipts, mode changes). The evidence export stays the durable record |
| MarketInsightCard, Portfolio card | Data cards: balances from `wallet.balance`; market snapshot from public Biatec DEX data (planned, needs a new anonymous capability) |
| "Ask Biatec AI about this order →" on the host page | Programmatic host API (`useBiatecChat().ask(...)`) plus `pageContext`; host-side changes for BiatecDEX are in [integration-biatec-dex.md](../integration-biatec-dex.md) |
| Signing "stays in the host's wallet flow" | **Not adopted.** Signing always goes from the backend to Biatec OIDC; the widget never uses the host's wallet (BiatecDEX's use-wallet / ARC-76 session is unrelated) |
| "Demo · simulated" labels | Replaced by a network label at the point of action ("Testnet" or "Mainnet"); no simulated data is shown as real |
| Chats history sidebar, DCA strategy card, Attach button | Not in v1 |
| Google Fonts import in `biatec-tokens.css` | Not ported: the widget never fetches fonts. It declares `Inter, system-ui, …`; BiatecDEX and other hosts load Inter if they want it |

### Preview status mapping

The design's fixed status set, mapped onto `ActionPreviewStatus` and `ConsentState`:

| Design state (glyph) | Protocol state | Label shown |
| --- | --- | --- |
| ◷ Awaiting approval (warning, dashed) | `AwaitingConfirmation` | Awaiting your confirmation |
| ◷ Awaiting approval (warning, dashed) | `AwaitingScope` | Needs signing permission |
| ◷ Awaiting approval (warning, dashed) | `AwaitingApproval` | Waiting for approval on your phone |
| ⛉ Approved (brand, solid) | `Signing` | Approved · signing |
| ◌ Pending execution (info, solid) | `Submitted` | Submitted · waiting for confirmation |
| ✓ Completed (success, solid) | `Completed` | Completed |
| ✕ Rejected (neutral, solid) | consent `Denied` | Rejected on your phone |
| ! Failed (danger, solid) | `Failed` | Failed, with the reason the backend reports |
| ! Failed (danger, solid) | consent `TimedOut` | Approval timed out · nothing was signed |
| ⊘ Cancelled (neutral, solid) | `Cancelled`, `Expired` | Cancelled / Expired |

"Draft" has no protocol state: a request still being shaped in chat has no card.
