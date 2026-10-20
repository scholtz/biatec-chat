<script setup lang="ts">
import { ref, computed, watch, nextTick, onBeforeUnmount, type Component } from "vue";
import type { SessionTier } from "@biatec/chat-protocol";
import {
  ArrowUp,
  BookOpen,
  Check,
  Copy,
  Droplets,
  Gauge,
  Layers,
  Loader,
  RotateCcw,
  Shield,
  ShieldCheck,
  Sparkles,
  ArrowLeftRight,
  TriangleAlert,
  Wallet,
} from "lucide-vue-next";
import type { ChatMessage, SignInPhase } from "./useChat";
import { promptsFor, type PromptIcon } from "./suggestions";
import { parseBlocks, safeHttpUrl } from "./format";
import { AGENT_MODE_LABEL, type AgentModeDisplay } from "../shell/agentMode";
import { BaiSymbol } from "../../ui";
import SafeCard from "../cards/SafeCard.vue";
import HoldingsCard from "../data/HoldingsCard.vue";
import ActionPreviewCard from "../cards/ActionPreviewCard.vue";
import ReceiptCard from "../cards/ReceiptCard.vue";
import { RECEIPT_STATUSES } from "./previews";
import { t } from "../../i18n";

const props = withDefaults(
  defineProps<{
    messages: ChatMessage[];
    tier: SessionTier;
    suggestions: "default" | string[];
    busy: boolean;
    phase: SignInPhase;
    announcement: string;
    /** The session's absolute expiry; the preview cards warn when it is close. */
    sessionExpiresAt?: string | null;
    agentMode?: AgentModeDisplay;
    /** The host page context, when the host set one. The chip only exists while this is set. */
    contextLabel?: string | null;
  }>(),
  { agentMode: "analyze", contextLabel: null, sessionExpiresAt: null },
);
const emit = defineEmits<{
  send: [text: string, opts?: { includeContext?: boolean }];
  signIn: [scope: string | null];
  confirmPreview: [previewId: string];
  cancelPreview: [previewId: string];
  authorizePreview: [previewId: string, scope: string];
}>();

const draft = ref("");
const input = ref<HTMLTextAreaElement | null>(null);
const list = ref<HTMLElement | null>(null);
const prompts = computed(() => promptsFor(props.tier, props.suggestions));
const includeContext = ref(true);
const copiedId = ref<number | null>(null);
let copiedTimer: ReturnType<typeof setTimeout> | undefined;

const ICONS: Record<PromptIcon, Component> = {
  book: BookOpen,
  layers: Layers,
  shield: Shield,
  sparkles: Sparkles,
  swap: ArrowLeftRight,
  droplets: Droplets,
  wallet: Wallet,
  gauge: Gauge,
};
const iconFor = (key: PromptIcon): Component => ICONS[key];

