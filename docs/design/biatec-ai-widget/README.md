# Handoff: Biatec AI — Conversational Trading Widget + Design System

## Overview
Biatec AI is an embeddable conversational crypto-trading assistant for Biatec DEX (repo `scholtz/BiatecDEX`, Vue 3 + PrimeVue) and partner exchanges. It analyzes markets, explains strategies, reviews portfolio exposure and **prepares** trades. The user always decides: nothing executes without explicit approval, inside permissions the user sets.

Brand promise: **Intelligent by design. Transparent by default. Always under your control.**

This bundle contains:
1. The **Biatec AI visual & design system** (brand, foundations, components, patterns, guidelines).
2. A working **widget prototype** (floating launcher → chat panel → full-screen workspace) with the complete analyze → propose → review → approve → receipt journey, permissions and activity log.

## About the design files
Everything in `design/` is a **design reference built in HTML** — prototypes that show intended look and behaviour, not production code. Recreate them in the target codebase's environment using its patterns (for BiatecDEX: Vue 3 SFCs + PrimeVue, tokens as CSS custom properties). If building the widget standalone, use a Web Component (`<biatec-ai-widget>`) rendering into Shadow DOM so host CSS can't leak in or out.

Open any `design/*.dc.html` file directly in a browser to inspect it (they need `support.js` next to them; no build step).

## Fidelity
**High-fidelity.** Final colors, type, spacing, copy and interactions. Match pixel values below. All market data, portfolios and trades in the prototype are **simulated** — replace with real services but keep every "demo / simulated" label until real execution exists.

## Source of truth — read this first
- **Tokens:** `tokens/biatec-tokens.css` (CSS vars) and `tokens/tokens.json` (same values, machine-readable). Dark is default (`:root` / `[data-theme="dark"]`), light is `[data-theme="light"]`. Never hard-code a hex that a token carries.
- **Visual rules:** `design/BAI DS *.dc.html` pages. Where the widget prototype and the DS pages disagree, **the DS pages win**.
- **Implementation note on the prototype:** `Biatec AI v2.dc.html` was scaffolded on a generic utility stylesheet (`_ds/modernist…/styles.css`, classes `.btn .card .field .input .table .dialog .nav`) and then re-themed at runtime by `applyTheme()` with Biatec values. **Do not port that stylesheet or its 0-radius / Archivo / red defaults.** Port the *Biatec* values. Variable mapping used by the prototype:

| Prototype var | Biatec token |
| --- | --- |
| `--color-bg` | `--color-bg-primary` |
| `--color-surface` | `--color-bg-secondary` |
| `--color-text` | `--color-text-primary` |
| `--color-neutral-700` | `--color-text-secondary` |
| `--color-divider` | `--color-border-default` (≈ .16 alpha) |
| `--bai-border-strong` | `--color-border-strong` |
| `--color-accent` / `-600` / `-700` / `-100` | `--color-brand-primary` / `-hover` / `-text` / `-subtle` |
| `--bai-success/warning/danger(-subtle)` | `--color-status-*` |
| `--font-heading/-body` | `--font-sans` (Inter) |

---

## Design system

### Brand
- Personality: Intelligent · Trustworthy · Controlled · Progressive · Minimal.
- Principles: 01 Clarity over complexity · 02 Control over blind automation · 03 Transparency over persuasion · 04 Consistency over novelty · 05 Calm over urgency · 06 Accessibility by default.
- **Avoid:** coin logos, neon and glow, robots/mascots, heavy glass, decorative charts, red/green as the only signal, hype copy, countdowns, flashing prices.

