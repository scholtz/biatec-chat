import { reactive, ref, computed, onBeforeUnmount } from "vue";
import type { ActionPreview, ChatEvent, ConsentStatus, DataCardEvent, SafeCard, SessionState, SessionTier, Suggestion } from "@biatec/chat-protocol";
import { t } from "../../i18n";
import { SESSION_EXPIRED, TransportError, type TransportManager } from "../../transport";
import type { AuthClient } from "./auth";
import type { ConsentApi } from "./consent";
import { PENDING_STATUSES, decisionHash, type ConfirmResult, type PreviewApi, type PreviewEntry } from "./previews";

export interface ToolLine {
  id: string;
  label: string;
  state: string;
}
export interface SignInCard {
  reason: "SignIn" | "ElevateScope";
  scope: string | null;
  description: string | null;
  /** Sign-in cannot run on this host; `url` is then the tenant's own sign-in page. */
  unavailable: boolean;
  url: string | null;
}
export interface ChatMessage {
  id: number;
  role: "user" | "assistant";
  text: string;
  tools: ToolLine[];
  signIn?: SignInCard;
  /** Safe cards from safe.list / safe.create results (safe_card events), in arrival order. */
  safeCards?: SafeCard[];
  /** Holdings cards from wallet.balance results (data_card events), in arrival order. */
  dataCards?: DataCardEvent[];
  /** Preview cards (latest per previewId) from preview_card events, with consent state. */
  previews?: PreviewEntry[];
  /** Follow-up questions the backend suggested for this reply, if any. */
  followUps?: Suggestion[];
  error?: string;
  streaming: boolean;
}

export type SignInPhase = "idle" | "opening" | "waiting" | "blocked" | "failed" | "no-cookies" | "signout-failed";

export interface ChatServices {
  transport: Pick<TransportManager, "open" | "retryOnSignIn" | "send" | "dispose">;
  auth: Pick<AuthClient, "session" | "startLogin" | "startElevate" | "status" | "logoutUrl">;
  /** Poll interval and overall timeout of the sign-in popup (ms). */
  pollMs?: number;
  timeoutMs?: number;
  /** Test seam; defaults to window.open. */
  openPopup?: () => Window | null;
  /** Confirm call for preview cards (T-076). Without it a card cannot be confirmed. */
  previews?: PreviewApi;
  /** POST /chat/previews/{id}/cancel; true when the backend accepted the cancel. */
  cancelPreview?: (previewId: string, groupHash: string) => Promise<boolean>;
  /** Keeps the session alive while a preview, consent or signing is pending. */
  keepalive?: { hold(reason: string): void; release(reason: string): void };
  /** A confirm was accepted (Activity logs it). */
  onConfirmed?: (previewId: string) => void;
  /** A cancel was accepted. */
  onCancelled?: (previewId: string) => void;
  /** Every confirm response body, so typed agent refusals can be shown (core.agent.reportRefusal). */
  onConfirmResponse?: (body: unknown) => void;
  /** The visible conversation was reset (sign-out, erase): clear what was derived from it (Activity, refusals). */
  onReset?: () => void;
  /** Phone approval: follow a pending consent to its end and show the prediction. Without it a card is not followed. */
  consent?: ConsentApi;
  /** An event the widget itself derived (a consent result, a preview that settled), so Activity logs it like a stream event. */
  onEvent?: (e: ChatEvent) => void;
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

/** Resolves after `ms`, or as soon as `signal` aborts. */
function sleepUnlessAborted(ms: number, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve) => {
    if (signal.aborted) return resolve();
    const done = () => {
      clearTimeout(id);
      signal.removeEventListener("abort", done);
      resolve();
    };
    const id = setTimeout(done, ms);
    signal.addEventListener("abort", done, { once: true });
  });
}

// Following a phone approval (T-237).
/** The stream is retried this often while the single-poll fallback is running. */
const STREAM_RETRY_MS = 60_000;
/** Delays of the single-poll fallback; the last value repeats. Each gets up to 20 percent jitter. */
const POLL_BACKOFF_MS = [2_000, 4_000, 8_000, 15_000];
/** A consent is given up this long after its own expiry, so the backend's last answer can still arrive. */
const CONSENT_GRACE_MS = 10_000;
/** A tab hidden this long stops the fallback polling until it is visible again. */
const HIDDEN_PAUSE_MS = 30_000;
/** A consent may be followed by new rounds (an approval that could not be used); never more than this many after the first. */
const MAX_CONSENT_ROUNDS = 3;

