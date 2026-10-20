import { computed, reactive, ref, type Ref } from "vue";
import type { AgentMode, SessionState } from "@biatec/chat-protocol";
import type { ToastMessage } from "../../ui/Toast.vue";
import type { Activity } from "../activity/model";
import { refusalCode, type AgentApi, type RefusalCode } from "./api";

/** A change that gives Biatec AI more power: it always goes through the confirm step. */
export type Raise = "prepare" | "resume";
export type DialogStep = "edit" | "confirm" | "revoke";

interface ChatLike {
  session: Ref<SessionState | null>;
  signOut(): Promise<boolean>;
  send(text: string): Promise<void>;
}

const TOAST_MS = 2800;

/**
 * What the user lets Biatec AI do in this session: reads the mode from the session and changes it through
 * PUT /session/agent. The backend enforces it (T-065); the widget only reflects what the server answered.
 */
export function useAgent(chat: ChatLike, api: AgentApi, activity: Activity, ui: { permissions: boolean }) {
  const signedIn = computed(() => chat.session.value?.tier === "Authenticated");
  const mode = computed<AgentMode>(() =>
    signedIn.value && chat.session.value?.agentMode !== "analyze" ? "prepare" : "analyze",
  );
  const paused = computed(() => signedIn.value && !!chat.session.value?.paused);

  /** Dialog state lives here so the paused banner, the aside and refusals can open it at the confirm step. */
  const dialog = reactive({ step: "edit" as DialogStep, draft: "analyze" as AgentMode, raise: null as Raise | null, ack: false });
  const saving = ref(false);
  /** i18n key of the last failed change, shown in the dialog. */
  const error = ref("");
  const refusal = ref<RefusalCode | null>(null);
  const toasts = reactive<ToastMessage[]>([]);
  let toastId = 1;

  function toast(text: string, tone: ToastMessage["tone"] = "neutral"): void {
    const id = toastId++;
    toasts.push({ id, text, tone });
    setTimeout(() => dismiss(id), TOAST_MS);
  }
  function dismiss(id: string | number): void {
    const i = toasts.findIndex((m) => m.id === id);
    if (i >= 0) toasts.splice(i, 1);
  }

  function open(raise: Raise | null = null): void {
    dialog.draft = raise === "prepare" ? "prepare" : mode.value;
    dialog.raise = raise;
    dialog.ack = false;
    dialog.step = raise ? "confirm" : "edit";
    error.value = "";
    ui.permissions = true;
  }
  /** Confirm or revoke step back to the edit step. */
  function back(): void {
    dialog.step = "edit";
    dialog.raise = null;
    dialog.ack = false;
    error.value = "";
  }
  /** Esc order: a confirm or revoke step goes back first, then the dialog closes. */
  function escape(): void {
    if (dialog.step !== "edit") back();
    else close();
  }
  function close(): void {
    ui.permissions = false;
    dialog.step = "edit";
    dialog.raise = null;
    dialog.ack = false;
  }

  /** Sends the new state; on success the session (and so the status pill) shows what the server enforces. */
  async function apply(nextMode: AgentMode, nextPaused: boolean): Promise<boolean> {
    if (saving.value || !signedIn.value) return false;
    const before = { mode: mode.value, paused: paused.value };
    saving.value = true;
    error.value = "";
    try {
      const r = await api.setAgent(nextMode, nextPaused);
      const s = chat.session.value;
      if (s) chat.session.value = { ...s, agentMode: r.agentMode, paused: r.paused };
      if (r.agentMode !== before.mode)
        activity.log({ kind: "permission", title: r.agentMode === "analyze" ? "modeLowered" : "modeRaised", by: "you" });
      if (r.paused !== before.paused) activity.log({ kind: "permission", title: r.paused ? "paused" : "resumed", by: "you" });
      if (!r.paused && refusal.value === "agent_paused") refusal.value = null;
      if (r.agentMode === "prepare" && refusal.value === "agent_analyze_only") refusal.value = null;
      return true;
    } catch {
      error.value = "agent.saveFailed";
      return false;
    } finally {
      saving.value = false;
    }
  }

  /** Lowering is instant: no confirm step. */
  const lower = () => apply("analyze", paused.value);
  const pause = () => apply(mode.value, true);

  /** Applies the raise the user confirmed in the confirm step. */
  async function confirmRaise(): Promise<boolean> {
    if (!dialog.ack || !dialog.raise) return false;
    const ok = dialog.raise === "resume" ? await apply(mode.value, false) : await apply("prepare", paused.value);
    if (ok) {
      toast(dialog.raise === "resume" ? "agent.toastResumed" : "agent.toastRaised");
      close();
    }
    return ok;
  }

  const revoking = ref(false);
  /** Cancels every open preview first, then signs out. A preview the backend would not cancel dies with the session. */
  async function revoke(): Promise<boolean> {
    if (revoking.value) return false;
    revoking.value = true;
    try {
      const open = activity.openPreviews.value;
      const results = await Promise.all(open.map((p) => api.cancelPreview(p.previewId, p.groupHash)));
      open.forEach((p, i) => results[i] && activity.markCancelled(p.previewId));
      activity.log({ kind: "permission", title: "revoked", by: "you" });
      close();
      const ok = await chat.signOut();
      toast(ok ? "agent.toastRevoked" : "agent.toastRevokeFailed", ok ? "neutral" : "danger");
      if (ok) refusal.value = null;
      return ok;
    } finally {
      revoking.value = false;
    }
  }

  /** Feed a confirm response or error body here; a typed agent refusal shows a message with the way out. */
  function reportRefusal(body: unknown): RefusalCode | null {
    const code = refusalCode(body);
    if (code) refusal.value = code;
    return code;
  }

  /** "Show my spending limits": asks through the chat, so the `limits.view` capability answers. */
  function showLimits(text: string): void {
    close();
    void chat.send(text);
  }

  return {
    signedIn,
    mode,
    paused,
    dialog,
    saving,
    error,
    refusal,
    revoking,
    toasts,
    toast,
    dismiss,
    open,
    back,
    escape,
    close,
    apply,
    lower,
    pause,
    confirmRaise,
    revoke,
    reportRefusal,
    showLimits,
  };
}

export type Agent = ReturnType<typeof useAgent>;
