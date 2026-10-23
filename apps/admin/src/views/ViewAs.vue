<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { api } from "../api";
import StateBlock from "../components/StateBlock.vue";
import StatusBadge from "../components/StatusBadge.vue";
import { formatDateTime, formatEur, formatTime, shortId } from "../format";
import { useLoader } from "../loader";
import { session } from "../session";

const imp = computed(() => session.me?.impersonation ?? null);
const selected = ref<string | null>(null);

const convs = useLoader(() => api.viewConversations());
const transcript = useLoader(() => api.viewMessages(selected.value as string));

async function init() {
  if (!imp.value) return;
  await convs.load();
  const first = convs.data.value?.[0];
  if (first && !selected.value) selected.value = first.id;
}
onMounted(init);
// A new impersonation (also of another user, replacing the running one) starts from a clean view.
watch(() => imp.value && `${imp.value.tenantId}|${imp.value.sub}|${imp.value.startedAt}`, (v, old) => {
  if (v && v !== old) {
    selected.value = null;
    init();
  }
});
watch(selected, (id) => {
  if (id) transcript.load();
});

function roleClass(role: string): string {
  return `role-${role.toLowerCase()}`;
}
</script>

<template>
  <section aria-labelledby="h-view">
    <h1 id="h-view">View as user</h1>
    <div v-if="!imp" class="card empty-card" data-test="no-impersonation">
      <p>There is no active impersonation. Start one from a user to read their conversations.</p>
      <a class="btn btn-primary" href="#/users">Go to Users</a>
    </div>
    <template v-else>
      <p class="notice notice-info" data-test="readonly-notice">
        You are viewing this conversation as the user. Nothing can be sent, confirmed or signed from here.
      </p>
      <div class="split">
        <aside class="card flush" aria-label="Conversations">
          <h2 class="pad">Conversations</h2>
          <StateBlock :loading="convs.loading.value" :error="convs.error.value" :empty="!!convs.data.value && convs.data.value.length === 0" empty-text="This user has no conversations." :rows="5" @retry="init">
            <ul class="conv-list">
              <li v-for="c in convs.data.value" :key="c.id">
                <button type="button" class="conv-btn" :aria-current="selected === c.id ? 'true' : undefined" data-test="conv" @click="selected = c.id">
                  <span class="mono">{{ shortId(c.id) }}</span>
                  <span class="small muted">{{ formatDateTime(c.lastMessageAt ?? c.createdAt) }}</span>
                  <span class="small muted">{{ c.messages }} messages, {{ c.previews }} previews{{ c.endedAt ? ", ended" : "" }}</span>
                </button>
              </li>
            </ul>
          </StateBlock>
        </aside>

        <div class="card transcript-card">
          <StateBlock :loading="transcript.loading.value && !!selected" :error="transcript.error.value" :empty="!selected" empty-text="Select a conversation." :rows="6" @retry="transcript.load">
            <template v-if="transcript.data.value && selected">
              <h2 class="mono">Conversation {{ shortId(transcript.data.value.conversation.id, 12) }}</h2>
              <p v-if="!transcript.data.value.messages.length" class="empty">No messages.</p>
              <p v-if="transcript.data.value.truncated" class="notice notice-info" role="note" data-test="truncated">
                This conversation is longer than what is shown: only the newest {{ transcript.data.value.messages.length }} messages are included.
              </p>
              <ol class="messages" data-test="messages">
                <li v-for="m in transcript.data.value.messages" :key="m.seq" class="msg" :class="roleClass(m.role)">
                  <div class="msg-head">
                    <span class="role-name">{{ m.role }}</span>
                    <time :datetime="m.createdAt" class="muted small">{{ formatTime(m.createdAt) }}</time>
                    <span v-for="t in m.toolNames" :key="t" class="chip" data-test="tool">{{ t }}</span>
                  </div>
                  <div class="msg-text" data-test="msg-text">{{ m.content }}</div>
                </li>
              </ol>

              <div v-if="transcript.data.value.previews.length" class="previews">
                <h3>Action previews</h3>
                <article v-for="p in transcript.data.value.previews" :key="p.id" class="preview">
                  <header>
                    <strong class="mono">{{ p.capability }}</strong>
                    <StatusBadge :status="p.status" />
                    <span v-if="p.needsConsent" class="chip">needs consent</span>
                  </header>
                  <p class="small muted">
                    Predicted value {{ formatEur(p.predictedValueEur) }}<template v-if="p.expiresAt">, expires {{ formatDateTime(p.expiresAt) }}</template>
                  </p>
                  <ol v-if="p.steps.length" class="steps">
                    <li v-for="s in p.steps" :key="s.seq">
                      <span>{{ s.seq }}. {{ s.kind }}</span> <StatusBadge :status="s.status" />
                      <span v-if="s.network" class="muted small"> {{ s.network }}</span>
                      <span v-for="tx in s.txIds" :key="tx" class="mono small tx" :title="tx">{{ shortId(tx, 12) }}</span>
                    </li>
                  </ol>
                </article>
              </div>
            </template>
          </StateBlock>
        </div>
      </div>
    </template>
  </section>
</template>
