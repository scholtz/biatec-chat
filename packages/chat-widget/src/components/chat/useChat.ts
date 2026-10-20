import { reactive, ref, computed, onBeforeUnmount } from "vue";
import type { ActionPreview, ChatEvent, ConsentStatus, DataCardEvent, SafeCard, SessionState, SessionTier, Suggestion } from "@biatec/chat-protocol";
import { t } from "../../i18n";
import type { TransportManager } from "../../transport";
import type { AuthClient } from "./auth";
import { PENDING_STATUSES, decisionHash, type PreviewApi, type PreviewEntry } from "./previews";

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
}

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms));

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
    try {
      for await (const e of svc.transport.send({ message, signal: ctl.signal, ...(opts.includeContext === false ? { context: null } : {}) })) apply(reply, e);
    } catch {
      if (!ctl.signal.aborted) reply.error = t("chat.errorGeneric");
    } finally {
      reply.streaming = false;
      busy.value = false;
      abort = null;
      announce(reply.error ?? (reply.text || reply.signIn?.description || safeSummary(reply) || dataSummary(reply) || previewSummary(reply)));
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
    const n = (m.dataCards ?? []).reduce((sum, c) => sum + c.assets.length, 0);
    return n ? t("holdings.announce", { count: n }) : "";
  }

  function apply(m: ChatMessage, e: ChatEvent): void {
    switch (e.type) {
      case "text_delta":
        m.text += e.text;
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
          entry.consent = e.consent;
          syncKeepalive(entry);
        } else orphanConsent.set(e.consent.previewId, e.consent);
        break;
      }
      case "error":
        m.error = e.message;
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
  function cancelOpenSteps(e: PreviewEntry): void {
    const steps = e.preview.steps.map((s) => (OPEN_STEP.includes(s.status) ? { ...s, status: "Cancelled" as const } : s));
    const settled = steps.filter((s) => s.status === "Completed" || s.status === "Submitted");
    const status: ActionPreview["status"] = settled.length === 0 ? "Cancelled" : settled.some((s) => s.status === "Submitted") ? "Submitted" : "Completed";
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
          entry.consent = r.consent ?? entry.consent;
          entry.preview = { ...entry.preview, status: "AwaitingApproval" };
          svc.onConfirmed?.(previewId);
          break;
        case "scope_required":
          entry.scope = r.reauthorizeScope ?? "sign";
          entry.preview = { ...entry.preview, status: "AwaitingScope" };
          elevate = !retried;
          break;
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
          entry.note = r.message || t("chat.previewFailed");
      }
    } finally {
      entry.submitting = false;
      syncKeepalive(entry);
    }
    // Elevate the scope in a popup, then retry the confirm once.
    if (elevate && !disposed) await authorizePreview(previewId, entry.scope ?? "sign");
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
        entry.note = "";
        svc.onCancelled?.(previewId);
      } else entry.note = t("chat.previewCancelFailed");
    } finally {
      entry.submitting = false;
      syncKeepalive(entry);
    }
  }

  function previewSummary(m: ChatMessage): string {
    const p = m.previews?.[0];
    return p ? t("chat.previewAnnounce", { intent: p.preview.intent }) : "";
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
    const fail = (): false => {
      try {
        popup.close();
      } catch {
        /* already closed */
      }
      phase.value = "failed";
      return false;
    };
    let cookieOk = true;
    if (!opts.scope) {
      cookieOk = await svc.transport.retryOnSignIn();
      // The transport just switched to the cookie transport: the header and session were stale until now.
      if (cookieOk) await refreshSession();
    }
    const started = opts.scope ? await svc.auth.startElevate(opts.scope) : await svc.auth.startLogin();
    if (!started || disposed) {
      fail();
      if (!cookieOk && !disposed) phase.value = "no-cookies";
      return false;
    }
    popup.location.href = started.loginUrl;
    phase.value = "waiting";

    const pollMs = svc.pollMs ?? 1000;
    const deadline = Date.now() + (svc.timeoutMs ?? 5 * 60_000);
    let closedSeen = false;
    while (!disposed && Date.now() < deadline) {
      await sleep(pollMs);
      const status = await svc.auth.status(started.authRequestId);
      if (status === "Complete") {
        try {
          popup.close();
        } catch {
          /* ignore */
        }
        await refreshSession();
        phase.value = "idle";
        announce(session.value?.email ? t("chat.signedInAs", { email: session.value.email }) : t("chat.signedIn"));
        return true;
      }
      if (status === "Failed") return fail();
      // The user closed the popup: allow one more poll for a flow that finished just before, then give up.
      if (popup.closed) {
        if (closedSeen) return fail();
        closedSeen = true;
      }
    }
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
    phase.value = "waiting";
    popup.location.href = svc.auth.logoutUrl();
    const pollMs = svc.pollMs ?? 1000;
    const deadline = Date.now() + (svc.timeoutMs ?? 60_000);
    let closedSeen = false;
    while (!disposed && Date.now() < deadline) {
      await sleep(pollMs);
      const s = await svc.auth.session();
      if (s && s.tier === "Anonymous") {
        session.value = s;
        closePopup();
        messages.splice(0, messages.length);
        phase.value = "idle";
        announce(t("chat.signedOut"));
        return true;
      }
      if (popup.closed) {
        if (closedSeen) break;
        closedSeen = true;
      }
    }
    closePopup();
    if (!disposed) {
      phase.value = "signout-failed";
      await refreshSession();
    }
    return false;
  }

  onBeforeUnmount(() => {
    disposed = true;
    abort?.abort();
    for (const reason of held) svc.keepalive?.release(reason);
    held.clear();
  });

  return { confirmPreview, authorizePreview, cancelPreview, messages, session, tier, busy, phase, announcement, start, send, signIn, signOut, refreshSession };
}
