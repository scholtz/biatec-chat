<script setup lang="ts">
import { computed } from "vue";
import { CirclePause, TriangleAlert, X } from "lucide-vue-next";
import { Button, IconButton, Toast } from "../../ui";
import { t } from "../../i18n";
import type { Agent } from "./useAgent";

/** Under the status bar: a typed refusal (with its way out), else the paused banner; and the shell's toasts. */
const props = defineProps<{ agent: Agent }>();
const a = props.agent;
const toasts = computed(() => a.toasts.map((m) => ({ ...m, text: t(m.text) })));
</script>

<template>
  <div v-if="a.refusal.value" class="bc-agent-banner" role="alert" data-testid="agent-refusal" :data-code="a.refusal.value">
    <TriangleAlert :size="16" aria-hidden="true" />
    <p>{{ a.refusal.value === "agent_paused" ? t("agent.refusalPaused") : t("agent.refusalAnalyze") }}</p>
    <Button
      v-if="a.refusal.value === 'agent_paused'"
      variant="ghost"
      size="sm"
      data-testid="refusal-resume"
      @click="a.open('resume')"
    >
      {{ t("agent.resume") }}
    </Button>
    <Button v-else variant="ghost" size="sm" data-testid="refusal-change-mode" @click="a.open('prepare')">
      {{ t("agent.changeMode") }}
    </Button>
    <IconButton :label="t('agent.dismiss')" @click="a.refusal.value = null"><X :size="16" aria-hidden="true" /></IconButton>
  </div>
  <div v-else-if="a.paused.value" class="bc-agent-banner" data-testid="paused-banner">
    <CirclePause :size="16" aria-hidden="true" />
    <p>{{ t("agent.pausedBanner") }}</p>
    <Button variant="ghost" size="sm" data-testid="banner-resume" @click="a.open('resume')">{{ t("agent.resume") }}</Button>
  </div>
  <div class="bc-agent-toasts">
    <Toast :messages="toasts" @dismiss="a.dismiss($event)" />
  </div>
</template>