### Logo (`assets/bai-symbol.svg`, `assets/bai-symbol-small.svg`, `design/BAI Logo.dc.html`)
- Symbol: 64×64 viewBox. Chip `rect x=11 y=11 w=42 h=42 rx=9` fill `#2DD4A7`; "AI" Inter 800, 22px, letter-spacing .5, fill `#04221A`, baseline y=39.8; 12 pins (3 per side, round caps, stroke 3.8 — 3.2 at ≥96px) in `--color-text-secondary`.
- **≤ 24px:** pins drop, chip becomes `x=4 w=56 rx=12`, "AI" 30px baseline 42.6.
- Horizontal lockup: "Biatec" in Pacifico (`--font-script`) at 0.72× symbol size, gap 0.12× size, then symbol.
- Mono variants: white (`#F5F8FC` chip, `#070B14` text) and dark (`#070B14` chip, `#F5F8FC` text). Launcher uses mono dark on the green button.
- App icon: Night tile `#070B14`, 22% corner radius, symbol at 70% of tile. Clear space = one pin length. Min sizes: symbol 16px, compact 56px wide, horizontal 96px wide.
- Inside partner platforms use the **chip only**, next to the host's brand.

### Color
Core: Night `#070B14` (app bg) · Card `#111827` · Panel `#161E2E` · Interactive `#1E293B` · Text `#E6EDF6` · Muted `#9AA7BA` · Biatec Green `#2DD4A7` · Green Strong `#14B88A` · Deep Teal `#0F3B53` · Sky `#38BDF8`.

Semantic tokens (dark / light): see `tokens/tokens.json`. Key rules:
- **Green = brand mark, primary action, selection, focus. Nothing else.**
- Green/red describe the **past**, never a forecast. Red only for negative movement, destructive actions and real risk. Amber = needs the user (e.g. Awaiting approval).
- Every status pairs **color + icon + word**. Price changes always show arrow + sign + color: `▲ +2.41%`, `▼ −0.86%`.
- Label on green is always `#04221A` (9.6:1). **Never white on green** (1.9:1, fails).
- Background: `var(--ambient)` (three soft radial glows) layered over `--color-bg-primary`, `background-attachment: fixed`. Cards are frosted: `--color-bg-secondary` (optionally semi-transparent) + `backdrop-filter: blur(14px)`. Shadows only separate floating layers.

Measured contrast: text on Night 16.6:1 · muted on Night 8.0:1 · green on Night 10.4:1 · danger on Night 7.3:1 · light green text `#087453` on white 5.8:1 · light secondary on white 7.6:1.

### Typography — Inter (+ Pacifico for the wordmark only)
| Style | Size/LH | Weight | Tracking |
| --- | --- | --- | --- |
| Display | 40/48 | 600 | −0.02em |
| H1 | 30/38 | 600 | −0.015em |
| H2 | 23/30 | 600 | −0.01em |
| H3 | 19/26 | 600 | −0.005em |
| Body large | 16/26 | 400 | 0 |
| Body | 14/22 | 400 | 0 |
| Supporting | 13/18 | 400 | 0, text-secondary |
| Data label | 12/16 | 500 | +0.04em, UPPERCASE, text-secondary |
| Value XL | 32/36 | 600 | −0.02em, tabular-nums |
| Value M | 20/26 | 600 | −0.01em, tabular-nums |

Rules: 600 headings/values, 400 reading, 500 labels and buttons. Tighten tracking only above 19px. **All prices, balances, percentages and times use `font-variant-numeric: tabular-nums`.** Risk disclosures ≥ 13px and AA. Sentence case everywhere; uppercase only for 12px data labels.

### Spacing, radius, borders, elevation, motion
- Spacing: 8px base, 4px for details — 4 · 8 · 12 · 16 · 24 · 32 · 48 · 64 (`--space-1…16`).
- Radius: 6 tags/chips · 8 buttons/inputs (`--radius-md`) · 12 cards · 16 panels & widget (prototype uses `--radius-lg` 13.6 / `--radius-xl` 18.4 from BiatecDEX) · 999 badges/avatars.
- Borders: 1px default, 1px strong for inputs, 2px focus ring `--color-border-focus` offset 2px.
- Elevation: `--shadow-sm` cards · `--shadow-md` menus/toasts · `--shadow-lg` dialogs, widget, launcher.
- Motion: 150ms hover/press · 180–200ms expand/toast · 220ms panel/dialog. Easing `cubic-bezier(.2,0,0,1)`. Honor `prefers-reduced-motion`.