/** Machine-readable reasons the backend may send, and the text each one gets. */
const CODE_TEXT: Record<string, string> = {
  no_active_device: "consent.noDevice",
  no_device_daily_cap: "consent.noDeviceCap",
  code_failed: "consent.codeFailed",
  preview_expired: "consent.previewExpired",
  resume_failed: "consent.resumeFailed",
  receipt_missing: "consent.receiptMissing",
};
const codeText = (code: string | null | undefined): string | null => (code && Object.prototype.hasOwnProperty.call(CODE_TEXT, code) ? t(CODE_TEXT[code]!) : null);
/** Confirm outcomes that say nothing is wrong with the action itself: the card stays confirmable. */
const RECOVERABLE = ["sign_in_required", "sign_in_unavailable", "agent_paused", "agent_analyze_only"];
const jitter = (ms: number): number => ms + Math.random() * ms * 0.2;
/** The binding code is shown only while the consent is pending; it is dropped from memory the moment it ends. */
const noCodeUnlessPending = (c: ConsentStatus): ConsentStatus => (c.state === "Pending" ? c : { ...c, bindingCode: null });

/** Longest assistant reply the widget keeps (characters). A real reply is a few thousand. */
export const MAX_REPLY_CHARS = 200_000;
const MAX_ORPHAN_CONSENTS = 50;

