<script setup lang="ts">
import { computed, nextTick, ref, watch } from "vue";
import type { AgentMode } from "@biatec/chat-protocol";
import { Ban, CirclePause, CirclePlay, ShieldCheck } from "lucide-vue-next";
import { Button, Dialog } from "../../ui";
import { t } from "../../i18n";
import type { Agent } from "./useAgent";

const props = defineProps<{ agent: Agent; open: boolean; openPreviews: number }>();
const emit = defineEmits<{ signIn: [] }>();

const a = props.agent;
const d = a.dialog;
const body = ref<HTMLElement | null>(null);

const title = computed(() =>
  d.step === "confirm" ? t("agent.confirmTitle") : d.step === "revoke" ? t("agent.revokeTitle") : t("agent.dialogTitle"),
);
const busy = computed(() => a.saving.value || a.revoking.value);
const modeName = (m: AgentMode) => t(m === "analyze" ? "agent.modeAnalyzeTitle" : "agent.modePrepareTitle");
/** A raise drafted in the edit step (selecting Prepare from Analyze only): Continue leads to the confirm step. */
const drafted = computed(() => a.signedIn.value && d.draft === "prepare" && a.mode.value === "analyze");

const powers = computed(() => {
  if (d.raise === "prepare") return ["powerPreparePreviews", "powerPrepareConfirm", "powerPreparePhone"];
  return a.mode.value === "prepare" ? ["powerResumePreviews", "powerResumeConfirm"] : ["powerResumeAnalyze"];
});

async function select(m: AgentMode) {
  d.draft = m;
  // Lowering takes effect at once; raising waits for Continue and the confirm step.
  if (m === "analyze" && a.mode.value === "prepare") {
    if (!(await a.lower())) d.draft = a.mode.value;
  }
}

function toConfirm(raise: "prepare" | "resume") {
  d.raise = raise;
  d.ack = false;
  d.step = "confirm";
}


// Each step starts with focus on its first control (the dialog only focuses on open).
watch(
  () => d.step,
  async () => {
    await nextTick();
    body.value?.closest<HTMLElement>(".bc-dialog")?.querySelector<HTMLElement>("input:not([disabled]), button:not([disabled])")?.focus();
  },
);
</script>