### Icons
Lucide geometry, 24px grid, 2px stroke (1.75 at 24px), round caps/joins, sizes 16 / 20 / 24 (32 for empty states only). Icons inherit text color; brand color only on the AI marker. Critical financial actions always pair icon + text. Sets used: AI assistant (message-square), Portfolio (pie-chart), Markets (bar-chart), Activity (history), Risk & permissions (shield), Send (arrow-up), Attach (paperclip), Copy, Retry (rotate-ccw), Trending up/down, Wallet, Order (file-text), Fee (percent), Shield-check, Lock, Key, Pause (circle-pause), Revoke (ban), Warning (triangle-alert), Confirmation (circle-check).

### Buttons
| Variant | Spec |
| --- | --- |
| Primary | h40, pad 0 16, radius 8, Inter 14/500, bg `brand.primary`, text `brand.on`; hover `brand.hover`; pressed `brand.pressed` |
| Secondary | transparent, 1px `border.strong`, text primary; hover bg `bg.interactive` |
| Ghost | transparent, no border, pad 0 12; hover bg `bg.interactive` |
| Danger (Revoke) | bg `status.danger-subtle`, text `status.danger`; hover border `status.danger` |
| Analyze (soft) | bg `brand.subtle`, text `brand.text` |
| Sizes | 32 (13px, pad 12) · 40 (14px, pad 16) · 48 (15px, pad 20, radius 10, touch) |
| Disabled | bg `bg.interactive`, text `text.secondary` — still legible |
| Loading | spinner + "Sending…", opacity .8, `aria-busy`, locked |
| Icon-only | 40px hit area, `aria-label`, tooltip on hover **and** focus |

**Approve is special:** the only 48px, double-ringed, icon-led (shield-check) primary, and it **always states the amount** — "Approve demo trade · $100.00". Navigation never uses this treatment. Labels are left-aligned when a button is wider than its label.

### Financial status conventions (fixed — never drop the label)
| Glyph | State | Tone | Border | Means |
| --- | --- | --- | --- | --- |
| ○ | Draft | neutral | dashed | Being shaped in chat; no order exists |
| ◷ | Awaiting approval | warning | dashed | Prepared order waiting on the user |
| ⛉ | Approved | brand | solid green | Authorized; queued |
| ◌ | Pending execution | info | solid green | Submitted; can still be cancelled |
| ✓ | Completed | success | solid green | Filled and recorded (demo fills labelled) |
| ✕ | Rejected | neutral | solid | User declined |
| ! | Failed | danger | solid red | Platform could not execute; nothing charged |
| ⊘ | Cancelled | neutral | solid | Stopped by user or voided by revoke |

Tone = badge bg `*-subtle` + text `*` (neutral: `bg.interactive` + `text.secondary`).

### Risk levels
Standard → neutral surface. Elevated → warning subtle (e.g. "uses 84% of today's allocation"). Consequential → danger subtle and **approval blocked** until edited (e.g. slippage above cap).

---

## Screens / views (widget prototype `design/Biatec AI v2.dc.html`)

### 0. Host page (illustrative, not part of the widget)
Biatec DEX-style page: nav (Pacifico "Biatec DEX", Trade/Liquidity/Portfolio/Developers, Connect wallet), BTC/USDC price 40px/600, chart, order card with "Ask Biatec AI about this order →" which opens the widget and sends "Prepare a hypothetical BTC trade using $100". A thin top banner states the prototype is simulated.

