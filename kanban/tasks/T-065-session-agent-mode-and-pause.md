---
id: T-065
title: Session agent mode (analyze / prepare) and pause, enforced server-side
status: in-progress
priority: P2
epic: "Widget UI rework"
blocked_by: [T-022]
area: ["backend/Biatec.Chat.Api/Agent/**", "backend/Biatec.Chat.Tests/Agent/**"]
parallel: no
conflicts_with: [T-067, T-069]
issue: "#106"
branch: "task/T-065"
owner: "session-20261019-agent"
taken_by: "scholtz"
source: "PRODUCT-DESCRIPTION.md#tier-enforcement"
created: 2026-10-19
updated: 2026-10-19
---

# T-065 · Session agent mode (analyze / prepare) and pause, enforced server-side

## Context

The Biatec AI design gives users an autonomy control ("What may Biatec AI do?"). The spec maps it onto the backend:

Tier Enforcement 5: "An Authenticated session also carries the user's agent mode (`analyze` or `prepare`) and a `paused` flag, set only by the user through the widget's permissions dialog (a CSRF-protected session endpoint, never a model tool). In `analyze` or while paused, fund-moving capabilities are left out of the tool list sent to the model, the server-side gate refuses them, and confirming an open preview is refused; reads, explanations and balances keep working. Every change is an evidence entry. The anonymous tier is always `analyze`."

Data Model: `ChatSession` gains `agentMode` (analyze or prepare, default prepare once signed in) and `paused`; "Agent mode and pause are per session and reset at the next sign-in".

See `docs/design/README.md` for the full mapping (Rule-based automation is not built).

## Goal

The user's agent mode and pause flag are stored on the session, exposed to the widget and enforced by the capability gate and the confirm endpoint.

## Scope

- In: `agentMode` and `paused` on `ChatSession` (EF migration); `GET /session` includes them; `PUT /session/agent` (`{ mode, paused }`, CSRF header + origin + client key like other state-changing routes) for Authenticated sessions only; Anonymous gets 403.
- In: capability gate: when `analyze` or paused, capabilities with `movesFunds` or that build a signable group (including `safe.create`, `safe.propose`, `limits.update` raising) are removed from the tool list and refused if called; reads stay.
- In: confirm and elevate endpoints refuse while `analyze` or paused with a typed error code the widget can show ("agent_paused", "agent_analyze_only").
- In: evidence entry `agent_mode_changed` with old and new values; the change is a user action, never a model tool.
- In: OpenAPI update and regenerated `packages/chat-protocol`.
- Out: the widget dialog (T-066); rule-based automation; persisting the mode across sign-ins.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-067, T-069: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Policy tests: in `analyze` and while paused the Bedrock request carries no fund-moving tool, and a forged tool call is refused server-side
- [ ] Confirming an existing preview after switching to `analyze` or pausing returns the typed error; resuming allows it again within the preview's validity
- [ ] `PUT /session/agent` without the CSRF header, from an unregistered origin, or on an anonymous session is refused
- [ ] Each change appends one evidence entry; the chain verifies
- [ ] `GET /session` returns `agentMode` and `paused`; `packages/chat-protocol` is regenerated and the widget typechecks

## Implementation notes

- Reuse the tier gate from T-013 rather than adding a second policy path: the effective allowed set is tier ∩ scopes ∩ agent mode.
- Lowering (prepare → analyze, pause) must also cancel nothing by itself; the widget's "Revoke all access" cancels previews explicitly.
- Registering the endpoint in `Program.cs` and the migration are append-only shared edits.

## Verification

dotnet test backend --filter Agent; pnpm --filter @biatec/chat-protocol generate (or the repo's regen script) and pnpm --filter @biatec/chat-widget typecheck

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
- 2026-10-19 22:40 · coordinator · blocked · conflicts_with T-067, T-069: all three regenerate packages/chat-protocol
- 2026-10-19 22:29 · kanban-board · blocked → backlog · blockers done: T-022
- 2026-10-19 22:32 · scholtz · taken · status stays backlog
- 2026-10-19 22:32 · session-20261019-agent · backlog → in-progress · claimed via /kanban-parallel + /kanban-take loop
