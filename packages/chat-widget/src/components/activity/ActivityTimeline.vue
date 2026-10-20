<script setup lang="ts">
import { computed, ref } from "vue";
import { ArrowLeft } from "lucide-vue-next";
import { Button, Chip } from "../../ui";
import { i18n, t } from "../../i18n";
import type { Agent } from "../agent/useAgent";
import { AGENT_MODE_LABEL, type AgentModeDisplay } from "../shell/agentMode";
import { ACTIVITY_FILTERS, groupByDay, localDay, type ActivityEntry, type ActivityFilter } from "./model";

const props = defineProps<{
  entries: readonly ActivityEntry[];
  agent: Agent;
  mode: AgentModeDisplay;
  /** Beside the conversation (workspace) rather than an overlay sheet. */
  docked: boolean;
  /** Test hook for "today" (epoch ms). */
  nowMs?: number;
}>();
const emit = defineEmits<{ close: [] }>();

const filter = ref<ActivityFilter>("all");
const days = computed(() => groupByDay(props.entries, filter.value));
const a = props.agent;

const locale = () => String(i18n.global.locale.value);
function dayLabel(day: string): string {
  const now = props.nowMs ?? Date.now();
  if (day === localDay(now)) return t("activity.today");
  if (day === localDay(now - 86_400_000)) return t("activity.yesterday");
  const [y, m, d] = day.split("-").map(Number);
  return new Intl.DateTimeFormat(locale(), { dateStyle: "medium" }).format(new Date(y!, m! - 1, d!));
}
const time = (ms: number) => new Intl.DateTimeFormat(locale(), { hour: "2-digit", minute: "2-digit" }).format(ms);

const agentLine = computed(() => {
  if (!a.signedIn.value) return t("activity.agentGuest");
  if (props.mode === "paused") return t("activity.agentPaused");
  return t(props.mode === "analyze" ? "activity.agentAnalyze" : "activity.agentPrepare");
});
</script>

<template>
  <div class="bc-activity" data-testid="activity">
    <div class="bc-activity-head">
      <h3 class="bc-activity-h">{{ t("activity.agent") }}</h3>
      <Button variant="ghost" size="sm" data-testid="activity-back" @click="emit('close')">
        <ArrowLeft v-if="!docked" :size="16" aria-hidden="true" />
        {{ docked ? t("activity.hide") : t("activity.back") }}
      </Button>
    </div>
    <div class="bc-activity-agent">
      <p class="bc-activity-mode">
        <span class="bc-status-dot" :data-mode="mode" aria-hidden="true" />
        {{ t(AGENT_MODE_LABEL[mode]) }}
      </p>
      <p class="bc-activity-sub">{{ agentLine }}</p>
      <div class="bc-activity-actions">
        <Button variant="secondary" size="sm" data-testid="activity-manage" @click="a.open()">{{ t("activity.manage") }}</Button>
        <template v-if="a.signedIn.value">
          <Button v-if="a.paused.value" variant="ghost" size="sm" data-testid="activity-resume" @click="a.open('resume')">
            {{ t("activity.resumeAgent") }}
          </Button>
          <Button v-else variant="ghost" size="sm" data-testid="activity-pause" :disabled="a.saving.value" @click="a.pause()">
            {{ t("activity.pauseAgent") }}
          </Button>
        </template>
      </div>
    </div>

    <h3 class="bc-activity-h">{{ t("activity.title") }}</h3>
    <div class="bc-activity-filters" role="group" :aria-label="t('activity.filtersLabel')">
      <Chip
        v-for="f in ACTIVITY_FILTERS"
        :key="f"
        interactive
        :selected="filter === f"
        :data-testid="`filter-${f}`"
        @click="filter = f"
      >
        {{ t(`activity.filter.${f}`) }}
      </Chip>
    </div>

    <p v-if="!days.length" class="bc-activity-empty">{{ t("activity.empty") }}</p>
    <section v-for="g in days" :key="g.day" class="bc-activity-day" :aria-label="dayLabel(g.day)">
      <h4 class="bc-activity-dayh">{{ dayLabel(g.day) }}</h4>
      <ol class="bc-activity-list">
        <li v-for="e in g.entries" :key="e.id" class="bc-activity-item" data-testid="activity-entry" :data-kind="e.kind">
          <span class="bc-activity-marker" :data-kind="e.kind" aria-hidden="true" />
          <div class="bc-activity-body">
            <p class="bc-activity-title">
              <span>{{ t(`activity.event.${e.title}`, e.params) }}</span>
              <time class="bc-activity-time" :datetime="new Date(e.at).toISOString()">{{ time(e.at) }}</time>
            </p>
            <p v-if="e.detail" class="bc-activity-detail">{{ e.detail }}</p>
            <p class="bc-activity-meta">
              <span class="bc-activity-kind">{{ t(`activity.filter.${e.kind}`) }}</span>
              <span aria-hidden="true"> · </span>
              <span>{{ e.by === "you" ? t("activity.byYou") : t("activity.byAi") }}</span>
            </p>
          </div>
        </li>
      </ol>
    </section>
    <p class="bc-activity-footer">{{ t("activity.footer") }}</p>
  </div>
</template>