### 1. Launcher (floating mode, closed)
- Fixed `right:24px; bottom:24px`, 60×60, radius 8, bg brand.primary (hover brand.hover), `shadow-lg`, symbol 44px mono-dark.
- Unread badge: top/right −6px, 20px min, text-primary bg, 11px/600 "1".
- Tooltip "Ask Biatec AI" on hover/focus, left of button, 13px, inverted colors.

### 2. Minimized bar
Fixed bottom-right, `width:min(320px, 100vw−32px)`, pad 10/10/10/12, radius 8, 1px border, shadow-lg. Symbol 36 · "Biatec AI" 14/600 · status line 12px secondary ("Analyze only · 1 proposal pending") · secondary button "Restore".

### 3. Chat panel
Geometry by mode:
- **Floating:** fixed right/bottom 24, `width:min(420px,100vw−32px)`, `height:min(700px,100vh−48px)`, radius lg, 1px border, shadow-lg.
- **Inline:** `position:relative`, width 100% of a `minmax(360px,420px)` host column, same height, no shadow.
- **Workspace / full screen** (and mobile < 640px when open): fixed inset 0, no radius/border.

Structure top → bottom:
1. **Header** (pad 12/10/12/16, bottom border): symbol 42 · "Biatec AI" 16/600 · "Your crypto-trading assistant" 12 secondary · icon buttons 34×34: Expand/Exit full screen (maximize-2/minimize-2), Minimize (floating only), Close.
2. **Status bar** (pad 6/10/6/16, bottom border): status pill button (1px border, radius 8, 12px) = 8px square dot + "Status:" (secondary) + mode label (600) + chevron → opens Permissions. Right side: "Chats" toggle (hidden in workspace) and "Activity" toggle, 12px, `aria-pressed`.
   - Dot: Analyze = neutral-500 · Approval mode = text-primary · Rule-based = brand · Paused = hollow with brand edge.
3. **Paused banner** (when paused): warning-subtle bg, warning text 13px, pause icon, "Agent paused. Analysis still works — proposals and approvals are on hold." + ghost "Resume".
4. **Body**: optional history sidebar (260px in workspace, overlay sheet otherwise) · conversation column (max-width 760, centered, pad 20/16, gap 22) · optional Agent/Activity aside (300px in workspace, overlay otherwise). Toast: absolute top 12, inverted, 13px, check icon, auto-dismiss 2.8s, `aria-live=polite`.
5. **Composer** (top border, pad 10/12/8): box radius 8, 1px border, surface bg; textarea 15/1.45, 2 rows, min 52 max 140, placeholder "Ask Biatec AI anything about crypto…", focus ring 2px brand inset −1. Toolbar: Attach (disabled in demo, tooltip says so) · Context chip "Context BTC/USDC ▾" (cycles BTC/USDC → ETH/USDC → Portfolio → None) · Send 36×36 primary icon button (arrow-up), disabled when empty or thinking. Footer 11px secondary: "Demo · simulated data · not financial advice. Biatec AI can be wrong — check before acting."

### 4. Empty state
"Meet Biatec AI." 32/600 −0.025em · body 15/1.55 max 520 "Your intelligent companion for exploring crypto markets, understanding trading strategies, and staying in control of every decision." · 2×2 suggestion grid (2px gaps over divider, cells min-h 96, pad 14, brand-colored 18px icon top, 14/600 label): "What's happening with Bitcoin today?", "Analyze ETH market trends", "Help me understand trading risks", "Explore a trading strategy" · ghost link "Try the full journey: analyze Bitcoin and a possible trade →" · shield note (12px, top border) stating current mode.