<template>
  <Dialog :open="open" :title="title" @close="a.escape()">
    <div ref="body" class="bc-perm" data-testid="permissions-dialog" :data-step="d.step">
      <template v-if="d.step === 'edit'">
        <p class="bc-perm-kicker">{{ t("agent.kicker") }}</p>
        <p class="bc-perm-intro">{{ t("agent.intro") }}</p>

        <div v-if="a.signedIn.value" class="bc-perm-row" data-testid="pause-row">
          <component :is="a.paused.value ? CirclePlay : CirclePause" :size="20" aria-hidden="true" />
          <div class="bc-perm-row-text">
            <p class="bc-perm-row-title">{{ t("agent.pauseTitle") }}</p>
            <p class="bc-perm-row-sub">{{ t("agent.pauseText") }}</p>
          </div>
          <Button
            v-if="a.paused.value"
            variant="secondary"
            size="sm"
            data-testid="resume"
            :disabled="busy"
            @click="toConfirm('resume')"
          >
            {{ t("agent.resume") }}
          </Button>
          <Button v-else variant="secondary" size="sm" data-testid="pause" :disabled="busy" @click="a.pause()">
            {{ t("agent.pause") }}
          </Button>
        </div>

        <fieldset class="bc-perm-modes" role="radiogroup" :aria-label="t('agent.modesLabel')">
          <label class="bc-perm-mode" :data-selected="d.draft === 'analyze' ? '' : undefined">
            <input
              type="radio"
              name="bc-agent-mode"
              value="analyze"
              data-testid="mode-analyze"
              :checked="d.draft === 'analyze'"
              :disabled="busy || !a.signedIn.value"
              @change="select('analyze')"
            />
            <span class="bc-perm-num" aria-hidden="true">01</span>
            <span class="bc-perm-mode-text">
              <span class="bc-perm-mode-title">{{ t("agent.modeAnalyzeTitle") }}</span>
              <span class="bc-perm-mode-sub">{{ t("agent.modeAnalyzeText") }}</span>
            </span>
          </label>
          <template v-if="a.signedIn.value">
            <label class="bc-perm-mode" :data-selected="d.draft === 'prepare' ? '' : undefined">
              <input
                type="radio"
                name="bc-agent-mode"
                value="prepare"
                data-testid="mode-prepare"
                :checked="d.draft === 'prepare'"
                :disabled="busy"
                @change="select('prepare')"
              />
              <span class="bc-perm-num" aria-hidden="true">02</span>
              <span class="bc-perm-mode-text">
                <span class="bc-perm-mode-title">{{ t("agent.modePrepareTitle") }}</span>
                <span class="bc-perm-mode-sub">{{ t("agent.modePrepareText") }}</span>
              </span>
            </label>
            <label class="bc-perm-mode" data-disabled="">
              <input type="radio" name="bc-agent-mode" value="rules" data-testid="mode-rules" disabled />
              <span class="bc-perm-num" aria-hidden="true">03</span>
              <span class="bc-perm-mode-text">
                <span class="bc-perm-mode-title">{{ t("agent.modeRuleTitle") }}</span>
                <span class="bc-perm-mode-sub">{{ t("agent.modeRuleText") }}</span>
                <span class="bc-perm-tag">{{ t("agent.notAvailable") }}</span>
              </span>
            </label>
          </template>
        </fieldset>

        <div v-if="!a.signedIn.value" class="bc-perm-guest" data-testid="guest-cta">
          <p>{{ t("agent.guestText") }}</p>
          <Button variant="primary" size="md" data-testid="guest-sign-in" @click="emit('signIn')">{{ t("agent.guestSignIn") }}</Button>
        </div>

        <section v-else class="bc-perm-limits" :aria-label="t('agent.limitsTitle')">
          <p class="bc-perm-row-title">{{ t("agent.limitsTitle") }}</p>
          <p class="bc-perm-row-sub">{{ t("agent.limitsText") }}</p>
          <Button variant="secondary" size="sm" data-testid="show-limits" @click="a.showLimits(t('agent.limitsPrompt'))">
            {{ t("agent.limitsShow") }}
          </Button>
          <p class="bc-perm-note">{{ t("agent.limitsNote") }}</p>
        </section>

        <p v-if="a.error.value" class="bc-perm-error" role="alert">{{ t(a.error.value) }}</p>

        <div class="bc-perm-footer">
          <Button v-if="a.signedIn.value" variant="danger" size="md" data-testid="revoke" :disabled="busy" @click="d.step = 'revoke'">
            <Ban :size="16" aria-hidden="true" />
            {{ t("agent.revokeAll") }}
          </Button>
          <span class="bc-perm-spacer" />
          <Button variant="ghost" size="md" data-testid="perm-cancel" @click="a.close()">
            {{ a.signedIn.value ? t("agent.cancel") : t("agent.close") }}
          </Button>
          <Button
            v-if="a.signedIn.value"
            variant="primary"
            size="md"
            data-testid="perm-continue"
            :disabled="!drafted || busy"
            @click="toConfirm('prepare')"
          >
            {{ t("agent.continue") }}
          </Button>
        </div>
      </template>

      <template v-else-if="d.step === 'confirm'">
        <p class="bc-perm-intro">
          {{
            d.raise === "prepare"
              ? t("agent.confirmMoving", { from: modeName("analyze"), to: modeName("prepare") })
              : t("agent.confirmResuming", { mode: modeName(a.mode.value) })
          }}
        </p>
        <ul class="bc-perm-powers" data-testid="powers">
          <li v-for="p in powers" :key="p">
            <ShieldCheck :size="16" aria-hidden="true" />
            <span>{{ t(`agent.${p}`) }}</span>
          </li>
        </ul>
        <label class="bc-perm-ack">
          <input v-model="d.ack" type="checkbox" data-testid="ack" />
          <span>{{ t("agent.confirmAck") }}</span>
        </label>
        <p v-if="a.error.value" class="bc-perm-error" role="alert">{{ t(a.error.value) }}</p>
        <div class="bc-perm-footer">
          <span class="bc-perm-spacer" />
          <Button variant="ghost" size="md" data-testid="perm-back" @click="a.back()">{{ t("agent.back") }}</Button>
          <Button
            variant="primary"
            size="md"
            data-testid="confirm-increase"
            :disabled="!d.ack"
            :loading="a.saving.value"
            @click="a.confirmRaise()"
          >
            {{ t("agent.confirmIncrease") }}
          </Button>
        </div>
      </template>

      <template v-else>
        <p class="bc-perm-intro">{{ t("agent.revokeText") }}</p>
        <p class="bc-perm-row-sub" data-testid="revoke-count">{{ t("agent.revokeOpen", { count: openPreviews }) }}</p>
        <div class="bc-perm-footer">
          <span class="bc-perm-spacer" />
          <Button variant="ghost" size="md" data-testid="perm-back" @click="a.back()">{{ t("agent.back") }}</Button>
          <Button variant="danger" size="md" data-testid="revoke-confirm" :loading="a.revoking.value" @click="a.revoke()">
            <Ban :size="16" aria-hidden="true" />
            {{ t("agent.revokeConfirm") }}
          </Button>
        </div>
      </template>
    </div>
  </Dialog>
</template>
