import { computed, reactive } from "vue";
import type { ActionPreview, ActionPreviewStatus, ChatEvent, ConsentState } from "@biatec/chat-protocol";

/** Filter groups of the timeline (User Experience 9). */
export type ActivityKind = "analysis" | "preview" | "execution" | "permission";
export type ActivityFilter = "all" | ActivityKind;
export const ACTIVITY_FILTERS: ActivityFilter[] = ["all", "analysis", "preview", "execution", "permission"];

export interface ActivityEntry {
  id: number;
  /** Epoch ms. */
  at: number;
  kind: ActivityKind;
  /** i18n key under `activity.event`, with `params`. */
  title: string;
  params?: Record<string, string>;
  /** Untrusted text (model output, intents) rendered as plain text only. */
  detail?: string;
  by: "you" | "ai";
}

export interface ActivityDay {
  /** Local date, YYYY-MM-DD. */
  day: string;
  entries: ActivityEntry[];
}

/** An open preview the user can still act on: what Revoke all access cancels. */
export interface OpenPreview {
  previewId: string;
  groupHash: string;
}

const OPEN: ActionPreviewStatus[] = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval"];
const PREVIEW_EVENT: Partial<Record<ActionPreviewStatus, { kind: ActivityKind; title: string }>> = {
  Submitted: { kind: "execution", title: "submitted" },
  Completed: { kind: "execution", title: "receipt" },
  Failed: { kind: "execution", title: "failed" },
  Cancelled: { kind: "preview", title: "previewCancelled" },
  Expired: { kind: "preview", title: "previewExpired" },
};
/** Past these the user has confirmed the preview, even when the confirm call itself was not seen here. */
const CONFIRMED: ActionPreviewStatus[] = ["Signing", "Submitted", "Completed"];
const CONSENT_EVENT: Partial<Record<ConsentState, string>> = {
  Approved: "consentApproved",
  Denied: "consentDenied",
  TimedOut: "consentTimedOut",
};

const DETAIL_MAX = 120;
function clip(s: string): string {
  const t = s.trim().replace(/\s+/g, " ");
  return t.length > DETAIL_MAX ? `${t.slice(0, DETAIL_MAX - 1)}…` : t;
}

export function localDay(ms: number): string {
  const d = new Date(ms);
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** The step a decision applies to: the first one still waiting for the user, else the first step. */
function decisionHash(p: ActionPreview): string {
  return (p.steps.find((s) => OPEN.includes(s.status)) ?? p.steps[0])?.groupHash ?? "";
}

/**
 * The session's activity, per page load: built from the chat stream's events and the user's own actions in the
 * widget. The signed evidence export stays the durable record.
 */
export function useActivity(now: () => number = Date.now) {
  const entries = reactive<ActivityEntry[]>([]);
  const previews = reactive(new Map<string, { status: ActionPreviewStatus; intent: string; groupHash: string }>());
  const consent = new Map<string, ConsentState>();
  const confirmed = new Set<string>();
  let nextId = 1;

  function log(e: Omit<ActivityEntry, "id" | "at">): void {
    entries.push({ ...e, id: nextId++, at: now() });
  }

  /** One chat-stream event. Only changes are logged: a re-sent card with the same status adds nothing. */
  function record(e: ChatEvent): void {
    if (e.type === "preview_card") {
      const p = e.preview;
      const before = previews.get(p.previewId);
      previews.set(p.previewId, { status: p.status, intent: p.intent, groupHash: decisionHash(p) });
      if (before?.status === p.status) return;
      const detail = clip(p.intent);
      if (!before && OPEN.includes(p.status)) log({ kind: "preview", title: "previewPrepared", detail, by: "ai" });
      if (before && CONFIRMED.includes(p.status)) recordConfirm(p.previewId);
      const ev = PREVIEW_EVENT[p.status];
      if (ev) log({ ...ev, detail, by: "ai" });
    } else if (e.type === "consent_status") {
      const key = `${e.consent.previewId}:${e.consent.stepId}`;
      if (consent.get(key) === e.consent.state) return;
      consent.set(key, e.consent.state);
      const title = CONSENT_EVENT[e.consent.state];
      const intent = previews.get(e.consent.previewId)?.intent;
      if (title) log({ kind: "execution", title, detail: intent ? clip(intent) : undefined, by: "you" });
    }
  }

  /** A finished assistant reply to the user's message. */
  function recordReply(question: string): void {
    log({ kind: "analysis", title: "analysis", detail: clip(question), by: "ai" });
  }

  /** The user confirmed a preview (the confirm call was accepted). Logged once per preview. */
  function recordConfirm(previewId: string): void {
    if (confirmed.has(previewId)) return;
    confirmed.add(previewId);
    const intent = previews.get(previewId)?.intent;
    log({ kind: "execution", title: "confirmed", detail: intent ? clip(intent) : undefined, by: "you" });
  }

  const openPreviews = computed<OpenPreview[]>(() =>
    [...previews.entries()]
      .filter(([, v]) => OPEN.includes(v.status) && v.groupHash)
      .map(([previewId, v]) => ({ previewId, groupHash: v.groupHash })),
  );

  /** Mark a preview cancelled locally (after the cancel call), so it is not offered again. */
  function markCancelled(previewId: string): void {
    const p = previews.get(previewId);
    if (!p || p.status === "Cancelled") return;
    p.status = "Cancelled";
    log({ kind: "preview", title: "previewCancelled", detail: clip(p.intent), by: "you" });
  }

  /** Forget the conversation's activity (sign-out, erasure): entries, previews, consent and confirmations. */
  function reset(): void {
    entries.splice(0, entries.length);
    previews.clear();
    consent.clear();
    confirmed.clear();
  }

  return { entries, log, record, recordReply, recordConfirm, openPreviews, markCancelled, reset };
}

export type Activity = ReturnType<typeof useActivity>;

/** Newest first, grouped by local day. */
export function groupByDay(list: readonly ActivityEntry[], filter: ActivityFilter): ActivityDay[] {
  const days: ActivityDay[] = [];
  const sorted = list.filter((e) => filter === "all" || e.kind === filter).sort((a, b) => b.at - a.at || b.id - a.id);
  for (const e of sorted) {
    const day = localDay(e.at);
    const last = days[days.length - 1];
    if (last?.day === day) last.entries.push(e);
    else days.push({ day, entries: [e] });
  }
  return days;
}