### 5. Messages
- **User:** right-aligned bubble, max 85%, surface bg, pad 10/14, 14/1.5, radius 8, shadow-sm, `pre-wrap`.
- **AI:** grid `28px | 1fr`, gap 12; 28px symbol avatar. Optional title 16/600; paragraphs 14/1.6; then rich blocks; then follow-up chips (secondary, 13/600, pad 6/10, disabled while thinking); then action row (12/600 ghost, secondary color): Copy (→ "Copied" 1.5s), Retry, Expand explanation / Show less, Supporting info / Hide info.
- Bullets: 6px brand square marker, 14/1.55, optional 11px uppercase title ("Key risk factors").
- Expanded "Why it matters": 14/1.6, left 2px border, pad-left 12.
- Supporting info: 12/1.5, surface bg, pad 8/10, "**Supporting information.** …" (data provenance).
- **Thinking:** avatar + three 6px squares pulsing (`bai-pulse` 1.1s, staggered 0/.15/.3s, opacity .2→1, −3px) + "Analyzing…" 12px. `role=status`.

### 6. Rich blocks (inside AI messages; all 1px border, radius 8, overflow hidden, section rules 1px)
- **MarketInsightCard:** kicker "Market snapshot · demo data" (brand text) + pair · price 26/600 tnum · change 13/600 in success/danger with ▲/▼ · "Up · 24h" · 96px area+line sparkline (area surface, line text-primary 2px) · 3-col stats 24h high / 24h low / Volume (label 11 secondary, value 13/600) · stamp "Demo data · snapshot 19 Oct 2026, 14:32 UTC · not live".
- **Strategy (DCA) card:** kicker "Illustration · $25 every week · demo prices", 6 bars (110px area; tallest = brand, others text-primary) with sats above and W1–W6 + price below, average-cost note, Advantages / Limitations two columns.
- **Portfolio card:** kicker "Mock portfolio · not your holdings" + total $11,700.00, 14px stacked allocation bar (BTC brand 46%, ETH text 22%, USDC neutral-500 18%, ALGO neutral-300 14%), rows (swatch · symbol · value · %), warning strip "Concentration risk: high. BTC is 46%…".
- **TradeProposalCard:** 1.5px **dashed** `border.strong` while proposed, **solid** after execution (header bg success-subtle). Header: "TRADE PROPOSAL · DEMO" 10px uppercase + status badge 11/600 uppercase bordered ("Proposed · not executed" / "✓ Demo executed" / "Cancelled" / "Voided"). Title "Buy BTC · $100.00" 20/600 + "≈ 0.001479 BTC". 2×2 grid Pair / Direction·type / Est. execution price / Est. fees (0.30%). Expandable "+ Show order details" adds Max slippage 0.50% / Worst-case price / Routing / Quote valid. **Downside scenario** row (warning icon, surface bg): "If BTC falls 10% after purchase, this position would be worth about $X, a loss of about $Y including fees. Larger falls are possible." **Authorization** row (shield icon) — copy varies by mode/state. Edit mode: amount input (min 10, step 5) + chips $25/$50/$100/$250 (selected = inverted) + inline alert ("Minimum demo order is $10." / "Above your $250 max order. Approval required."). Actions: Primary "Approve demo trade" (or "Enable approvals" in Analyze only; disabled if paused or < $10) · "Review risks" · "Edit trade"/"Done editing" · ghost "Cancel".
- **Confirmation card:** success-subtle header "✓ Demo order filled · SIMULATED", 2×2 Filled / Avg. price / Total incl. fee / Receipt (DEMO-XXXXXX), footer "Approved by you at HH:MM · logged to Activity · no wallet signature requested".

### 7. History sidebar ("Chats")
"+ New chat" secondary (left-aligned) · "Recent" h6 · rows (13px title, 600 when current + surface bg; 11px time) · footer "Demo session · conversations stay in this browser tab".

### 8. Agent & Activity aside
"Agent" header + Hide/Back to chat · status dot + mode 18/600 · "Agent active · {mode description}" · in Rule-based: 2-col limits grid (Max order, Daily allocation, Stop at loss, Expires, Confirm above, Assets) · "Manage permissions" + "Pause agent"/"Resume agent". "Activity" list, newest first: 10px square marker (Analysis neutral, Proposal hollow, Demo execution brand, Permissions text), title 13/600, time 11, detail 12/1.45, kind 10px uppercase. Target pattern (DS): group by day, filter chips All / Analysis / Proposals / Executions / Permissions, "by" attribution (You / Biatec AI).

