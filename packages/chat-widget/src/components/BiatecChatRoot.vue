<script setup lang="ts">
import { ref, computed, nextTick, watch, watchEffect, onMounted, onBeforeUnmount } from "vue";
import type { ResolvedOptions } from "../options";
import ChatLauncher from "./ChatLauncher.vue";
import ChatPanel, { type PanelMode } from "./ChatPanel.vue";
import MinimizedBar from "./shell/MinimizedBar.vue";
import ChatView from "./chat/ChatView.vue";
import PermissionsDialog from "./agent/PermissionsDialog.vue";
import AgentNotices from "./agent/AgentNotices.vue";
import { evidenceDownloadUrl } from "../transport/types";
import ActivityTimeline from "./activity/ActivityTimeline.vue";
import ExportView from "./export/ExportView.vue";
import { agentModeOf, AGENT_MODE_LABEL } from "./shell/agentMode";
import { createCore, pageContext, type WidgetCore, type WidgetServices } from "../api/core";
import { hostSettings } from "../api/controller";
import { resolveTheme, watchTheme, type ResolvedTheme } from "../theme";
import { resolvedLocale, setLocale, t } from "../i18n";

const props = defineProps<{
  options: ResolvedOptions;
  services?: WidgetServices;
  shadow?: boolean;
  /** Share an existing widget's session and shell state (BiatecChatInline next to the floating widget). */
  core?: WidgetCore;
  /** Render the panel in the host's layout instead of the floating launcher and panel. */
  inline?: boolean;
}>();

// Transport and auth are created here, but nothing touches the network until the panel is first shown.
const core = props.core ?? createCore(props.options, props.services);
const { chat, ui, agent, activity } = core;
const options = core.options;
const inline = !!props.inline;

const rootEl = ref<HTMLElement | null>(null);

// Theme: the plugin option, or what the host set through setTheme(), applied without remount.
const themeChoice = computed(() => hostSettings.theme ?? options.theme);
const theme = ref<ResolvedTheme>(resolveTheme({ theme: themeChoice.value, hostDarkClass: options.hostDarkClass }));
let stopTheme = () => {};
// Locale: the plugin option, or setLocale(); vue-i18n re-renders every string in place.
const lang = computed(() => hostSettings.locale ?? options.locale);
setLocale(lang.value);
/** The locale the catalog actually shows (de falls back to en): what screen readers must announce. */
const shownLang = computed(() => resolvedLocale(lang.value));

// Without a shadow root (host mounts BiatecChatRoot itself) the tokens apply to the root element instead of :host.
// Known synchronously from the `shadow` prop so the first render already has its tokens; onMounted confirms it.
const standalone = ref(!props.shadow);
function shadowHost(): Element | null {
  const root = rootEl.value?.getRootNode();
  return typeof ShadowRoot !== "undefined" && root instanceof ShadowRoot ? root.host : null;
}
// The :host([data-theme]) rules in the shadow root read the attribute from the host element.
watchEffect(() => {
  shadowHost()?.setAttribute("data-theme", theme.value);
});

/** Below 640 px an open floating or workspace panel is full screen. */
const small = ref(false);
let mq: MediaQueryList | null = null;
const onMq = () => (small.value = !!mq?.matches);
function measure() {
  if (typeof window !== "undefined" && window.matchMedia) small.value = !!window.matchMedia("(max-width: 639.98px)").matches;
}

const launcher = ref<InstanceType<typeof ChatLauncher> | null>(null);
const minbar = ref<InstanceType<typeof MinimizedBar> | null>(null);
const panelId = inline ? "biatec-chat-inline-panel" : "biatec-chat-panel";

/** This root draws the floating UI only while no inline panel is mounted, and never in `mode: 'inline'`. */
const floatingUi = computed(() => !inline && ui.inline === 0 && options.mode !== "inline");
const showPanel = computed(() => inline || (floatingUi.value && ui.open && !ui.minimized));
const showMinBar = computed(() => floatingUi.value && ui.open && ui.minimized);
const showLauncher = computed(() => floatingUi.value && !ui.open);
// An inline panel always stays in the host's column: it never becomes the workspace (its header has no Expand).
const panelMode = computed<PanelMode>(() => (inline ? "inline" : ui.expanded ? "workspace" : "floating"));
const fullscreen = computed(() => panelMode.value === "workspace" || (panelMode.value === "floating" && small.value));
const modal = computed(() => showPanel.value && fullscreen.value);

const agentMode = computed(() => agentModeOf(chat.session.value));
const statusLine = computed(() => {
  const mode = t(AGENT_MODE_LABEL[agentMode.value]);
  if (chat.busy.value) return `${mode} · ${t("shell.replying")}`;
  return ui.unread ? `${mode} · ${t("shell.unread", { count: ui.unread })}` : mode;
});