export function useChat(svc: ChatServices) {
  const messages = reactive<ChatMessage[]>([]);
  const session = ref<SessionState | null>(null);
  const busy = ref(false);
  const phase = ref<SignInPhase>("idle");
  /** Text for the polite live region: set once per finished assistant reply, never per streamed token. */
  const announcement = ref("");
  let nextId = 1;
  let disposed = false;
  let abort: AbortController | null = null;
  /** Bumped by resetConversation, so a reply still finishing from the old conversation is not announced. */
  let generation = 0;

  /**
   * A live region only speaks when its text changes, so clear it first and set the text on the next tick: two
   * identical replies or the same error twice are then announced again.
   */
  function announce(text: string): void {
    announcement.value = "";
    if (!text) return;
    void Promise.resolve().then(() => {
      if (!disposed) announcement.value = text;
    });
  }

  const tier = computed<SessionTier>(() => session.value?.tier ?? "Anonymous");

  async function refreshSession(): Promise<void> {
    session.value = await svc.auth.session();
  }

  /** Call on first panel open. */
  async function start(): Promise<void> {
    const result = await svc.transport.open();
    if (result === "success") await refreshSession();
  }

  /** `includeContext: false` sends this one message without the host page context. */
  async function send(text: string, opts: { includeContext?: boolean } = {}): Promise<void> {
    const message = text.trim();
    if (!message || busy.value) return;
    messages.push({ id: nextId++, role: "user", text: message, tools: [], streaming: false });
    messages.push({ id: nextId++, role: "assistant", text: "", tools: [], streaming: true });
    // Index into the reactive array so mutations below are tracked.
    const reply = messages[messages.length - 1]!;
    busy.value = true;
    const ctl = (abort = new AbortController());
    const gen = generation;
    const wasSignedIn = tier.value === "Authenticated";
    const stream = () => svc.transport.send({ message, signal: ctl.signal, ...(opts.includeContext === false ? { context: null } : {}) });
    try {
      try {
        for await (const e of stream()) apply(reply, e);
      } catch (err) {
        if (!(err instanceof TransportError && err.code === SESSION_EXPIRED) || ctl.signal.aborted) throw err;
        // The session ended (idle timeout or absolute lifetime) and the backend already set a fresh anonymous one.
        await refreshSession().catch(() => {});
        if (wasSignedIn) {
          reply.text = t("chat.sessionExpired");
          reply.signIn = { reason: "SignIn", scope: null, description: null, unavailable: false, url: null };
        } else for await (const e of stream()) apply(reply, e);
      }
    } catch {
      if (!ctl.signal.aborted) reply.error = t("chat.errorGeneric");
    } finally {
      reply.streaming = false;
      busy.value = false;
      if (abort === ctl) abort = null;
      if (gen === generation) announce(reply.error ?? (reply.text || reply.signIn?.description || safeSummary(reply) || dataSummary(reply) || previewSummary(reply)));

    }
  }

  /** A reply that is only a safe card still gets announced: a short summary of what the card shows. */
  function safeSummary(m: ChatMessage): string {
    const safes = (m.safeCards ?? []).flatMap((c) => c.safes);
    if (!m.safeCards?.length) return "";
    const proposals = safes.reduce((n, s) => n + (s.pendingProposals?.length ?? 0), 0);
    return t("safe.announce", { safes: safes.length, proposals });
  }

  /** A reply that is only a data card still gets announced. */
  function dataSummary(m: ChatMessage): string {
    const cards = m.dataCards ?? [];
    const market = cards.find((c) => c.kind === "market");
    if (market) return t("market.announce", { pair: market.pair ?? "" });
    const n = cards.reduce((sum, c) => sum + c.assets.length, 0);
    return n ? t("holdings.announce", { count: n }) : "";
  }

  function apply(m: ChatMessage, e: ChatEvent): void {
    switch (e.type) {
      case "text_delta":
        // A reply cannot grow without bound (BCHAT-025): past the cap further text is dropped.
        if (m.text.length < MAX_REPLY_CHARS) {
          let take = e.text.slice(0, MAX_REPLY_CHARS - m.text.length);
          // Do not cut a surrogate pair in half.
          if (take.length < e.text.length && /[\uD800-\uDBFF]$/.test(take)) take = take.slice(0, -1);
          m.text += take;
          // Say so: a truncated reply must not read as a complete one.
          if (take.length < e.text.length) m.error = t("chat.replyTooLarge");
        } else if (e.text.length > 0) m.error = t("chat.replyTooLarge");
        break;
      case "tool_status": {
        const line = m.tools.find((t) => t.id === e.toolCallId);
        if (line) {
          line.label = e.label;
          line.state = e.state;
        } else m.tools.push({ id: e.toolCallId, label: e.label, state: e.state });
        break;
      }
      case "sign_in_card":
        // The event's own URL is not used: the popup flow always starts from the backend's /auth/popup endpoints.
        m.signIn = {
          reason: e.reason,
          scope: e.scope ?? null,
          description: e.description ?? null,
          unavailable: e.unavailable === true,
          url: e.unavailable === true ? e.signInUrl : null,
        };
        break;
      case "safe_card":
        (m.safeCards ??= []).push(e.card);
        break;
      case "data_card":
        (m.dataCards ??= []).push(e);
        break;
      case "preview_card":
        applyPreview(m, e.preview);
        break;
      case "consent_status": {
        const entry = findPreview(e.consent.previewId);
        if (entry) {
          entry.consent = noCodeUnlessPending(e.consent);
          syncKeepalive(entry);
        } else {
          orphanConsent.set(e.consent.previewId, noCodeUnlessPending(e.consent));
          // Bounded: only a handful can legitimately arrive before their preview_card.
          if (orphanConsent.size > MAX_ORPHAN_CONSENTS) orphanConsent.delete(orphanConsent.keys().next().value as string);
        }
        break;
      }
      case "error":
        m.error = e.code === "stream_too_large" ? t("chat.replyTooLarge") : e.message;
        break;
      default:
        break;
    }
  }

  // ---- Preview cards (T-076) ----
  const orphanConsent = new Map<string, ConsentStatus>();
  const held = new Set<string>();

  function findPreview(previewId: string): PreviewEntry | undefined {
    for (const m of messages) {
      const found = m.previews?.find((p) => p.preview.previewId === previewId);
      if (found) return found;
    }
    return undefined;
  }

  /** The latest event for a previewId replaces the card where it already is; a new id adds a card to this reply. */
  function applyPreview(m: ChatMessage, preview: ActionPreview): void {
    const existing = findPreview(preview.previewId);
    if (existing) {
      existing.preview = preview;
      if (preview.status !== "AwaitingConfirmation") existing.note = "";
      syncKeepalive(existing);
      // A card the backend says is awaiting the phone (a reload, a restored session): one poll, then the stream.
      if (preview.status === "AwaitingApproval") followConsent(preview.previewId, true);
      else if (preview.status === "AwaitingConfirmation" && !existing.prediction) void loadPrediction(preview.previewId);
      return;
    }
    const list = (m.previews ??= []);
    list.push({
      preview,
      consent: orphanConsent.get(preview.previewId) ?? null,
      submitting: false,
      note: "",
      needsSignIn: false,
      scope: null,
    });
    orphanConsent.delete(preview.previewId);
    syncKeepalive(list[list.length - 1]!);
    if (preview.status === "AwaitingApproval") followConsent(preview.previewId, true);
    else if (preview.status === "AwaitingConfirmation") void loadPrediction(preview.previewId);
  }

  function isPending(e: PreviewEntry): boolean {
    return e.submitting || PENDING_STATUSES.includes(e.preview.status) || e.consent?.state === "Pending";
  }
  function syncKeepalive(e: PreviewEntry): void {
    const reason = `preview:${e.preview.previewId}`;
    if (isPending(e)) {
      if (!held.has(reason)) {
        held.add(reason);
        svc.keepalive?.hold(reason);
      }
    } else if (held.delete(reason)) svc.keepalive?.release(reason);
  }

  const OPEN_STEP: ActionPreview["status"][] = ["AwaitingConfirmation", "AwaitingScope", "AwaitingApproval"];

  /**
   * Cancel closes only the steps still open; steps that already landed or were submitted keep their status. The
   * preview as a whole is Cancelled only when nothing settled, otherwise it reflects the partial completion.
   */
  function cancelOpenSteps(e: PreviewEntry, to: "Cancelled" | "Expired" | "Failed" = "Cancelled"): void {
    const steps = e.preview.steps.map((s) => (OPEN_STEP.includes(s.status) ? { ...s, status: to } : s));
    const settled = steps.filter((s) => s.status === "Completed" || s.status === "Submitted");
    const status: ActionPreview["status"] = settled.length === 0 ? to : settled.some((s) => s.status === "Submitted") ? "Submitted" : "Completed";
    e.preview = { ...e.preview, steps, status };
  }

  /**
   * A confirm settles only the step whose group hash was sent, and the txIds belong to that step. The preview turns
   * into a receipt only when no step is still waiting for the user; otherwise the card stays open for the next step.
   */
  function settleStep(e: PreviewEntry, groupHash: string, status: ActionPreview["status"], txIds: string[]): void {
    let hit = false;
    const steps = e.preview.steps.map((s) => {
      if (hit || s.groupHash !== groupHash) return s;
      hit = true;
      return { ...s, status, transactionId: txIds[0] ?? s.transactionId };
    });
    const stillOpen = steps.some((s) => OPEN_STEP.includes(s.status));
    e.preview = { ...e.preview, steps, status: stillOpen ? "AwaitingConfirmation" : status };
  }

  /** Confirm posts the group hash the card showed; every ConfirmResponse.status has its own outcome. */
  async function confirmPreview(previewId: string, retried = false): Promise<void> {
    const entry = findPreview(previewId);
    if (!entry || entry.submitting || !svc.previews) return;
    const hash = decisionHash(entry.preview);
    entry.submitting = true;
    entry.note = "";
    entry.needsSignIn = false;
    syncKeepalive(entry);
    let elevate = false;
    try {
      let r;
      try {
        r = await svc.previews.confirm(previewId, hash);
      } catch {
        entry.note = t("chat.previewConfirmFailed");
        return;
      }
      svc.onConfirmResponse?.(r.raw);
      rounds.delete(previewId);
      elevate = settleResult(entry, previewId, hash, r, retried);
    } finally {
      entry.submitting = false;
      syncKeepalive(entry);
    }
    // Elevate the scope in a popup, then retry the confirm once.
    if (elevate && !disposed) await authorizePreview(previewId, entry.scope ?? "sign");
  }

  /**
   * One ConfirmResponse applied to the card, whether it came from the confirm call or from the end of a consent.
   * Returns true when the scope must be elevated and the confirm retried.
   */
  function settleResult(entry: PreviewEntry, previewId: string, hash: string, r: ConfirmResult, retried = true): boolean {
    switch (r.status) {
      case "landed":
        settleStep(entry, hash, "Completed", r.txIds);
        svc.onConfirmed?.(previewId);
        break;
      case "submitted":
        settleStep(entry, hash, "Submitted", r.txIds);
        svc.onConfirmed?.(previewId);
        break;
      case "consent_pending":
        entry.consent = r.consent ? noCodeUnlessPending(r.consent) : entry.consent;
        entry.preview = { ...entry.preview, status: "AwaitingApproval" };
        svc.onConfirmed?.(previewId);
        followConsent(previewId);
        break;
      case "scope_required":
        entry.scope = r.reauthorizeScope ?? "sign";
        entry.preview = { ...entry.preview, status: "AwaitingScope" };
        return !retried;
      case "sign_in_required":
        entry.note = t("chat.previewSignInRequired");
        entry.needsSignIn = true;
        break;
      case "sign_in_unavailable":
        entry.note = t("chat.previewSignInUnavailable");
        break;
      case "agent_paused":
      case "agent_analyze_only":
        break; // shown through core.agent.reportRefusal with Resume / Change mode
      default:
        entry.note = codeText(r.errorCode) ?? (r.message || t("chat.previewFailed"));
    }
    return false;
  }

  /** Runs the elevate popup for the scope, then retries the confirm once. Also the card's Authorize button. */
  async function authorizePreview(previewId: string, scope: string): Promise<void> {
    const entry = findPreview(previewId);
    if (!entry || entry.submitting) return;
    if (!(await signIn({ scope: entry.scope ?? scope }))) return;
    entry.preview = { ...entry.preview, status: "AwaitingConfirmation" };
    await confirmPreview(previewId, true);
  }

  async function cancelPreview(previewId: string): Promise<void> {
    const entry = findPreview(previewId);
    if (!entry || !svc.cancelPreview || entry.submitting) return;
    if (entry.preview.status === "Cancelled") return;
    // `submitting` also locks Confirm and Cancel while the cancel call is in flight.
    entry.submitting = true;
    // The user is ending the consent: nothing is polled or streamed for it any more (resumed below if the cancel fails).
    stopFollower(previewId);
    syncKeepalive(entry);
    try {
      let ok = false;
      try {
        ok = await svc.cancelPreview(previewId, decisionHash(entry.preview));
      } catch {
        ok = false;
      }
      if (ok) {
        cancelOpenSteps(entry);
        if (entry.consent) entry.consent = noCodeUnlessPending({ ...entry.consent, state: "Denied" });
        entry.note = "";
        svc.onCancelled?.(previewId);
      } else {
        entry.note = t("chat.previewCancelFailed");
        if (!disposed && isWaitingForPhone(entry)) followConsent(previewId, true);
      }
    } finally {
      entry.submitting = false;
      syncKeepalive(entry);
    }
  }

  // ---- Following a phone approval (T-237) ----
  /** One follower per preview, at most. Aborting its controller stops every request and timer it holds. */
  const followers = new Map<string, AbortController>();
  /** Consent rounds opened for a preview, so a backend that keeps asking again cannot keep the widget polling forever. */
  const rounds = new Map<string, number>();
  /** Aborted on dispose and reset: no prediction request is sent after them. */
  let lifetime = new AbortController();
  let hiddenSince: number | null = null;
  const visibleWaiters = new Set<() => void>();

  function onVisibility(): void {
    hiddenSince = document.visibilityState === "hidden" ? Date.now() : null;
    if (hiddenSince === null) {
      for (const wake of [...visibleWaiters]) wake();
      visibleWaiters.clear();
    }
  }
  function watchVisibility(on: boolean): void {
    if (typeof document === "undefined") return;
    if (on) {
      hiddenSince = document.visibilityState === "hidden" ? Date.now() : null;
      document.addEventListener("visibilitychange", onVisibility);
    } else document.removeEventListener("visibilitychange", onVisibility);
  }

  /** Waiting for the phone, or approved and waiting for the outcome of the approval (still being finished). */
  function isWaitingForPhone(e: PreviewEntry): boolean {
    return e.preview.status === "AwaitingApproval" && (e.consent === null || (e.consent.state !== "Denied" && e.consent.state !== "TimedOut"));
  }

  /** When each follower started: a consent without a usable expiry (never expected) is given up five minutes after it. */
  const followStarted = new Map<string, number>();

  /** When the consent is given up: its own expiry (the preview's, if it has none) plus a short grace. */
  function consentDeadline(e: PreviewEntry): number {
    const end = Date.parse(e.consent?.expiresAt ?? e.preview.expiresAt);
    return (Number.isNaN(end) ? (followStarted.get(e.preview.previewId) ?? Date.now()) + 300_000 : end) + CONSENT_GRACE_MS;
  }

  function stopFollower(previewId: string): void {
    followers.get(previewId)?.abort();
    followers.delete(previewId);
    followStarted.delete(previewId);
    if (followers.size === 0) watchVisibility(false);
  }
  function stopAllFollowers(): void {
    for (const ctl of followers.values()) ctl.abort();
    followers.clear();
    followStarted.clear();
    visibleWaiters.clear();
    watchVisibility(false);
    lifetime.abort();
    lifetime = new AbortController();
  }

  /** The card's line about phone approval, asked once when the card is first shown. A failure shows nothing. */
  const predicting = new Set<string>();
  async function loadPrediction(previewId: string): Promise<void> {
    const api = svc.consent;
    if (!api || disposed || predicting.has(previewId)) return;
    predicting.add(previewId);
    const text = await api.prediction(previewId, lifetime.signal).catch(() => null);
    predicting.delete(previewId);
    if (!text || disposed) return;
    const entry = findPreview(previewId);
    if (entry && entry.preview.status === "AwaitingConfirmation") entry.prediction = text;
  }

  /** Shows a consent status on the card. The code is only kept while pending, and Activity logs the change. */
  function showConsent(entry: PreviewEntry, c: ConsentStatus): void {
    const shown = noCodeUnlessPending(c);
    entry.consent = shown;
    syncKeepalive(entry);
    svc.onEvent?.({ type: "consent_status", consent: shown });
  }

  /** Everything that follows a consent that ended without a signature: the card stops waiting and says why. */
  function closeWithoutSignature(entry: PreviewEntry, to: "Cancelled" | "Expired" | "Failed", note: string): void {
    cancelOpenSteps(entry, to);
    entry.note = note;
    syncKeepalive(entry);
    svc.onEvent?.({ type: "preview_card", preview: entry.preview });
  }

  /** The consent ended: apply the backend's outcome (or the bare consent state when no outcome came with it). */
  function finishConsent(entry: PreviewEntry, consent: ConsentStatus, result: ConfirmResult | null): void {
    if (result) {
      svc.onConfirmResponse?.(result.raw);
      const hash = entry.preview.steps.find((s) => s.stepId === consent.stepId)?.groupHash ?? decisionHash(entry.preview);
      const elevate = settleResult(entry, entry.preview.previewId, hash, result, false);
      svc.onEvent?.({ type: "preview_card", preview: entry.preview });
      // The approval could not be used because signing needs one more permission: ask for it, then confirm again.
      if (elevate && !disposed) void authorizePreview(entry.preview.previewId, entry.scope ?? "sign");
      // A new round of consent (the approval could not be used): the follower goes on with the new one.
      if (result.status === "consent_pending") return;
      // The outcome moved the card on (landed, submitted, a scope to elevate): nothing more to do.
      if (entry.preview.status !== "AwaitingApproval") return;
      // Not a failure of the action: sign-in lost, or the agent paused. The card goes back to where Confirm would leave it.
      if (RECOVERABLE.includes(result.status)) {
        entry.consent = null;
        entry.preview = { ...entry.preview, status: "AwaitingConfirmation" };
        syncKeepalive(entry);
        svc.onEvent?.({ type: "preview_card", preview: entry.preview });
        return;
      }
    }
    // Nothing was signed: the card leaves the waiting state, with the reason.
    const known = codeText(result?.errorCode);
    if (consent.state === "Denied") closeWithoutSignature(entry, "Cancelled", known ?? t("consent.denied"));
    else if (consent.state === "TimedOut") closeWithoutSignature(entry, "Expired", known ?? t("consent.timedOut"));
    else closeWithoutSignature(entry, "Failed", known ?? (entry.note || result?.message || t("chat.previewFailed")));
  }

  /** The consent ran out on this side (its expiry and the grace have passed with no answer). */
  function timeOut(entry: PreviewEntry): void {
    const step = entry.preview.steps.find((s) => OPEN_STEP.includes(s.status)) ?? entry.preview.steps[0];
    const c: ConsentStatus = { previewId: entry.preview.previewId, stepId: entry.consent?.stepId ?? step?.stepId ?? "", state: "TimedOut", expiresAt: entry.consent?.expiresAt ?? null };
    showConsent(entry, c);
    closeWithoutSignature(entry, "Expired", t("consent.timedOut"));
  }

  /** The backend says this consent cannot be followed (sign-in gone, preview or consent not there any more). */
  function lostConsent(entry: PreviewEntry, status: number): void {
    entry.consent = null;
    entry.needsSignIn = status === 401;
    closeWithoutSignature(entry, "Expired", t(status === 401 ? "chat.previewSignInRequired" : "consent.lost"));
  }

  /**
   * Follows the consent of a preview to its end. Only one follower runs per preview. It opens the consent stream; if
   * that fails or ends early it polls once per backoff step and retries the stream every minute, until the consent
   * ends, the user cancels, the widget goes away, or the consent's expiry (plus ten seconds) has passed.
   * `pollFirst` is for a card that is found already waiting (a reload): one plain poll before the stream.
   */
  function followConsent(previewId: string, pollFirst = false, newRound = false): void {
    const api = svc.consent;
    if (!api || disposed || followers.has(previewId)) return;
    const entry = findPreview(previewId);
    if (!entry || !isWaitingForPhone(entry)) return;
    if (newRound) {
      const n = (rounds.get(previewId) ?? 0) + 1;
      if (n > MAX_CONSENT_ROUNDS) {
        // The backend keeps asking again: stop following and say so, rather than leave the card waiting for nothing.
        entry.consent = null;
        closeWithoutSignature(entry, "Expired", t("consent.lost"));
        return;
      }
      rounds.set(previewId, n);
    }
    if (followers.size === 0) watchVisibility(true);
    const ctl = new AbortController();
    followers.set(previewId, ctl);
    if (!followStarted.has(previewId)) followStarted.set(previewId, Date.now());
    void runFollower(previewId, api, ctl, pollFirst)
      .catch(() => {
        // Something unexpected threw while applying an answer: the card must not wait for a follower that is gone.
        const stuck = findPreview(previewId);
        if (!stuck || disposed || ctl.signal.aborted || !isWaitingForPhone(stuck)) return;
        try {
          stuck.consent = null;
          closeWithoutSignature(stuck, "Failed", t("consent.lost"));
        } catch {
          /* nothing more can be done for this card */
        }
      })
      .finally(() => {
        if (followers.get(previewId) === ctl) {
          followers.delete(previewId);
          followStarted.delete(previewId);
        }
        if (followers.size === 0) watchVisibility(false);
        // The outcome was a new round of consent: follow that one too.
        const again = findPreview(previewId);
        if (!ctl.signal.aborted && !disposed && again && isWaitingForPhone(again)) followConsent(previewId, false, true);
      });
  }

  async function runFollower(previewId: string, api: ConsentApi, ctl: AbortController, pollFirst: boolean): Promise<void> {
    const signal = ctl.signal;
    const live = () => !signal.aborted && !disposed;

    /** Applies what one answer says. True when the follower is done. */
    const take = (entry: PreviewEntry, consent: ConsentStatus, result: ConfirmResult | null): boolean => {
      showConsent(entry, consent);
      if (consent.state === "Pending") return false;
      // Approved and the outcome is still on its way (another tab or poll is finishing it): keep asking.
      if (consent.state === "Approved" && !result) return false;
      finishConsent(entry, consent, result);
      return true;
    };

    const gone = (): PreviewEntry | null => {
      const entry = findPreview(previewId);
      return entry && isWaitingForPhone(entry) ? entry : null;
    };

    /**
     * One plain poll. True when the follower is done. A consent past its deadline is not asked about: its approval can
     * no longer be redeemed (the backend only signs inside a poll or stream, and Biatec OIDC has expired the request),
     * so nothing was signed and the answer is known without a request.
     */
    const pollStep = async (): Promise<boolean> => {
      const entry = gone();
      if (!entry) return true;
      if (Date.now() >= consentDeadline(entry)) return timeOutIfWaiting(entry);
      let answer: Awaited<ReturnType<ConsentApi["poll"]>>;
      try {
        answer = await api.poll(previewId, signal);
      } catch {
        return false;
      }
      if (!live()) return true;
      const now = findPreview(previewId);
      if (!now) return true;
      if (!answer.ok) {
        if (answer.status === 401 || answer.status === 404 || answer.status === 409) {
          lostConsent(now, answer.status);
          return true;
        }
        return false;
      }
      return take(now, answer.consent, answer.result);
    };

    const timeOutIfWaiting = (entry: PreviewEntry): true => {
      if (entry.consent?.state === "Approved") {
        // Approved, but the outcome never reached this tab: do not claim a time-out that did not happen.
        closeWithoutSignature(entry, "Failed", t("consent.lost"));
      } else timeOut(entry);
      return true;
    };

    /** One stream attempt. True when the follower is done. */
    const streamStep = async (): Promise<boolean> => {
      const entry = gone();
      if (!entry) return true;
      const wait = consentDeadline(entry) - Date.now();
      if (wait <= 0) return timeOutIfWaiting(entry);
      // The stream is also cut at the consent's deadline, in case the connection goes quiet without closing.
      const step = new AbortController();
      let cutByDeadline = false;
      const cut = () => step.abort();
      signal.addEventListener("abort", cut, { once: true });
      // A delay past about 24 days overflows setTimeout and would fire at once: such a consent is never cut by the timer.
      const timer =
        wait < 2_000_000_000
          ? setTimeout(() => {
              cutByDeadline = true;
              step.abort();
            }, wait)
          : undefined;
      let ended: ConsentStatus | null = null;
      try {
        for await (const ev of api.stream(previewId, step.signal)) {
          if (!live()) return true;
          const now = findPreview(previewId);
          if (!now) return true;
          if (ev.kind === "status") {
            showConsent(now, ev.consent);
            if (ev.consent.state !== "Pending") ended = ev.consent;
          } else if (ev.kind === "result") {
            finishConsent(now, ended ?? now.consent ?? { previewId, stepId: "", state: "Approved" }, ev.result);
            return true;
          } else break;
        }
      } catch (err) {
        if (!live()) return true;
        const status = (err as { status?: number } | null)?.status;
        const now = findPreview(previewId);
        if (!now) return true;
        if (status === 401 || status === 404 || status === 409) {
          lostConsent(now, status);
          return true;
        }
        if (cutByDeadline) return timeOutIfWaiting(now);
        return false;
      } finally {
        clearTimeout(timer);
        signal.removeEventListener("abort", cut);
        // Whichever way this attempt ended, its connection ends with it (a stream the server keeps open must not linger).
        step.abort();
      }
      if (!live()) return true;
      const now = findPreview(previewId);
      if (!now) return true;
      // The stream ended after a final status but without an outcome: that status is the outcome.
      if (ended && ended.state !== "Approved") {
        finishConsent(now, ended, null);
        return true;
      }
      if (cutByDeadline) return timeOutIfWaiting(now);
      return false;
    };

    /** A tab hidden for a while does not poll; the poll happens once it is visible again. */
    const visibleGate = async (): Promise<void> => {
      if (hiddenSince === null || Date.now() - hiddenSince <= HIDDEN_PAUSE_MS) return;
      await new Promise<void>((resolve) => {
        const wake = () => {
          visibleWaiters.delete(wake);
          signal.removeEventListener("abort", wake);
          resolve();
        };
        visibleWaiters.add(wake);
        signal.addEventListener("abort", wake, { once: true });
      });
    };

    if (pollFirst && (await pollStep())) return;
    while (live()) {
      if (await streamStep()) return;
      // The stream failed or ended early: single polls with backoff until it is time to try the stream again.
      const retryAt = Date.now() + STREAM_RETRY_MS;
      for (let i = 0; live() && Date.now() < retryAt; i++) {
        // Never sleep past the consent's deadline: the time-out is shown when it is due.
        const left = (() => {
          const e = findPreview(previewId);
          return e ? consentDeadline(e) - Date.now() : 0;
        })();
        await sleepUnlessAborted(Math.max(0, Math.min(jitter(POLL_BACKOFF_MS[Math.min(i, POLL_BACKOFF_MS.length - 1)]!), left)), signal);
        if (!live()) return;
        await visibleGate();
        if (!live()) return;
        if (await pollStep()) return;
      }
    }
  }

  function previewSummary(m: ChatMessage): string {
    const p = m.previews?.[0];
    return p ? t("chat.previewAnnounce", { intent: p.preview.intent }) : "";
  }

  /**
   * The user's own way out of a sign-in or sign-out that is waiting. The popup's `closed` flag is not a cancel signal:
   * once the identity provider sends Cross-Origin-Opener-Policy the opener reads it as true from the start.
   */
  let waiting: { cancelled: boolean } | null = null;
  function cancelSignIn(): void {
    if (waiting && (phase.value === "waiting" || phase.value === "opening")) waiting.cancelled = true;
  }

  function announceSignedIn(): void {
    announce(session.value?.email ? t("chat.signedInAs", { email: session.value.email }) : t("chat.signedIn"));
  }

  const POPUP_FEATURES = "popup=yes,width=480,height=720";

  /**
   * Sign in (or elevate a scope) through a popup. The popup is opened synchronously from the click so browsers do
   * not block it, then pointed at the one-time URL from the backend. Completion is learned only by polling.
   */
  async function signIn(opts: { scope?: string | null } = {}): Promise<boolean> {
    if (phase.value === "opening" || phase.value === "waiting") return false;
    const popup = svc.openPopup ? svc.openPopup() : window.open("", "biatec-chat-signin", POPUP_FEATURES);
    if (!popup) {
      phase.value = "blocked";
      return false;
    }
    phase.value = "opening";
    const mine = (waiting = { cancelled: false });
    const fail = (): false => {
      try {
        popup.close();
      } catch {
        /* already closed */
      }
      phase.value = "failed";
      return false;
    };
    /**
     * A user's Cancel is not a failure. The flow may have finished a moment before the press, so the session is read once:
     * a sign-in that did complete is reported as signed in, otherwise the widget just returns to idle.
     */
    const finishCancelled = async (): Promise<boolean> => {
      try {
        popup.close();
      } catch {
        /* already closed */
      }
      if (disposed) return false;
      await refreshSession();
      if (disposed) return false;
      phase.value = "idle";
      if (!opts.scope && tier.value === "Authenticated") {
        announceSignedIn();
        return true;
      }
      announce(t("chat.signInCancelled"));
      return false;
    };
    let cookieOk = true;
    if (!opts.scope) {
      cookieOk = await svc.transport.retryOnSignIn();
      // The transport just switched to the cookie transport: the header and session were stale until now.
      if (cookieOk) await refreshSession();
    }
    const started = opts.scope ? await svc.auth.startElevate(opts.scope) : await svc.auth.startLogin();
    // A flow that could not start is reported as such (for example blocked cookies), whether or not Cancel was pressed.
    if (!started || disposed) {
      fail();
      if (!cookieOk && !disposed) phase.value = "no-cookies";
      return false;
    }
    // The user pressed Cancel while the flow was still opening: do not send the popup to the login page.
    if (mine.cancelled) return finishCancelled();
    popup.location.href = started.loginUrl;
    phase.value = "waiting";

    const pollMs = svc.pollMs ?? 1000;
    const deadline = Date.now() + (svc.timeoutMs ?? 5 * 60_000);
    while (!disposed && Date.now() < deadline && !mine.cancelled) {
      await sleep(pollMs);
      if (mine.cancelled) break;
      const status = await svc.auth.status(started.authRequestId);
      if (status === "Complete") {
        try {
          popup.close();
        } catch {
          /* ignore */
        }
        await refreshSession();
        phase.value = "idle";
        announceSignedIn();
        return true;
      }
      if (status === "Failed") return fail();
    }
    if (mine.cancelled) return finishCancelled();
    return fail();
  }

  /**
   * Sign out through a popup navigation to the backend's /auth/logout (a fetch would be refused). The visible
   * conversation is only cleared once the session is really gone; otherwise the state is left as it was.
   */
  async function signOut(): Promise<boolean> {
    if (phase.value === "opening" || phase.value === "waiting") return false;
    const popup = svc.openPopup ? svc.openPopup() : window.open("", "biatec-chat-signout", POPUP_FEATURES);
    if (!popup) {
      phase.value = "blocked";
      return false;
    }
    const closePopup = () => {
      try {
        popup.close();
      } catch {
        /* already closed */
      }
    };
    const mine = (waiting = { cancelled: false });
    phase.value = "waiting";
    popup.location.href = svc.auth.logoutUrl();
    const pollMs = svc.pollMs ?? 1000;
    const deadline = Date.now() + (svc.timeoutMs ?? 60_000);
    while (!disposed && Date.now() < deadline && !mine.cancelled) {
      await sleep(pollMs);
      if (mine.cancelled) break;
      const s = await svc.auth.session();
      if (s && s.tier === "Anonymous") {
        session.value = s;
        closePopup();
        resetConversation();
        phase.value = "idle";
        announce(t("chat.signedOut"));
        return true;
      }
    }
    closePopup();
    if (!disposed) {
      await refreshSession();
      if (disposed) return false;
      if (mine.cancelled) {
        // Cancel is not a failure; the logout may have completed a moment before the press.
        if (tier.value === "Anonymous") {
          resetConversation();
          phase.value = "idle";
          announce(t("chat.signedOut"));
          return true;
        }
        phase.value = "idle";
        announce(t("chat.signOutCancelled"));
        return false;
      }
      phase.value = "signout-failed";
    }
    return false;
  }

  /**
   * Drop everything shown for the current conversation: the transcript, a reply still streaming, the keepalive holds
   * of its previews, the live-region text, and (through onReset) the Activity derived from it. Sign-out and erasure
   * use it; the backend has already moved the session on.
   */
  function resetConversation(): void {
    generation++;
    abort?.abort();
    abort = null;
    messages.splice(0, messages.length);
    stopAllFollowers();
    rounds.clear();
    for (const reason of held) svc.keepalive?.release(reason);
    held.clear();
    announcement.value = "";
    svc.onReset?.();
  }

  onBeforeUnmount(() => {
    disposed = true;
    abort?.abort();
    stopAllFollowers();
    for (const reason of held) svc.keepalive?.release(reason);
    held.clear();
  });

  return { confirmPreview, authorizePreview, cancelPreview, messages, session, tier, busy, phase, announcement, start, send, signIn, cancelSignIn, signOut, refreshSession, resetConversation };
}
