<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { api } from "../api";
import CopyButton from "../components/CopyButton.vue";
import ImpersonateModal from "../components/ImpersonateModal.vue";
import StateBlock from "../components/StateBlock.vue";
import { formatDateTime, formatNumber, shortId, shortMiddle } from "../format";
import { useLoader } from "../loader";
import { route } from "../router";

const tenant = computed(() => route.value.query.get("tenant") ?? "");
const sub = computed(() => route.value.query.get("sub") ?? "");
const showModal = ref(false);

const { data, loading, error, load } = useLoader(() => api.user(tenant.value, sub.value));
onMounted(() => {
  if (tenant.value && sub.value) load();
});
watch([tenant, sub], () => {
  if (tenant.value && sub.value) load();
});

const tiles = computed(() => {
  const u = data.value?.user;
  if (!u) return [];
  return [
    { label: "Conversations", value: u.conversations },
    { label: "Messages", value: u.messages },
    { label: "Previews", value: u.previews },
    { label: "Active sessions", value: u.activeSessions },
    { label: "Sign-ins", value: u.signInCount },
  ];
});
</script>

<template>
  <section aria-labelledby="h-user">
    <p><a href="#/users">&larr; Users</a></p>
    <div class="page-head">
      <h1 id="h-user">User</h1>
      <button v-if="data" type="button" class="btn btn-primary" data-test="impersonate" @click="showModal = true">Impersonate</button>
    </div>
    <p v-if="!tenant || !sub" class="empty">No user selected. Pick one from the <a href="#/users">users list</a>.</p>
    <StateBlock v-else :loading="loading && !data" :error="error" :rows="5" @retry="load">
      <template v-if="data">
        <div class="card">
          <h2 class="mono" :title="data.user.sub">{{ shortMiddle(data.user.sub, 24) }}</h2>
          <dl class="kv">
            <dt>Subject</dt><dd class="mono">{{ data.user.sub }} <CopyButton :value="data.user.sub" label="subject" /></dd>
            <dt>Tenant</dt><dd>{{ data.user.tenantId }}</dd>
            <dt>First seen</dt><dd>{{ formatDateTime(data.user.firstSeenAt) }}</dd>
            <dt>Last seen</dt><dd>{{ formatDateTime(data.user.lastSeenAt) }}</dd>
            <dt>Last activity</dt><dd>{{ formatDateTime(data.user.lastActivityAt) }}</dd>
          </dl>
        </div>
        <div class="tiles">
          <div v-for="t in tiles" :key="t.label" class="tile">
            <div class="tile-label">{{ t.label }}</div>
            <div class="tile-value">{{ formatNumber(t.value) }}</div>
          </div>
        </div>
        <div class="card">
          <h2>Conversations</h2>
          <p class="muted small">Metadata only. To read what is in a conversation, impersonate the user (the reason is audited).</p>
          <p v-if="!data.conversations.length" class="empty">This user has no conversations.</p>
          <div v-else class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Conversation</th><th scope="col">Started</th><th scope="col">Last message</th>
                  <th scope="col">State</th><th scope="col" class="num">Messages</th><th scope="col" class="num">Previews</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="c in data.conversations" :key="c.id">
                  <th scope="row" class="mono" :title="c.id">{{ shortId(c.id) }}</th>
                  <td>{{ formatDateTime(c.createdAt) }}</td>
                  <td>{{ formatDateTime(c.lastMessageAt) }}</td>
                  <td>{{ c.endedAt ? `Ended ${formatDateTime(c.endedAt)}` : "Open" }}</td>
                  <td class="num">{{ formatNumber(c.messages) }}</td>
                  <td class="num">{{ formatNumber(c.previews) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <ImpersonateModal
          v-if="showModal"
          :target="{ tenantId: data.user.tenantId, sub: data.user.sub }"
          @close="showModal = false"
        />
      </template>
    </StateBlock>
  </section>
</template>