function submit(text = draft.value) {
  if (!text.trim() || props.busy) return;
  const withoutContext = !!props.contextLabel && !includeContext.value;
  draft.value = "";
  includeContext.value = true;
  growInput();
  emit("send", text, withoutContext ? { includeContext: false } : undefined);
}
function onKey(e: KeyboardEvent) {
  // Enter sends, Shift+Enter is a newline, and Enter that confirms an IME composition does nothing.
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing && e.keyCode !== 229) {
    e.preventDefault();
    submit();
  }
}
function growInput() {
  void nextTick(() => {
    const el = input.value;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, 140)}px`;
  });
}

async function copy(m: ChatMessage) {
  try {
    await navigator.clipboard.writeText(m.text);
  } catch {
    return; // clipboard blocked: say nothing rather than claim a copy that did not happen
  }
  copiedId.value = m.id;
  clearTimeout(copiedTimer);
  copiedTimer = setTimeout(() => (copiedId.value = null), 1500);
}
function retry(index: number) {
  for (let i = index - 1; i >= 0; i--) {
    const prev = props.messages[i]!;
    if (prev.role === "user") {
      if (!props.busy) emit("send", prev.text);
      return;
    }
  }
}
onBeforeUnmount(() => clearTimeout(copiedTimer));

// Keep the newest text in view while it streams.
watch(
  () => props.messages.map((m) => m.text.length + m.tools.length + (m.safeCards?.length ?? 0) + (m.dataCards?.length ?? 0) + (m.previews?.length ?? 0)).join(","),
  async () => {
    await nextTick();
    if (list.value) list.value.scrollTop = list.value.scrollHeight;
  },
);
// After a sign-in finishes the popup is closed, so put the focus back where the user was typing.
watch(
  () => props.phase,
  (now, before) => {
    if ((before === "waiting" || before === "opening") && now === "idle") input.value?.focus();
  },
);

const PHASE_KEY: Record<SignInPhase, string> = {
  idle: "",
  opening: "chat.phaseOpening",
  waiting: "chat.phaseWaiting",
  blocked: "chat.phaseBlocked",
  failed: "chat.phaseFailed",
  "no-cookies": "chat.phaseNoCookies",
  "signout-failed": "chat.phaseSignoutFailed",
};
const phaseText = computed(() => (PHASE_KEY[props.phase] ? t(PHASE_KEY[props.phase]) : ""));
const signingIn = computed(() => props.phase === "opening" || props.phase === "waiting");
const modeLabel = computed(() => t(AGENT_MODE_LABEL[props.agentMode]));
const TOOL_STATE_KEY: Record<string, string> = { Started: "chat.toolStarted", Succeeded: "chat.toolSucceeded", Failed: "chat.toolFailed" };
const toolState = (s: string) => (TOOL_STATE_KEY[s] ? t(TOOL_STATE_KEY[s]!) : "");
const toolIcon = (s: string): Component => (s === "Succeeded" ? Check : s === "Failed" ? TriangleAlert : Loader);
const cardText = (m: ChatMessage) => m.signIn?.description ?? t(m.signIn?.reason === "ElevateScope" ? "chat.elevateCard" : "chat.signInCard");
const showThinking = (m: ChatMessage) => m.role === "assistant" && m.streaming && !m.text && !m.signIn && !m.error && !m.previews?.length;
</script>

<template>
  <div class="bc-chat">
    <div ref="list" class="bc-messages" role="log" :aria-label="t('chat.conversation')" aria-live="off">
      <div v-if="!messages.length" class="bc-empty">
        <h2 class="bc-empty-title">{{ t("chat.emptyTitle") }}</h2>
        <p class="bc-empty-intro">{{ t("chat.emptyIntro") }}</p>
        <ul class="bc-suggestions" :aria-label="t('chat.suggestedPrompts')">
          <li v-for="p in prompts" :key="p.prompt">
            <button type="button" class="bc-chip bc-suggest" :disabled="busy" @click="submit(p.prompt)">
              <component :is="iconFor(p.icon)" class="bc-suggest-icon" :size="18" :stroke-width="2" aria-hidden="true" />
              <span class="bc-suggest-label">{{ p.label }}</span>
            </button>
          </li>
        </ul>
        <p class="bc-shield-note" data-testid="shield-note">
          <ShieldCheck :size="16" :stroke-width="2" aria-hidden="true" />
          <span>{{ t("chat.shieldNote", { mode: modeLabel }) }}</span>
        </p>
      </div>

      <div v-for="(m, i) in messages" :key="m.id" class="bc-msg" :class="`bc-msg-${m.role}`" :data-role="m.role">
        <template v-if="m.role === 'user'">
          <p class="bc-text">{{ m.text }}</p>
        </template>
        <template v-else>
          <span class="bc-avatar"><BaiSymbol :size="28" variant="small" /></span>
          <div class="bc-msg-body">
            <ul v-if="m.tools.length" class="bc-tools">
              <li v-for="tl in m.tools" :key="tl.id" class="bc-tool" :data-state="tl.state">
                <component :is="toolIcon(tl.state)" class="bc-tool-icon" :size="14" :stroke-width="2" aria-hidden="true" />
                <span class="bc-tool-label">{{ tl.label }}</span>
                <span class="bc-sr-only">{{ toolState(tl.state) }}</span>
              </li>
            </ul>
            <!-- Text only: model output is never interpreted as HTML. -->
            <div v-if="m.text" class="bc-text">
              <template v-for="(b, bi) in parseBlocks(m.text)" :key="bi">
                <p v-if="b.kind === 'p'" class="bc-para">{{ b.text }}</p>
                <ul v-else class="bc-bullets">
                  <li v-for="(item, ii) in b.items" :key="ii">{{ item }}</li>
                </ul>
              </template>
            </div>
            <!-- Safe cards come only from safe_card events (structured tool results), never from model text. -->
            <SafeCard v-for="(sc, si) in m.safeCards ?? []" :key="si" :card="sc" />
            <!-- Data cards come only from data_card events (structured tool results), never from model text. -->
            <HoldingsCard v-for="(dc, di) in m.dataCards ?? []" :key="di" :card="dc" />
            <!-- Preview cards come only from preview_card events (structured backend data), never from model text. -->
            <template v-for="pv in m.previews ?? []" :key="pv.preview.previewId">
              <ReceiptCard v-if="RECEIPT_STATUSES.includes(pv.preview.status)" :preview="pv.preview" />
              <ActionPreviewCard
                v-else
                :preview="pv.preview"
                :consent="pv.consent"
                :session-expires-at="sessionExpiresAt"
                :submitting="pv.submitting"
                @confirm="emit('confirmPreview', $event)"
                @cancel="emit('cancelPreview', $event)"
                @authorize="(scope) => emit('authorizePreview', pv.preview.previewId, scope)"
              />
              <div v-if="pv.note" class="bc-preview-note">
                <p class="bc-error" role="alert">{{ pv.note }}</p>
                <button v-if="pv.needsSignIn" type="button" class="bc-btn" data-variant="primary" :disabled="signingIn" @click="emit('signIn', null)">{{ t("chat.previewSignIn") }}</button>
              </div>
            </template>
            <p v-if="showThinking(m)" class="bc-thinking" role="status">
              <span class="bc-pulse" aria-hidden="true"><i /><i /><i /></span>
              <span>{{ t("chat.thinking") }}</span>
            </p>
            <div v-if="m.signIn && m.signIn.unavailable" class="bc-signin-card" data-variant="unavailable">
              <p>{{ cardText(m) }}</p>
              <p class="bc-signin-note">{{ t("chat.signInUnavailableTitle") }} {{ t("chat.signInUnavailableText") }}</p>
              <a
                v-if="safeHttpUrl(m.signIn.url)"
                class="bc-btn bc-signin-link"
                data-variant="secondary"
                :href="safeHttpUrl(m.signIn.url)!"
                target="_blank"
                rel="noopener noreferrer"
              >{{ t("chat.signInUnavailableLink") }}</a>
            </div>
            <div v-else-if="m.signIn" class="bc-signin-card">
              <p>{{ cardText(m) }}</p>
              <button
                type="button"
                class="bc-btn"
                data-variant="primary"
                :disabled="signingIn"
                @click="emit('signIn', m.signIn.reason === 'ElevateScope' ? m.signIn.scope : null)"
              >
                {{ t(m.signIn.reason === "ElevateScope" ? "chat.authorizeButton" : "chat.signInButton") }}
              </button>
            </div>
            <p v-if="m.error" class="bc-error" role="alert">{{ m.error }}</p>
            <ul v-if="m.followUps?.length && !m.streaming" class="bc-followups" :aria-label="t('chat.followUps')">
              <li v-for="f in m.followUps" :key="f.id">
                <button type="button" class="bc-chip bc-followup" :disabled="busy" @click="submit(f.prompt)">{{ f.label }}</button>
              </li>
            </ul>
            <div v-if="!m.streaming && (m.text || m.error)" class="bc-actions">
              <button v-if="m.text" type="button" class="bc-action" :aria-label="t('chat.copyLabel')" @click="copy(m)">
                <component :is="copiedId === m.id ? Check : Copy" :size="14" :stroke-width="2" aria-hidden="true" />
                <span data-testid="copy-label">{{ copiedId === m.id ? t("chat.copied") : t("chat.copy") }}</span>
              </button>
              <button type="button" class="bc-action" data-testid="retry" :aria-label="t('chat.retryLabel')" :disabled="busy" @click="retry(i)">
                <RotateCcw :size="14" :stroke-width="2" aria-hidden="true" />
                <span>{{ t("chat.retry") }}</span>
              </button>
            </div>
          </div>
        </template>
      </div>
    </div>

    <p v-if="phaseText" class="bc-signin-status" :class="{ 'bc-bad': ['blocked', 'failed', 'no-cookies', 'signout-failed'].includes(phase) }" role="status">{{ phaseText }}</p>
    <!-- Announces each finished reply once, instead of every streamed token. -->
    <div class="bc-sr-only" role="status" aria-live="polite" aria-atomic="true" data-testid="announcer">{{ announcement }}</div>

    <form class="bc-composer" @submit.prevent="submit()">
      <div class="bc-composer-box">
        <label class="bc-sr-only" for="biatec-chat-input">{{ t("chat.composerLabel") }}</label>
        <textarea
          id="biatec-chat-input"
          ref="input"
          v-model="draft"
          rows="2"
          :placeholder="t('chat.placeholder')"
          @keydown="onKey"
          @input="growInput"
        />
        <div class="bc-composer-bar">
          <button
            v-if="contextLabel"
            type="button"
            class="bc-context"
            data-testid="context-chip"
            :aria-pressed="includeContext"
            :title="includeContext ? t('chat.contextOn') : t('chat.contextOff')"
            @click="includeContext = !includeContext"
          >
            {{ t("chat.contextChip", { context: contextLabel }) }}
          </button>
          <button type="submit" class="bc-send" data-testid="send" :aria-label="t('chat.send')" :disabled="busy || !draft.trim()">
            <ArrowUp :size="20" :stroke-width="2" aria-hidden="true" />
          </button>
        </div>
      </div>
      <p class="bc-composer-footer">{{ t("chat.footer") }}</p>
    </form>
  </div>
</template>
