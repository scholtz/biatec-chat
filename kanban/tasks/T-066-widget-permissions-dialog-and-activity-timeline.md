---
id: T-066
title: Widget permissions dialog, pause/revoke and activity timeline
status: blocked
priority: P2
epic: "Widget UI rework"
blocked_by: [T-063, T-065]
area: ["packages/chat-widget/src/components/agent/**", "packages/chat-widget/src/components/activity/**"]
parallel: no
conflicts_with: [T-036]
issue: ""
branch: ""
owner: ""
taken_by: ""
source: "PRODUCT-DESCRIPTION.md#user-experience"
created: 2026-10-19
updated: 2026-10-19
---

# T-066 · Widget permissions dialog, pause/revoke and activity timeline

## Context

Read `docs/design/README.md` (mapping and status table), `docs/design/biatec-ai-widget/README.md` (pixel spec), `packages/chat-widget/AGENTS.md` (UI rules); prototype screens 8 (Agent & Activity aside) and 9 (Permissions dialog).

Spec, User Experience 8: "the status pill opens a dialog "What may Biatec AI do?" with the agent modes below, the user's Biatec OIDC spending limits (read with `limits.view`; lowered with `limits.update` where the tenant has `manage-limits`), Pause, and "Revoke all access". Lowering the mode, pausing and revoking take effect immediately; raising the mode or resuming needs a second confirmation step with an explicit "I understand" checkbox." Modes: Analyze only, Prepare for approval (default once signed in), Rule-based automation shown as not available yet. "**Revoke all access** cancels every open preview and signs out."

User Experience 9: "**Activity timeline**: newest first, grouped by day, filterable (All, Analysis, Previews, Executions, Permissions), listing this session's analyses, previews, confirmations, consent results, receipts and mode changes with who did each ("You" or "Biatec AI"). It is built from the session's chat events; the signed evidence export remains the durable record."

## Goal

Users can see and change what Biatec AI may do, pause or revoke it in one step, and review what happened in this session.

## Scope

- In: permissions dialog (radiogroup of three modes, number 01–03, Rule-based disabled with "Not available yet"; Pause row; footer "Revoke all access" (danger ghost), Cancel, Save changes / Continue); confirm step when raising (plain-language list of new powers, required checkbox, Back / Confirm increase); calls `PUT /session/agent` from T-065.
- In: spending-limits section: a secondary button "Show my spending limits" that sends "What are my current spending limits?" through the chat (uses the `limits.view` capability; hidden for Guest), and the note "Limits are safeguards, not guarantees against loss."
- In: Revoke all access: confirm dialog, then cancel every open preview (existing decision endpoint), then the existing sign-out flow; toast and activity entry.
- In: paused banner under the status bar (warning-subtle, pause icon, "Biatec AI is paused. Answers still work; previews and confirmations are on hold." + Resume, which goes through the confirm step).
- In: Activity aside (300 px in workspace, overlay sheet otherwise, "Back to chat"): entries derived from SSE events and local actions, day groups, filter chips, "by You / Biatec AI", 10 px square kind marker.
- In: Guest: dialog shows Analyze only selected and a sign-in call to action instead of the other modes.
- Out: backend enforcement (T-065); a persisted activity API; rule-based automation.

## Parallel work

Parallel work possible: **no**. Don't run it at the same time as T-036: they change the same files, and running them together causes merge conflicts or broken behaviour.

## Acceptance criteria

- [ ] Lowering prepare → analyze and Pause call the API immediately; raising and Resume show the confirm step and are disabled until the checkbox is ticked
- [ ] The status pill dot and label reflect the server state after each change, and after a reload (from `GET /session`)
- [ ] Revoke all access cancels open previews (decision calls observed in the test) before sign-out, and the header returns to Guest
- [ ] Typed errors `agent_paused` / `agent_analyze_only` from confirm show a message with a Resume / Change mode action
- [ ] Activity lists a preview, a confirmation, a receipt and a mode change from a fixture event stream, newest first, filterable
- [ ] Dialog: `role=dialog`, `aria-modal`, focus trap, Esc closes, focus returns to the status pill

## Implementation notes

- Esc order: confirm step → dialog → sheet.
- Activity is per page load; say so in the aside footer ("This session. Download evidence for the full signed record.").

## Verification

pnpm --filter @biatec/chat-widget test; manual playground run against the local backend: change modes, pause during an open preview, revoke

## Hand-off

Filled in by the agent when it moves the task to `review`.

## Review

## Activity log

- 2026-10-19 22:40 · coordinator · (new) → blocked · created from the Biatec AI design handoff (docs/design/) for the widget UI rework
- 2026-10-19 22:40 · coordinator · blocked · conflicts_with T-036: the evidence download link is wired into the activity aside
