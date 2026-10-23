<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { api, ApiError } from "../api";
import { formatCountdown, msUntil, shortMiddle } from "../format";
import type { Impersonation } from "../types";

const props = defineProps<{ impersonation: Impersonation }>();
const emit = defineEmits<{ (e: "stopped"): void; (e: "expired"): void }>();

const now = ref(Date.now());
const stopping = ref(false);
const error = ref("");
let timer: ReturnType<typeof setInterval> | undefined;
let expiredSent = false;

const remaining = computed(() => msUntil(props.impersonation.expiresAt, now.value));
const label = computed(() => `subject ${shortMiddle(props.impersonation.sub, 24)}`);

function tick() {
  now.value = Date.now();
  if (remaining.value <= 0 && !expiredSent) {
    expiredSent = true;
    emit("expired");
  }
}

onMounted(() => {
  tick();
  timer = setInterval(tick, 1000);
});
onBeforeUnmount(() => clearInterval(timer));
watch(() => props.impersonation.expiresAt, () => (expiredSent = false));

async function stop() {
  stopping.value = true;
  error.value = "";
  try {
    await api.stopImpersonation();
    emit("stopped");
  } catch (e) {
    // 409: already ended on the server, so the banner is stale either way.
    if (e instanceof ApiError && (e.status === 409 || e.status === 404)) emit("stopped");
    else if (!(e instanceof ApiError && e.status === 401)) error.value = e instanceof Error ? e.message : "Could not stop.";
  } finally {
    stopping.value = false;
  }
}
</script>

<template>
  <div class="imp-banner" role="region" aria-label="Impersonation active" data-test="banner">
    <div class="imp-text">
      <strong :title="impersonation.sub">Viewing as <span class="mono">{{ label }}</span></strong>
      <span class="imp-reason">Reason: {{ impersonation.reason }}</span>
    </div>
    <div class="imp-actions">
      <span class="imp-timer" data-test="countdown">
        <span class="sr-only">Time left </span>{{ formatCountdown(remaining) }}
      </span>
      <a class="btn btn-banner" href="#/view" data-test="open-view">Open view</a>
      <button type="button" class="btn btn-banner-stop" :disabled="stopping" data-test="stop" @click="stop">
        {{ stopping ? "Stopping…" : "Stop" }}
      </button>
    </div>
    <p v-if="error" class="imp-error" role="alert">{{ error }}</p>
  </div>
</template>