### 9. Permissions dialog (`role=dialog`, width min(540,100%))
Kicker "Biatec AI · Agent permissions" · title "What may Biatec AI do?" · "Change or revoke this at any time. Increasing autonomy always asks you to confirm." · pause row · radiogroup of 3 modes (2px border, brand when selected, number 20/600):
- **01 Analyze only** — Answers questions, analyzes markets and explains strategies. Never prepares or executes trades.
- **02 Prepare for approval** — Drafts trade proposals in chat. Every trade needs your explicit approval before it runs.
- **03 Rule-based automation** — May execute eligible trades only inside the limits you set. Everything else still asks you.

Rule-based reveals **Automation limits**: Max order (USD, default 250), Daily allocation (500), Stop at loss % (5, 1–50), Always confirm above (100), Permission expires (24 hours / 7 days / 30 days), Allowed assets chips (BTC, ETH, ALGO, USDC; vs USDC) + warning "Limits are safeguards, not guarantees against loss…". Footer: danger ghost "Revoke all access" · "Cancel" · primary "Save changes" or "Continue" (when raising autonomy).

**Confirm step** (raising only): "Give Biatec AI more autonomy?" · "You're moving from X to Y. Biatec AI will be able to:" · checklist of new powers in plain language · required checkbox "I understand I can pause or revoke this at any time, and that limits don't prevent losses." · Back / "Confirm increase" (disabled until ticked).

### 10. Review trade dialog (width min(500,100%))
Kicker "Proposed · not executed" · "Review demo trade" · table Order / Amount / Est. quantity / Est. price · worst case / Est. fees · Risks list (warning icons) · "What happens next" note · required checkbox "I've reviewed the risks and understand this is a demo trade." · "Back to chat" / primary "Approve demo trade" (disabled until ticked, or "Needs approval mode" / "Agent paused").
DS target: every consequential action answers 5 questions before enabling — What will happen · Which permissions are involved · Order details · Financial implications · How to stop it.

### 11. Developers / embed spec dialog
Shows `<biatec-ai-widget mode="floating|inline|workspace" theme="/partner-theme.css" context="market,portfolio" features="proposals,automation">`, the component tree, and isolation rules (see below).

---

## Interactions & behaviour
- Launcher click → open panel, clear unread, focus composer. Minimize → bar; Restore → panel. Close → launcher.
- Expand → workspace (full screen, sidebar 260 + aside 300 visible side by side). Below 640px the open floating panel is always full screen; sidebars become overlay sheets with "Back to chat".
- Enter sends, Shift+Enter newline, ignore during IME composition. Auto-scroll to newest on new message/thinking.
- Esc closes in order: review dialog → permissions → spec → sheet. Dialogs trap focus, focus their container on open, return focus to composer on close. Backdrop click closes.
- **Lifecycle:** Analyze only → trade request yields "Proposals are switched off" + follow-up "Allow trade proposals" (opens permissions preset to Prepare; after save, the pending trade is drafted automatically). Prepare → proposal card. Approve → Review dialog → tick → confirm → proposal turns solid/executed, toast, Activity log, confirmation message (~700ms).
- Lowering autonomy is instant; raising always requires the confirm step. Revoke → mode Analyze, unpause, void all pending proposals (status Voided), log + toast.
- Pause → banner, approvals disabled, new trade requests answered with "The agent is paused" + "Resume agent".
- Every consequential event is logged to Activity: Session started, Market analysis, Proposal drafted/cancelled, Demo trade executed, Autonomy increased, Permissions updated, Agent paused/resumed, Access revoked.
- Approve buttons lock while submitting (prevent double orders). Every action gives loading/success/failure feedback.
- AI replies, toasts and status changes are announced via live regions.

