---
id: T-067
title: Holdings data card from wallet.balance
status: backlog
priority: P3
epic: "Widget UI rework"
blocked_by: [T-020, T-064]
area: ["packages/chat-widget/src/components/data/**", "backend/Biatec.Chat.Api/Chat/DataCards/**"]
parallel: no
conflicts_with: [T-035, T-042, T-074, T-076, T-082]
issue: "#108"
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-19
updated: 2026-10-20
---

# T-067 · Holdings data card from wallet.balance

## Context

Read `docs/design/README.md` (mapping and status table), `docs/design/biatec-ai-widget/README.md` (pixel spec), `packages/chat-widget/AGENTS.md` (UI rules); prototype screen 6, Portfolio card.

Spec, User Experience 10: "**Data cards**: balances from `wallet.balance` render as a holdings card (total, allocation bar, one row per asset, a concentration note when one asset dominates)."

Today balances come back only as model prose. The card needs structured data, so the backend emits a new SSE event next to the text when `wallet.balance` returns.

## Goal

Asking for balances shows a structured, accurate holdings card instead of numbers only in prose.

## Scope

- In: new chat event `data_card` (`{ kind: 'holdings', network, addresses, assets: [{ assetId, unitName, amount (decimal string), decimals, fiatValue? }], fiatCurrency?, asOf }`) emitted by the orchestrator from the `wallet.balance` tool result (not from model text); OpenAPI update and regenerated `packages/chat-protocol`.
- In: widget `HoldingsCard`: total (when fiat values exist), 14 px stacked allocation bar (brand for the largest, neutral tones for the rest, never red/green), rows (swatch · unit name · amount · share %), concentration note when one asset is above 40 percent, timestamp "Balances as of HH:MM UTC · network".
- Out: market snapshot (T-069); portfolio advice.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-035, T-042, T-074, T-076, T-082: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Orchestrator test: a `wallet.balance` tool result produces exactly one `data_card` event with amounts as decimal strings
- [ ] Anonymous sessions never receive a `data_card` of kind holdings
- [ ] Widget test renders the card from a fixture with tabular numerals, correct shares summing to 100 percent, and the concentration note at the threshold
- [ ] Without fiat values the card shows amounts only and no total

## Implementation notes

- Amounts above 2^53 must stay strings end to end.
- The SSE union in `chat-protocol` has an exhaustiveness check; update it.

## Verification

dotnet test backend --filter DataCard; pnpm --filter @biatec/chat-widget test

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
- 2026-10-19 22:40 · coordinator · blocked · conflicts_with T-065: both regenerate packages/chat-protocol
- 2026-10-20 01:09 · LukassSoukup · blocked · parallel analysis: dropped T-065 (done); added T-042 (cards in ChatView.vue/useChat.ts) and T-035 (orchestrator emits data_card)
- 2026-10-20 02:30 · coordinator · blocked · added T-074 to conflicts_with (new task)
- 2026-10-20 02:18 · kanban-board · blocked → backlog · blockers done: T-020, T-064
