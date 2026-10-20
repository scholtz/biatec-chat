import { computed, onBeforeUnmount, reactive, watch } from "vue";
import type { ChatEvent, HostContext } from "@biatec/chat-protocol";
import type { ResolvedOptions } from "../options";
import { AuthClient } from "../components/chat/auth";
import { useChat, type ChatServices } from "../components/chat/useChat";
import { TransportManager } from "../transport";
import { AgentClient, type AgentApi } from "../components/agent/api";
import { useAgent } from "../components/agent/useAgent";
import { useActivity } from "../components/activity/model";
import { t } from "../i18n";
import { currentCore, hostSettings, registerCore, type CoreHandle } from "./controller";
import type { AskResult } from "./types";

export interface ShellState {
  /** Floating panel open (the minimized bar counts as open). */
  open: boolean;
  minimized: boolean;
  /** Workspace (full screen) instead of the floating or inline frame. */
  expanded: boolean;
  /** Activity aside shown (T-066 mounts its content). */
  activity: boolean;
  /** Permissions dialog open (T-066 mounts it). */
  permissions: boolean;
  /** Assistant replies finished while the panel was hidden. */
  unread: number;
  /** One-line visible note from the shell (for example an `ask` refused while a reply streams). */
  notice: string;
  /** Mounted `BiatecChatInline` panels; while one is mounted the floating launcher and panel hide. */
  inline: number;
  /** Bumped whenever focus should move to the composer (open, restore, ask). */
  focusTick: number;
}

export type WidgetCore = ReturnType<typeof createCore>;

/** Chat services plus the agent-mode and preview-decision calls (T-066); each is a test seam. */
export type WidgetServices = ChatServices & { agent?: AgentApi };

/** Page context as the chat request's HostContext. Nothing is read from the host page: only what the host passed. */
export function pageContext(options: ResolvedOptions): HostContext | null {
  const ctx = hostSettings.pageContextSet ? hostSettings.pageContext : options.pageContext;
  if (!ctx) return null;
  return { product: ctx, locale: hostSettings.locale ?? options.locale };
}

/**
 * The state one widget owns: the chat session (transport, auth, messages) and the shell state. Must be called in a
 * component's setup; it is released when that component unmounts. The floating root owns it, and a
 * `BiatecChatInline` mounted next to it shares it, so both show the same conversation.
 */
export function createCore(options: ResolvedOptions, services?: WidgetServices) {
  const cfg = { backendUrl: options.backendUrl, clientKey: options.clientKey };
  const svc: ChatServices = services ?? { transport: new TransportManager(cfg), auth: new AuthClient(cfg) };
  const base = svc.transport;
  const activity = useActivity();
  // The page context is attached to each message at send time (so a later setPageContext applies without remount).
  // Messages are only sent from an open panel, so the context never leaves the page before the user opened it.
  const transport: ChatServices["transport"] = {
    open: () => base.open(),
    retryOnSignIn: () => base.retryOnSignIn(),
    send: (o) => tap(base.send({ ...o, context: o.context !== undefined ? o.context : pageContext(options) }), o.message),
    dispose: () => base.dispose(),
  };
  const chat = useChat({ ...svc, transport });

  /** Every stream event also feeds the Activity timeline and the agent's typed refusals. */
  async function* tap(events: AsyncGenerator<ChatEvent>, question: string): AsyncGenerator<ChatEvent> {
    let ok = true;
    try {
      for await (const e of events) {
        activity.record(e);
        if (e.type === "error") {
          ok = false;
          agent.reportRefusal(e);
        }
        yield e;
      }
    } catch (err) {
      ok = false;
      throw err;
    } finally {
      if (ok) activity.recordReply(question);
    }
  }

  const ui = reactive<ShellState>({
    open: false,
    minimized: false,
    expanded: options.mode === "workspace",
    activity: options.mode === "workspace",
    permissions: false,
    unread: 0,
    notice: "",
    inline: 0,
    focusTick: 0,
  });
  const agent = useAgent(chat, services?.agent ?? new AgentClient(cfg), activity, ui);

  /** Whether a panel can be shown at all: `mode: 'inline'` has no floating panel, only BiatecChatInline. */
  const canShow = computed(() => ui.inline > 0 || options.mode !== "inline");
  /** The conversation is on screen (inline panels are always shown while mounted). */
  const visible = computed(() => ui.inline > 0 || (ui.open && !ui.minimized && options.mode !== "inline"));

  let started: Promise<void> | null = null;
  /** First open runs the cookie probe (T-040). Never on page load. */
  function start(): Promise<void> {
    return (started ??= chat.start().catch(() => {}));
  }

  function openPanel(): void {
    if (!canShow.value) return;
    if (ui.inline === 0) {
      ui.open = true;
      ui.minimized = false;
    }
    ui.unread = 0;
    void start();
    ui.focusTick++;
  }

  function closePanel(): void {
    ui.open = false;
    ui.minimized = false;
    ui.permissions = false;
    ui.expanded = options.mode === "workspace";
    ui.activity = ui.expanded;
    ui.notice = "";
  }

  function minimize(): void {
    if (!ui.open) return;
    ui.minimized = true;
    ui.permissions = false;
  }

  function toggleExpand(): void {
    ui.expanded = !ui.expanded;
    // Workspace shows the Activity aside next to the conversation; the smaller frames start without it.
    ui.activity = ui.expanded;
  }

  let asking = false;
  async function ask(prompt: string): Promise<AskResult> {
    const text = prompt.trim();
    if (!text) return "empty";
    if (!canShow.value) {
      console.warn("[biatec-chat] ask(): mode is 'inline' but no BiatecChatInline is mounted; nothing was sent.");
      return "unavailable";
    }
    const refuse = (): AskResult => {
      ui.notice = t("shell.askBusy");
      return "busy";
    };
    if (asking || chat.busy.value) {
      openPanel();
      return refuse();
    }
    asking = true;
    try {
      // Open first: the panel must be visible, and the first open starts the transport probe.
      openPanel();
      await start();
      if (chat.busy.value) return refuse();
      ui.notice = "";
      // Same path as the composer: the prompt becomes a visible user message, sent once.
      void chat.send(text);
      return "sent";
    } finally {
      asking = false;
    }
  }

  // Unread badge: a reply that finishes while the panel is closed or minimized.
  watch(chat.busy, (now, before) => {
    // A refused ask's notice holds only while the reply it waited for streams, and goes with any new send.
    if (now !== before) ui.notice = "";
    if (before && !now && !visible.value) ui.unread++;
  });
  watch(visible, (v) => {
    if (v) ui.unread = 0;
  });

  const core = { options, chat, ui, agent, activity, canShow, visible, start, openPanel, closePanel, minimize, toggleExpand, ask };
  const unregister = registerCore(core satisfies CoreHandle);
  onBeforeUnmount(unregister);
  if (hostSettings.pendingOpen) {
    hostSettings.pendingOpen = false;
    openPanel();
  }
  return core;
}

/** The widget the host API talks to (the latest mounted owner), for a BiatecChatInline to share. */
export function sharedCore(): WidgetCore | null {
  // Only createCore registers, so the handle is a full core.
  return currentCore() as WidgetCore | null;
}
