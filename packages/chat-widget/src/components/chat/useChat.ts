import { reactive, ref, computed, onBeforeUnmount } from "vue";
import type { ChatEvent, SessionState, SessionTier } from "@biatec/chat-protocol";
import type { TransportManager } from "../../transport";
import type { AuthClient } from "./auth";

export interface ToolLine {
  id: string;
  label: string;
  state: string;
}
export interface SignInCard {
  reason: "SignIn" | "ElevateScope";
  scope: string | null;
  description: string | null;
}
export interface ChatMessage {
  id: number;
  role: "user" | "assistant";
  text: string;
  tools: ToolLine[];
  signIn?: SignInCard;
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

  async function send(text: string): Promise<void> {
    const message = text.trim();
    if (!message || busy.value) return;
    messages.push({ id: nextId++, role: "user", text: message, tools: [], streaming: false });
    messages.push({ id: nextId++, role: "assistant", text: "", tools: [], streaming: true });
    // Index into the reactive array so mutations below are tracked.
    const reply = messages[messages.length - 1]!;
    busy.value = true;
    const ctl = (abort = new AbortController());
    try {
      for await (const e of svc.transport.send({ message, signal: ctl.signal })) apply(reply, e);
    } catch {
      if (!ctl.signal.aborted) reply.error = "Something went wrong. Please try again.";
    } finally {
      reply.streaming = false;
      busy.value = false;
      abort = null;
      announce(reply.error ?? (reply.text || reply.signIn?.description || ""));
    }
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
        m.signIn = { reason: e.reason, scope: e.scope ?? null, description: e.description ?? null };
        break;
      case "error":
        m.error = e.message;
        break;
      default:
        break; // preview_card and consent_status are rendered by T-042
    }
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
        announce(session.value?.email ? `Signed in as ${session.value.email}` : "Signed in");
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
        announce("Signed out");
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
  });

  return { messages, session, tier, busy, phase, announcement, start, send, signIn, signOut, refreshSession };
}
