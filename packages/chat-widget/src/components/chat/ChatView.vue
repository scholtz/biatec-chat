<script setup lang="ts">
import { ref, computed, watch, nextTick } from "vue";
import type { SessionTier } from "@biatec/chat-protocol";
import type { ChatMessage, SignInPhase } from "./useChat";
import { promptsFor } from "./suggestions";

const props = defineProps<{
  messages: ChatMessage[];
  tier: SessionTier;
  suggestions: "default" | string[];
  busy: boolean;
  phase: SignInPhase;
  announcement: string;
}>();
const emit = defineEmits<{ send: [text: string]; signIn: [scope: string | null] }>();

const draft = ref("");
const input = ref<HTMLTextAreaElement | null>(null);
const list = ref<HTMLElement | null>(null);
const prompts = computed(() => promptsFor(props.tier, props.suggestions));

function submit(text = draft.value) {
  if (!text.trim() || props.busy) return;
  draft.value = "";
  emit("send", text);
}
function onKey(e: KeyboardEvent) {
  if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
    e.preventDefault();
    submit();
  }
}

// Keep the newest text in view while it streams.
watch(
  () => props.messages.map((m) => m.text.length + m.tools.length).join(","),
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

const phaseText = computed(
  () =>
    ({
      idle: "",
      opening: "Opening the sign-in window...",
      waiting: "Finish signing in in the popup window.",
      blocked: "Your browser blocked the sign-in window. Allow popups for this site and try again.",
      failed: "Sign-in did not complete. Try again.",
      "no-cookies":
        "Sign-in needs cookies for this chat. Your browser may be blocking third-party cookies, or this website's origin is not registered.",
      "signout-failed": "Sign-out did not complete. You are still signed in; try again.",
    })[props.phase],
);
const signingIn = computed(() => props.phase === "opening" || props.phase === "waiting");
</script>

<template>
  <div class="bc-chat">
    <div ref="list" class="bc-messages" role="log" aria-label="Conversation" aria-live="off">
      <div v-if="!messages.length" class="bc-empty">
        <p class="bc-placeholder">Ask about Biatec, or pick a suggestion.</p>
        <ul class="bc-suggestions" aria-label="Suggested prompts">
          <li v-for="p in prompts" :key="p.prompt">
            <button type="button" class="bc-chip" :disabled="busy" @click="submit(p.prompt)">{{ p.label }}</button>
          </li>
        </ul>
      </div>
      <div v-for="m in messages" :key="m.id" class="bc-msg" :class="`bc-msg-${m.role}`" :data-role="m.role">
        <ul v-if="m.tools.length" class="bc-tools">
          <li v-for="t in m.tools" :key="t.id" class="bc-tool" :data-state="t.state">{{ t.label }}</li>
        </ul>
        <!-- Text only: model output is never interpreted as HTML. -->
        <p v-if="m.text" class="bc-text">{{ m.text }}</p>
        <p v-else-if="m.streaming && !m.tools.length && !m.signIn" class="bc-typing" aria-hidden="true">...</p>
        <div v-if="m.signIn" class="bc-signin-card">
          <p>{{ m.signIn.description ?? (m.signIn.reason === "ElevateScope" ? "More permission is needed for this." : "Sign in to continue.") }}</p>
          <button type="button" class="bc-primary" :disabled="signingIn" @click="emit('signIn', m.signIn.reason === 'ElevateScope' ? m.signIn.scope : null)">
            {{ m.signIn.reason === "ElevateScope" ? "Authorize" : "Sign in" }}
          </button>
        </div>
        <p v-if="m.error" class="bc-error" role="alert">{{ m.error }}</p>
      </div>
    </div>

    <p v-if="phaseText" class="bc-signin-status" :class="{ 'bc-bad': ['blocked', 'failed', 'no-cookies', 'signout-failed'].includes(phase) }" role="status">{{ phaseText }}</p>
    <!-- Announces each finished reply once, instead of every streamed token. -->
    <div class="bc-sr-only" role="status" aria-live="polite" aria-atomic="true" data-testid="announcer">{{ announcement }}</div>

    <form class="bc-composer" @submit.prevent="submit()">
      <label class="bc-sr-only" for="biatec-chat-input">Message</label>
      <textarea
        id="biatec-chat-input"
        ref="input"
        v-model="draft"
        rows="1"
        placeholder="Type a message"
        @keydown="onKey"
      />
      <button type="submit" class="bc-primary" :disabled="busy || !draft.trim()">Send</button>
    </form>
  </div>
</template>
