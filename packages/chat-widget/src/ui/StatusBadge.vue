<script setup lang="ts">
import { computed } from "vue";
import { t } from "../i18n";
import { STATUS, type StatusKey } from "./status";

const props = defineProps<{ status: StatusKey }>();
const spec = computed(() => STATUS[props.status]);
</script>

<template>
  <span class="bc-badge" :data-status="status" :data-tone="spec.tone" :data-dashed="spec.dashed">
    <component :is="spec.icon" :size="16" :stroke-width="2" aria-hidden="true" />
    <span class="bc-badge-label">{{ t(`status.${status}`) }}</span>
  </span>
</template>