## State (per widget instance)
```
ui:      open, minimized, workspace, sheet: null|'history'|'activity', ctxOpen, viewportWidth, unread, toast
chat:    conversations[{id,title,when,messages[]}], activeId, draft, thinking, contextIdx
message: {id, role:'user'|'ai', kind:'text'|'market'|'strategy'|'portfolio'|'proposal'|'confirm',
          title, paras[], bullets[], bulletsTitle, more, sources, followups[{label, action?}], expanded, showSrc}
proposal:{amount, status:'proposed'|'executed'|'cancelled'|'revoked', editing, open, receipt}
perms:   {mode:'analyze'|'prepare'|'rules', maxOrder, daily, maxLoss, confirmAbove, expiry, assets{BTC,ETH,ALGO,USDC}}
agent:   paused; permOpen, permStep:'edit'|'confirm', permDraft, permAck, pendingTrade
review:  reviewId, reviewAck
activity:[{id, t, title, detail, kind:'analysis'|'proposal'|'executed'|'permission'}]
```
Real services needed: market data (price, 24h hi/lo/volume, series), LLM responses (structured blocks above), portfolio, quotes (price, fee, slippage, worst case), execution via the **host's** wallet signing flow, server-side permission enforcement, activity persistence.

## Embedding & theming
- Shadow DOM isolation. Hosts may override **CSS custom properties only**: `--color-*`, `--font-*`, `--radius-*`, `--space-*`, `--shadow-*`. Theme attribute `data-theme="dark|light"` on the widget root. The prototype's "partner" theme (Tweaks → Host theme) demonstrates a foreign host palette (blue accent `#4c8dff`, radius 4/6, system font).
- Signing stays in the host's wallet flow. The widget never receives keys, seed phrases or API secrets.
- Component tree: `BiatecAIWidget › ChatLauncher, ChatPanel › ChatHeader · AutonomySelector, ChatMessages → AIResponse (MarketInsightCard, TradeProposalCard, RiskSummary), ChatComposer, PermissionDialog, ActivityTimeline`.

## Accessibility (non-negotiable)
Text ≥ 4.5:1, large text & UI boundaries ≥ 3:1. Visible 2px focus ring everywhere, logical tab order. 44×44 touch targets on mobile, 40px min desktop. `aria-pressed`/`aria-expanded`/`aria-checked` on toggles, `role=radiogroup` for modes, `aria-modal` dialogs. No signal by color alone. Honor reduced motion.

## Copy voice
Concrete, neutral, calm. Name the action and the amount. State downside in dollars ("A 10% fall would cost about $10"). Never promise returns, no hype, no urgency, no emoji. Label demo/simulated at the point of action.

## Assets
- `assets/bai-symbol.svg`, `assets/bai-symbol-small.svg` — Biatec AI chip mark (from `design/BAI Logo.dc.html`).
- `assets/biatec-logo.svg`, `assets/favicon.svg` — official Biatec DEX marks (from `scholtz/BiatecDEX/public`).
- Fonts: Inter 400/500/600/700(/800 for the logo "AI"), Pacifico 400 — Google Fonts (imported in `biatec-tokens.css`).
- Icons: Lucide (lucide.dev) — use the official package, not hand-copied paths.

## Files
- `CLAUDE.md` — short rule sheet for coding agents (put it in the target repo).
- `tokens/biatec-tokens.css`, `tokens/tokens.json` — design tokens.
- `design/Biatec AI v2.dc.html` — full interactive widget prototype (all logic in the bottom `<script>`; `respond()` maps prompts to the demo replies).
- `design/Biatec AI Design System.dc.html` — DS index; `design/BAI DS Brand|Foundations|Components|Patterns|Guidelines.dc.html` — the visual system pages, each in dark and light.
- `design/BAI Logo.dc.html` — parametric logo (variant symbol/horizontal/compact, size, mono).
- `design/support.js`, `design/_ds/…` — runtime needed only to open the prototypes; not for production.
