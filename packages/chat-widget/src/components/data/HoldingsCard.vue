<script setup lang="ts">
import { computed } from "vue";
import { Info } from "lucide-vue-next";
import { t } from "../../i18n";
import { networkLabel } from "../cards/model";
import { buildHoldings, percentText, utcTime, type HoldingsCardData } from "./holdings";

/**
 * Holdings card (T-067), built only from a data_card event of kind "holdings" (the wallet.balance tool result), never
 * from model text. Unit names come from the chain: rendered as text, never as HTML. Amounts are decimal strings and
 * stay strings; shares and the total need fiat values on every asset, otherwise the card shows amounts only.
 */
const props = defineProps<{ card: HoldingsCardData }>();

const model = computed(() => buildHoldings(props.card));
const net = computed(() => networkLabel({ network: props.card.network, genesisId: "" }));
const time = computed(() => utcTime(props.card.asOf));
/** The bar draws only assets with a share, in the order of the rows. */
const segments = computed(() => model.value.rows.filter((r) => r.tenths !== null && r.tenths > 0));
</script>

<template>
  <article class="bc-card bc-holdings" data-border="solid" data-test="holdings-card" :aria-label="t('holdings.label')">
    <header class="bc-card-head">
      <h3 class="bc-card-title">{{ t("holdings.title") }}</h3>
      <span class="bc-tag" :data-network="net">{{ t(`cards.preview.${net}`) }}</span>
    </header>

    <p v-if="model.total" class="bc-holdings-total bc-num" data-test="holdings-total">
      <span class="bc-muted">{{ t("holdings.total") }}</span>
      <strong>{{ model.total }}</strong>
    </p>

    <div
      v-if="model.hasShares"
      class="bc-holdings-bar"
      data-test="holdings-bar"
      role="img"
      :aria-label="t('holdings.allocation')"
    >
      <span
        v-for="r in segments"
        :key="r.assetId"
        class="bc-holdings-seg"
        data-test="holdings-seg"
        :data-rank="Math.min(r.rank, 4)"
        :style="{ flexGrow: r.tenths ?? 0 }"
      ></span>
    </div>

    <ul class="bc-holdings-rows">
      <li v-for="r in model.rows" :key="r.assetId" class="bc-holdings-row" data-test="holdings-row">
        <span v-if="model.hasShares" class="bc-holdings-swatch" :data-rank="Math.min(r.rank, 4)" aria-hidden="true"></span>
        <span class="bc-holdings-unit bc-break" data-test="holdings-unit">{{ r.unitName }}</span>
        <span class="bc-holdings-amount bc-num" data-test="holdings-amount">{{ r.amount }}</span>
        <span v-if="r.tenths !== null" class="bc-holdings-share bc-num" data-test="holdings-share">{{ percentText(r.tenths) }}%</span>
      </li>
    </ul>

    <p v-if="model.concentrated" class="bc-holdings-note" data-test="holdings-concentration">
      <Info :size="16" :stroke-width="2" aria-hidden="true" />
      <span>{{ t("holdings.concentration", { asset: model.concentrated.unitName, percent: percentText(model.concentrated.tenths), threshold: 40 }) }}</span>
    </p>

    <p v-if="time" class="bc-holdings-asof bc-muted bc-num" data-test="holdings-asof">
      {{ t("holdings.asOf", { time, network: t(`cards.preview.${net}`) }) }}
    </p>
  </article>
</template>
