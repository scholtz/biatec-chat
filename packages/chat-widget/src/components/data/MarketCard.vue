<script setup lang="ts">
import { computed } from "vue";
import { t } from "../../i18n";
import { utcTime } from "./holdings";
import { buildMarket, SPARK_H, SPARK_W, type MarketCardData } from "./market";

/**
 * Market snapshot card (T-069), built only from a data_card event of kind "market" (the market.snapshot tool result),
 * never from model text. Static by design: a snapshot, not a live ticker, so nothing here animates or updates the price.
 * The change is never signalled by colour alone: arrow, sign and a word ("Up", "Down", "Flat") say the same.
 */
const props = defineProps<{ card: MarketCardData }>();

const m = computed(() => buildMarket(props.card));
const time = computed(() => utcTime(props.card.asOf));
const arrow = computed(() => (m.value.direction === "up" ? "▲" : m.value.direction === "down" ? "▼" : "■"));
const word = computed(() => t(`market.${m.value.direction}`));
</script>

<template>
  <article class="bc-card bc-market" data-border="solid" data-test="market-card" :aria-label="t('market.label', { pair: m.pair })">
    <header class="bc-card-head">
      <div class="bc-market-head">
        <p class="bc-market-kicker" data-test="market-kicker">{{ t("market.kicker", { source: m.source }) }}</p>
        <h3 class="bc-card-title bc-break" data-test="market-pair">{{ m.pair }}</h3>
      </div>
    </header>

    <p class="bc-market-price bc-num" data-test="market-price">{{ m.price }}</p>

    <p class="bc-market-change bc-num" :data-direction="m.direction" data-test="market-change">
      <span aria-hidden="true" data-test="market-arrow">{{ arrow }}</span>
      <span data-test="market-pct">{{ m.change }}</span>
      <span class="bc-muted" data-test="market-word">{{ word }} · {{ t("market.period") }}</span>
    </p>

    <svg
      v-if="m.line && m.area"
      class="bc-market-spark"
      data-test="market-spark"
      :viewBox="`0 0 ${SPARK_W} ${SPARK_H}`"
      preserveAspectRatio="none"
      role="img"
      :aria-label="t('market.spark')"
    >
      <path class="bc-market-spark-area" :d="m.area" />
      <path class="bc-market-spark-line" :d="m.line" fill="none" vector-effect="non-scaling-stroke" />
    </svg>

    <dl class="bc-market-stats">
      <div>
        <dt class="bc-muted">{{ t("market.high") }}</dt>
        <dd class="bc-num" data-test="market-high">{{ m.high }}</dd>
      </div>
      <div>
        <dt class="bc-muted">{{ t("market.low") }}</dt>
        <dd class="bc-num" data-test="market-low">{{ m.low }}</dd>
      </div>
      <div>
        <dt class="bc-muted">{{ t("market.volume") }}</dt>
        <dd class="bc-num" data-test="market-volume">{{ m.volume }}</dd>
      </div>
    </dl>

    <p v-if="time" class="bc-market-stamp bc-muted bc-num" data-test="market-stamp">{{ t("market.stamp", { time }) }}</p>
  </article>
</template>