function composer(): HTMLTextAreaElement | null {
  return rootEl.value?.querySelector<HTMLTextAreaElement>(".bc-panel textarea") ?? null;
}
function close() {
  core.closePanel();
}
function minimize() {
  core.minimize();
}
// Focus follows the shell: composer when the panel appears, Restore when minimized, the launcher when closed.
watch(
  () => ui.focusTick,
  async () => {
    await nextTick();
    composer()?.focus();
  },
);
watch([showPanel, showMinBar, showLauncher], async ([, bar, launch], [wasPanel, wasBar]) => {
  // The launcher comes back after the panel or the minimized bar (host close() from the bar): focus goes with it.
  if (!wasPanel && !(wasBar && launch)) return;
  await nextTick();
  if (bar) minbar.value?.focus();
  else if (launch) launcher.value?.focus();
});
// Probe on first show: the floating panel calls start() from openPanel; an inline panel is shown when mounted.
onMounted(() => {
  standalone.value = !shadowHost();
  if (typeof window !== "undefined" && window.matchMedia) {
    mq = window.matchMedia("(max-width: 639.98px)");
    onMq();
    mq.addEventListener?.("change", onMq);
  }
  watch(
    themeChoice,
    (c) => {
      stopTheme();
      stopTheme = watchTheme({ theme: c, hostDarkClass: options.hostDarkClass }, (v) => (theme.value = v));
    },
    { immediate: true },
  );
  if (inline) {
    ui.inline++;
    ui.unread = 0;
    void core.start();
  }
});
onBeforeUnmount(() => {
  stopTheme();
  mq?.removeEventListener?.("change", onMq);
  if (inline) ui.inline = Math.max(0, ui.inline - 1);
});

const FOCUSABLE = "button:not([disabled]), [href], input:not([disabled]), select, textarea, [tabindex]:not([tabindex='-1'])";

function inWidget(e: Event): boolean {
  return !!rootEl.value && e.composedPath().includes(rootEl.value);
}

const HOST_MODAL = "[role=dialog][aria-modal=true], dialog[open]:modal";
function isHostModal(el: Element): boolean {
  try {
    return el.matches(HOST_MODAL);
  } catch {
    // Engines without :modal: fall back to the ARIA form and open native dialogs.
    return el.matches("[role=dialog][aria-modal=true], dialog[open]");
  }
}
/** A modal dialog of the host page (not ours) is open: it owns Esc and Tab. */
function hostModalOpen(): boolean {
  if (typeof document === "undefined") return false;
  const found = [...document.querySelectorAll("[role=dialog][aria-modal=true], dialog[open]")].filter(isHostModal);
  return found.some((el) => !rootEl.value?.contains(el) && !el.closest("[data-biatec-chat-host]") && el.getClientRects().length > 0);
}
function insideHostDialog(n: Node | null): boolean {
  for (let el = n instanceof Element ? n : (n?.parentElement ?? null); el; el = el.parentElement)
    if (!rootEl.value?.contains(el) && isHostModal(el)) return true;
  return false;
}
function fromHostDialog(e: Event): boolean {
  return e.composedPath().some((n) => n instanceof Element && !!rootEl.value && !rootEl.value.contains(n) && isHostModal(n));
}
/** Decided in the capture phase, before any host handler can remove its dialog. */
const hostOwned = new WeakSet<Event>();
function onDocKeydownCapture(e: KeyboardEvent) {
  if (fromHostDialog(e) || insideHostDialog(document.activeElement) || (!inWidget(e) && hostModalOpen())) hostOwned.add(e);
}

// Closing the permissions dialog returns focus to the status pill when its opener is gone (a banner that hid).
watch(
  () => ui.permissions,
  async (open) => {
    if (open || !rootEl.value) return;
    await nextTick();
    const active = (rootEl.value.getRootNode() as Document | ShadowRoot).activeElement;
    if (!active || !rootEl.value.contains(active)) rootEl.value.querySelector<HTMLElement>("[data-testid=status-pill]")?.focus();
  },
);
/** The aside sits beside the conversation only in a workspace wider than 640 px; otherwise it is a sheet. */
const asideDocked = computed(() => panelMode.value === "workspace" && !small.value);

/** Esc order: permissions confirm step, the dialog, then the Activity sheet, then (floating and workspace only) the panel itself. */
function onEscape(e: KeyboardEvent) {
  if (e.defaultPrevented) return;
  if (inline && !inWidget(e)) return;
  // Esc meant for a host dialog (pressed inside it, or while it is open and focus is outside the widget).
  if (hostOwned.has(e) || fromHostDialog(e) || (!inWidget(e) && hostModalOpen())) return;
  if (ui.permissions) {
    agent.escape();
    return;
  }
  if (ui.activity && !asideDocked.value) {
    ui.activity = false;
    return;
  }
  if (inline) return;
  // Esc must not throw away a half-written message.
  if (composer()?.value.trim()) return;
  close();
}

