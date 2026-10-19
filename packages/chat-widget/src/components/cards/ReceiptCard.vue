<script setup lang="ts">
import type { ActionPreview } from "@biatec/chat-protocol";
import { computed } from "vue";
import { t } from "../../i18n";
import { StatusBadge } from "../../ui";
import { amountText, networkLabel, safeUrl, statusKey, stepStatusKey } from "./model";

const props = defineProps<{
  preview: ActionPreview;
  /** Link to the downloadable evidence record for this action. */
  evidenceUrl?: string | null;
}>();

const key = computed(() => statusKey(props.preview.status));
const net = computed(() => networkLabel(props.preview));
const evidence = computed(() => safeUrl(props.evidenceUrl));
</script>

<template>
  <article class="bc-card" data-border="solid" :data-status="key" :aria-label="t('cards.receipt.title')">
    <header class="bc-card-head">
      <h3 class="bc-card-title">{{ preview.intent }}</h3>
      <StatusBadge :status="key" />
    </header>
    <div class="bc-card-meta">
      <span class="bc-tag" :data-network="net">{{ t(`cards.preview.${net}`) }}</span>
      <span class="bc-muted">{{ t("cards.receipt.title") }}</span>
    </div>
    <ol class="bc-steps">
      <li v-for="s in preview.steps" :key="s.stepId" class="bc-step" :data-status="stepStatusKey(s)">
        <span class="bc-step-title">{{ s.title }}</span>
        <span v-for="a in s.assets ?? []" :key="a.assetId" class="bc-num">{{ amountText(a) }}</span>
        <StatusBadge :status="stepStatusKey(s)" />
        <template v-if="s.transactionId">
          <span class="bc-muted">{{ t("cards.receipt.txId") }}</span>
          <span class="bc-num bc-break" data-test="tx-id">{{ s.transactionId }}</span>
          <a
            v-if="safeUrl(s.explorerUrl)"
            class="bc-link"
            data-test="tx-link"
            :href="safeUrl(s.explorerUrl)!"
            target="_blank"
            rel="noopener noreferrer"
          >{{ t("cards.receipt.viewTx") }}</a>
        </template>
        <span v-else class="bc-muted">{{ t("cards.receipt.none") }}</span>
      </li>
    </ol>
    <div class="bc-card-actions">
      <a v-if="evidence" class="bc-link" data-test="evidence-link" :href="evidence" target="_blank" rel="noopener noreferrer" download>
        {{ t("cards.receipt.evidence") }}
      </a>
    </div>
  </article>
</template>
