<script setup lang="ts">
import { ref } from "vue";
import { BaiSymbol, Button } from "../../ui";
import { t } from "../../i18n";

/** The minimized panel: chip, name, status line and Restore. */
defineProps<{ statusLine: string; controls: string }>();
const emit = defineEmits<{ restore: [] }>();
const root = ref<HTMLElement | null>(null);
defineExpose({ focus: () => root.value?.querySelector<HTMLButtonElement>("button")?.focus() });
</script>

<template>
  <div ref="root" class="bc-minbar" role="region" :aria-label="t('shell.panelLabel')" data-testid="minimized-bar">
    <BaiSymbol :size="36" aria-hidden="true" />
    <div class="bc-minbar-text">
      <div class="bc-minbar-title">{{ t("shell.title") }}</div>
      <div class="bc-minbar-status" role="status" data-testid="minimized-status">{{ statusLine }}</div>
    </div>
    <Button variant="secondary" size="sm" data-testid="restore" :aria-controls="controls" @click="emit('restore')">
      {{ t("shell.restore") }}
    </Button>
  </div>
</template>