function onDocKeydown(e: KeyboardEvent) {
  if (e.key === "Escape") {
    onEscape(e);
    return;
  }
  if (e.key !== "Tab" || !modal.value || !rootEl.value) return;
  // The full-screen trap yields while a host modal dialog is open over it.
  if (hostOwned.has(e) || fromHostDialog(e) || hostModalOpen()) return;
  const items = [...rootEl.value.querySelectorAll<HTMLElement>(FOCUSABLE)];
  const first = items[0];
  const last = items[items.length - 1];
  if (!first || !last) return;
  // Inside a shadow root document.activeElement is the host, so resolve focus through the root node.
  const active = (rootEl.value.getRootNode() as Document | ShadowRoot).activeElement;
  if (!active || !rootEl.value.contains(active)) {
    e.preventDefault();
    first.focus();
  } else if (e.shiftKey && active === first) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && active === last) {
    e.preventDefault();
    first.focus();
  }
}

// Keys are handled while a panel is on screen: Esc wherever focus is (floating), Tab trapped while full screen.
watch(
  showPanel,
  (v) => {
    if (typeof document === "undefined") return;
    if (v) {
      measure();
      document.addEventListener("keydown", onDocKeydownCapture, true);
      document.addEventListener("keydown", onDocKeydown);
    } else {
      document.removeEventListener("keydown", onDocKeydownCapture, true);
      document.removeEventListener("keydown", onDocKeydown);
    }
  },
  { immediate: true },
);
onBeforeUnmount(() => {
  if (typeof document !== "undefined") {
    document.removeEventListener("keydown", onDocKeydownCapture, true);
    document.removeEventListener("keydown", onDocKeydown);
  }
});
</script>

<template>
  <div
    ref="rootEl"
    class="biatec-chat"
    :data-theme="theme"
    :data-standalone="standalone ? '' : undefined"
    :data-inline="inline ? '' : undefined"
    :lang="shownLang"
  >
    <ChatPanel
      v-if="showPanel"
      :id="panelId"
      :mode="panelMode"
      :fullscreen="fullscreen"
      :modal="modal"
      :inline="inline"
      :tier="chat.session.value?.tier ?? null"
      :email="chat.session.value?.email"
      :primary-address="chat.session.value?.primaryAddress"
      :signing-in="chat.phase.value === 'opening' || chat.phase.value === 'waiting'"
      :agent-mode="agentMode"
      :activity-open="ui.activity"
      :permissions-open="ui.permissions"
      :notice="ui.notice"
      @close="close"
      @minimize="minimize"
      @toggle-expand="core.toggleExpand()"
      @sign-in="chat.signIn()"
      @sign-out="chat.signOut()"
      @open-permissions="ui.permissions ? agent.close() : agent.open()"
      @toggle-activity="ui.activity = !ui.activity"
    >
      <ChatView
        :messages="chat.messages"
        :tier="chat.tier.value"
        :suggestions="options.suggestions"
        :tenant-suggestions="chat.session.value?.suggestions"
        :busy="chat.busy.value"
        :phase="chat.phase.value"
        :announcement="chat.announcement.value"
        :session-expires-at="chat.session.value?.expiresAt ?? null"
        :agent-mode="agentMode"
        :context-label="pageContext(options)?.product ?? null"
        @send="(text, opts) => chat.send(text, opts)"
        @sign-in="chat.signIn({ scope: $event })"
        @confirm-preview="chat.confirmPreview($event)"
        @cancel-preview="chat.cancelPreview($event)"
        @authorize-preview="(id, scope) => chat.authorizePreview(id, scope)"
      />
      <template #banner>
        <AgentNotices :agent="agent" />
      </template>
      <template #aside>
        <ActivityTimeline
          :entries="activity.entries"
          :agent="agent"
          :mode="agentMode"
          :docked="asideDocked"
          :evidence-url="evidenceDownloadUrl(options.backendUrl)"
          @close="ui.activity = false"
        />
        <!-- T-094: the export view and erasure. After an erase the cookie session is on the returned conversation. -->
        <ExportView
          v-if="core.retention && chat.session.value"
          :api="core.retention"
          :signed-in="chat.session.value.tier === 'Authenticated'"
          :revision="activity.entries.length"
          @erased="core.resetConversation()"
        />
      </template>
      <template #dialog>
        <PermissionsDialog
          :agent="agent"
          :open="ui.permissions"
          :open-previews="activity.openPreviews.value.length"
          @sign-in="agent.close(); chat.signIn()"
        />
      </template>
    </ChatPanel>
    <MinimizedBar v-if="showMinBar" ref="minbar" :status-line="statusLine" :controls="panelId" @restore="core.openPanel()" />
    <ChatLauncher v-if="showLauncher" ref="launcher" :controls="panelId" :unread="ui.unread" @open="core.openPanel()" />
  </div>
</template>
