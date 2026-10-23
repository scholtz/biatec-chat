<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { api } from "../api";
import CopyButton from "../components/CopyButton.vue";
import ImpersonateModal from "../components/ImpersonateModal.vue";
import StateBlock from "../components/StateBlock.vue";
import { formatDateTime, formatNumber, shortMiddle } from "../format";
import { readPref, useLoader, writePref } from "../loader";
import { navigate, userHref } from "../router";
import type { SortDir, UserItem, UserSort, TenantRow } from "../types";

const PAGE_SIZES = [10, 25, 50, 100];
const stored = Number(readPref("pageSize"));

const search = ref("");
const q = ref(""); // debounced
const tenant = ref("");
const sort = ref<UserSort>("lastActivity");
const dir = ref<SortDir>("desc");
const page = ref(1);
const pageSize = ref(PAGE_SIZES.includes(stored) ? stored : 25);
const tenants = ref<TenantRow[]>([]);
const target = ref<UserItem | null>(null);

const { data, loading, error, load } = useLoader(() =>
  api.users({ q: q.value, tenant: tenant.value, sort: sort.value, dir: dir.value, page: page.value, pageSize: pageSize.value }),
);

let debounce: ReturnType<typeof setTimeout> | undefined;
watch(search, (v) => {
  clearTimeout(debounce);
  debounce = setTimeout(() => {
    if (q.value !== v.trim()) {
      q.value = v.trim();
    }
  }, 300);
});
// A changed filter or sort goes back to page 1; moving off a later page loads through the page watcher, so one change is one request.
watch([q, tenant, sort, dir, pageSize], () => {
  if (page.value !== 1) page.value = 1;
  else load();
});
watch(page, load);
watch(pageSize, (v) => writePref("pageSize", String(v)));

onMounted(async () => {
  load();
  try {
    tenants.value = (await api.stats(7)).tenants;
  } catch { /* the filter then only offers "All tenants" */ }
});
onBeforeUnmount(() => clearTimeout(debounce));

const columns: { key: UserSort | null; label: string; num?: boolean }[] = [
  { key: "sub", label: "Subject" },
  { key: null, label: "Tenant" },
  { key: "firstSeen", label: "First seen" },
  { key: "lastActivity", label: "Last activity" },
  { key: null, label: "Sign-ins", num: true },
  { key: "conversations", label: "Conversations", num: true },
];

function sortBy(key: UserSort) {
  if (sort.value === key) dir.value = dir.value === "asc" ? "desc" : "asc";
  else {
    sort.value = key;
    dir.value = key === "sub" ? "asc" : "desc";
  }
}
function ariaSort(key: UserSort | null): "ascending" | "descending" | "none" | undefined {
  if (!key) return undefined;
  return sort.value === key ? (dir.value === "asc" ? "ascending" : "descending") : "none";
}

const total = computed(() => data.value?.total ?? 0);
const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)));
const from = computed(() => (total.value === 0 ? 0 : (page.value - 1) * pageSize.value + 1));
const to = computed(() => Math.min(total.value, page.value * pageSize.value));

function open(u: UserItem) {
  navigate(userHref(u.tenantId, u.sub).slice(1));
}
</script>

<template>
  <section aria-labelledby="h-users">
    <div class="page-head">
      <h1 id="h-users">Users</h1>
      <div class="toolbar">
        <label class="inline-field grow">
          <span class="label">Search</span>
          <input v-model="search" type="search" class="input" placeholder="Search by subject" data-test="search" />
        </label>
        <label class="inline-field">
          <span class="label">Tenant</span>
          <select v-model="tenant" class="input" data-test="tenant">
            <option value="">All tenants</option>
            <option v-for="t in tenants" :key="t.id" :value="t.id">{{ t.host || t.id }}</option>
          </select>
        </label>
      </div>
    </div>
    <p class="muted small">Metadata only. Message content is never listed here; reading it requires impersonation.</p>

    <StateBlock :loading="loading && !data" :error="error" :empty="!!data && data.items.length === 0" empty-text="No users match." :rows="8" @retry="load">
      <div v-if="data" class="card flush" :class="{ dim: loading }">
        <div class="table-wrap">
          <table class="table table-click" data-test="users-table">
            <caption class="sr-only">Users</caption>
            <thead>
              <tr>
                <th v-for="c in columns" :key="c.label" scope="col" :aria-sort="ariaSort(c.key)" :class="{ num: c.num }">
                  <button v-if="c.key" type="button" class="th-btn" :data-test="`sort-${c.key}`" @click="sortBy(c.key)">
                    {{ c.label }}
                    <span aria-hidden="true" class="sort-ind">{{ sort === c.key ? (dir === "asc" ? "▲" : "▼") : "" }}</span>
                  </button>
                  <template v-else>{{ c.label }}</template>
                </th>
                <th scope="col"><span class="sr-only">Actions</span></th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="u in data.items" :key="u.tenantId + u.sub" data-test="user-row" @click="open(u)">
                <th scope="row" class="user-cell">
                  <a :href="userHref(u.tenantId, u.sub)" class="user-link" @click.stop>
                    <span class="mono" :title="u.sub" :aria-label="`Subject ${u.sub}`">{{ shortMiddle(u.sub, 20) }}</span>
                  </a>
                  <div class="sub"><CopyButton :value="u.sub" label="subject" /></div>
                </th>
                <td>{{ u.tenantId }}</td>
                <td>{{ formatDateTime(u.firstSeenAt) }}</td>
                <td>{{ formatDateTime(u.lastActivityAt ?? u.lastSeenAt) }}</td>
                <td class="num">{{ formatNumber(u.signInCount) }}</td>
                <td class="num">{{ formatNumber(u.conversations) }}</td>
                <td class="actions">
                  <a :href="userHref(u.tenantId, u.sub)" class="btn btn-small" @click.stop>Open</a>
                  <button type="button" class="btn btn-small" data-test="impersonate" @click.stop="target = u">Impersonate</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="pager">
          <span aria-live="polite" data-test="range">{{ from }}-{{ to }} of {{ formatNumber(total) }}</span>
          <label class="inline-field">
            <span class="label">Per page</span>
            <select v-model.number="pageSize" class="input" data-test="page-size">
              <option v-for="s in PAGE_SIZES" :key="s" :value="s">{{ s }}</option>
            </select>
          </label>
          <div class="pager-btns">
            <button type="button" class="btn btn-small" :disabled="page <= 1" data-test="prev" @click="page--">Previous</button>
            <span>Page {{ page }} of {{ pages }}</span>
            <button type="button" class="btn btn-small" :disabled="page >= pages" data-test="next" @click="page++">Next</button>
          </div>
        </div>
      </div>
    </StateBlock>

    <ImpersonateModal v-if="target" :target="target" @close="target = null" />
  </section>
</template>
