<script setup lang="ts">
import { computed } from "vue";
import { formatNumber } from "../format";

// Horizontal bars with the value printed beside each; a plain list so it reads fine without sight of the colour.
const props = defineProps<{ title: string; items: { label: string; value: number }[]; emptyText?: string }>();
const max = computed(() => Math.max(1, ...props.items.map((i) => i.value)));
function pct(v: number): string {
  return `${Math.max(v > 0 ? 1.5 : 0, (v / max.value) * 100)}%`;
}
</script>

<template>
  <figure class="chart" data-test="bar-list">
    <figcaption class="chart-title">{{ title }}</figcaption>
    <p v-if="!items.length" class="empty">{{ emptyText ?? "No data." }}</p>
    <ul v-else class="bars">
      <li v-for="it in items" :key="it.label" class="bar-row">
        <span class="bar-label">{{ it.label }}</span>
        <span class="bar-track" aria-hidden="true"><span class="bar-fill" :style="{ width: pct(it.value) }"></span></span>
        <span class="bar-value">{{ formatNumber(it.value) }}</span>
      </li>
    </ul>
  </figure>
</template>
