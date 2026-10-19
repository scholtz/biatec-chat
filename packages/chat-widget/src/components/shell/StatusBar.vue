<script setup lang="ts">
import { computed } from "vue";
import { ChevronDown, History } from "lucide-vue-next";
import { t } from "../../i18n";
import { AGENT_MODE_LABEL, type AgentModeDisplay } from "./agentMode";

const props = defineProps<{ mode: AgentModeDisplay; permissionsOpen: boolean; activityOpen: boolean; controlsAside?: string }>();
const emit = defineEmits<{ openPermissions: []; toggleActivity: [] }>();
const label = computed(() => t(AGENT_MODE_LABEL[props.mode]));
</script>

<template>
  <div class="bc-shell-status">
    <button
      type="button"
      class="bc-status-pill"
      data-testid="status-pill"
      aria-haspopup="dialog"
      :aria-expanded="permissionsOpen"
      :aria-label="t('shell.statusAria', { mode: label })"
      @click="emit('openPermissions')"
    >
      <span class="bc-status-dot" :data-mode="mode" aria-hidden="true" />
      <span class="bc-status-prefix">{{ t("shell.statusPrefix") }}</span>
      <span class="bc-status-mode" data-testid="agent-mode">{{ label }}</span>
      <ChevronDown :size="16" aria-hidden="true" />
    </button>
    <span class="bc-shell-spacer" />
    <button
      type="button"
      class="bc-status-toggle"
      data-testid="activity-toggle"
      :aria-pressed="activityOpen"
      :aria-controls="controlsAside"
      @click="emit('toggleActivity')"
    >
      <History :size="16" aria-hidden="true" />
      {{ t("shell.activity") }}
    </button>
  </div>
</template>
