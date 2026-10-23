<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import { api } from "../api";
import BarList from "../components/BarList.vue";
import LineChart from "../components/LineChart.vue";
import StateBlock from "../components/StateBlock.vue";
import { formatDateTime, formatNumber, shortModel } from "../format";
import { useLoader } from "../loader";
import type { TenantRow } from "../types";

const ranges = [7, 30, 90];
const days = ref(30);
const tenant = ref("");
const seriesKey = ref<"messages" | "conversations" | "newUsers" | "tokens">("messages");
const knownTenants = ref<TenantRow[]>([]);

const { data: stats, loading, error, load } = useLoader(() => api.stats(days.value, tenant.value || null));

onMounted(load);
watch([days, tenant], load);
watch(stats, (s) => {
  // Keep the full tenant list for the filter even after filtering narrows the response.
  if (s && !tenant.value) knownTenants.value = s.tenants;
});

const seriesOptions = [
  { key: "messages", label: "Messages" },
  { key: "conversations", label: "Conversations" },
  { key: "newUsers", label: "New users" },
  { key: "tokens", label: "Tokens in/out" },
] as const;

const tiles = computed(() => {
  const t = stats.value?.totals;
  if (!t) return [];
  return [
    { label: "Users", value: t.users },
    { label: "Active (7 days)", value: t.activeUsers7d },
    { label: "Conversations (all time)", value: t.conversations },
    { label: "Messages (all time)", value: t.messages },
    { label: "Live sessions", value: t.liveSessions },
    { label: "Previews (all time)", value: t.previews },
    { label: "Tenants", value: t.tenants },
  ];
});

const dates = computed(() => stats.value?.daily.map((d) => d.date) ?? []);
const chartSeries = computed(() => {
  const d = stats.value?.daily ?? [];
  switch (seriesKey.value) {
    case "conversations": return [{ name: "Conversations", values: d.map((r) => r.conversations) }];
    case "newUsers": return [{ name: "New users", values: d.map((r) => r.newUsers) }];
    case "tokens":
      return [
        { name: "Input tokens", values: d.map((r) => r.inputTokens) },
        { name: "Output tokens", values: d.map((r) => r.outputTokens) },
      ];
    default: return [{ name: "Messages", values: d.map((r) => r.messages) }];
  }
});
const chartTitle = computed(
  () => `${seriesOptions.find((o) => o.key === seriesKey.value)?.label} per day, last ${stats.value?.days ?? days.value} days`,
);
const tiers = computed(() => (stats.value?.sessionsByTier ?? []).map((t) => ({ label: t.tier, value: t.count })));
const statuses = computed(() => (stats.value?.previewsByStatus ?? []).map((t) => ({ label: t.status, value: t.count })));
</script>

<template>
  <section aria-labelledby="h-overview">
    <div class="page-head">
      <h1 id="h-overview">Overview</h1>
      <div class="toolbar">
        <div class="seg" role="group" aria-label="Date range">
          <button
            v-for="r in ranges"
            :key="r"
            type="button"
            class="seg-btn"
            :aria-pressed="days === r"
            :data-test="`range-${r}`"
            @click="days = r"
          >{{ r }} days</button>
        </div>
        <label class="inline-field">
          <span class="label">Tenant</span>
          <select v-model="tenant" class="input" data-test="tenant">
            <option value="">All tenants</option>
            <option v-for="t in knownTenants" :key="t.id" :value="t.id">{{ t.host || t.id }}</option>
          </select>
        </label>
      </div>
    </div>

    <StateBlock :loading="loading && !stats" :error="error" :rows="6" @retry="load">
      <template v-if="stats">
        <p v-if="stats.generatedAt" class="muted small">Generated {{ formatDateTime(stats.generatedAt) }}</p>
        <div class="tiles" :class="{ dim: loading }">
          <div v-for="t in tiles" :key="t.label" class="tile">
            <div class="tile-label">{{ t.label }}</div>
            <div class="tile-value">{{ formatNumber(t.value) }}</div>
          </div>
        </div>

        <div class="card">
          <div class="card-head">
            <div class="seg" role="group" aria-label="Chart series">
              <button
                v-for="o in seriesOptions"
                :key="o.key"
                type="button"
                class="seg-btn"
                :aria-pressed="seriesKey === o.key"
                :data-test="`series-${o.key}`"
                @click="seriesKey = o.key"
              >{{ o.label }}</button>
            </div>
          </div>
          <LineChart :title="chartTitle" :dates="dates" :series="chartSeries" />
        </div>

        <div class="grid-2">
          <div class="card"><BarList title="Action previews by status" :items="statuses" empty-text="No previews yet." /></div>
          <div class="card"><BarList title="Live sessions by tier" :items="tiers" empty-text="No live sessions." /></div>
        </div>

        <div class="card">
          <h2>Token usage per model <span class="muted small">(last {{ stats.days }} days)</span></h2>
          <p v-if="!stats.tokens.byModel.length" class="empty">No model usage recorded.</p>
          <div v-else class="table-wrap">
            <table class="table">
              <thead>
                <tr>
                  <th scope="col">Model</th><th scope="col" class="num">Messages</th><th scope="col" class="num">Input</th>
                  <th scope="col" class="num">Output</th><th scope="col" class="num">Cache read</th><th scope="col" class="num">Cache write</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="m in stats.tokens.byModel" :key="m.modelId">
                  <th scope="row" class="mono" :title="m.modelId">{{ shortModel(m.modelId) }}</th>
                  <td class="num">{{ formatNumber(m.messages) }}</td><td class="num">{{ formatNumber(m.input) }}</td>
                  <td class="num">{{ formatNumber(m.output) }}</td><td class="num">{{ formatNumber(m.cacheRead) }}</td>
                  <td class="num">{{ formatNumber(m.cacheWrite) }}</td>
                </tr>
              </tbody>
              <tfoot>
                <tr>
                  <th scope="row">Total</th><td></td>
                  <td class="num">{{ formatNumber(stats.tokens.input) }}</td><td class="num">{{ formatNumber(stats.tokens.output) }}</td>
                  <td class="num">{{ formatNumber(stats.tokens.cacheRead) }}</td><td class="num">{{ formatNumber(stats.tokens.cacheWrite) }}</td>
                </tr>
              </tfoot>
            </table>
          </div>
        </div>

        <div class="card">
          <h2>Tenants</h2>
          <p v-if="!stats.tenants.length" class="empty">No tenants.</p>
          <div v-else class="table-wrap">
            <table class="table">
              <thead><tr><th scope="col">Tenant</th><th scope="col">Host</th><th scope="col" class="num">Users</th><th scope="col" class="num">Conversations</th></tr></thead>
              <tbody>
                <tr v-for="t in stats.tenants" :key="t.id">
                  <th scope="row">{{ t.id }}</th><td>{{ t.host }}</td>
                  <td class="num">{{ formatNumber(t.users) }}</td><td class="num">{{ formatNumber(t.conversations) }}</td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
      </template>
    </StateBlock>
  </section>
</template>
