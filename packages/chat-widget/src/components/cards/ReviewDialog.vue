<script setup lang="ts">
import type { ActionPreview } from "@biatec/chat-protocol";
import { computed } from "vue";
import { t } from "../../i18n";
import { Button, Dialog } from "../../ui";
import { nextUid } from "../../ui/uid";
import { amountText, downside, networkLabel } from "./model";

const props = defineProps<{ open: boolean; preview: ActionPreview; acknowledged: boolean }>();
const emit = defineEmits<{ close: []; "update:acknowledged": [value: boolean] }>();

const ackId = `bc-ack-${nextUid()}`;
const fall = computed(() => downside(props.preview));
const phone = computed(() => t(props.preview.expectsAuthenticatorApproval ? "cards.preview.phoneYes" : "cards.preview.phoneNo"));
</script>

<template>
  <Dialog :open="open" :title="t('cards.review.title')" @close="emit('close')">
    <div class="bc-review">
      <section>
        <h3 class="bc-review-h">{{ t("cards.review.what") }}</h3>
        <p class="bc-review-p">{{ preview.intent }}</p>
      </section>
      <section>
        <h3 class="bc-review-h">{{ t("cards.review.permissions") }}</h3>
        <p class="bc-review-p">
          {{ t("cards.review.permissionsText", { network: t(`cards.preview.${networkLabel(preview)}`), phone: phone }) }}
        </p>
      </section>
      <section>
        <h3 class="bc-review-h">{{ t("cards.review.details") }}</h3>
        <ol class="bc-review-steps">
          <li v-for="s in preview.steps" :key="s.stepId">
            <strong>{{ s.title }}</strong>
            <dl class="bc-kv">
              <template v-if="s.from"><dt>{{ t("cards.preview.from") }}</dt><dd class="bc-num bc-break">{{ s.from }}</dd></template>
              <template v-if="s.to"><dt>{{ t("cards.preview.to") }}</dt><dd class="bc-num bc-break">{{ s.to }}</dd></template>
              <template v-for="a in s.assets ?? []" :key="a.assetId">
                <dt>{{ a.symbol }}</dt><dd class="bc-num">{{ amountText(a) }}</dd>
              </template>
              <template v-if="s.feeAmount"><dt>{{ t("cards.preview.fee") }}</dt><dd class="bc-num">{{ s.feeAmount }}</dd></template>
              <template v-if="s.slippagePercent"><dt>{{ t("cards.preview.slippage") }}</dt><dd class="bc-num">{{ s.slippagePercent }}%</dd></template>
              <template v-if="s.simulationSummary"><dt>{{ t("cards.preview.simulation") }}</dt><dd>{{ s.simulationSummary }}</dd></template>
            </dl>
          </li>
        </ol>
      </section>
      <section>
        <h3 class="bc-review-h">{{ t("cards.review.financial") }}</h3>
        <p class="bc-review-p" data-test="downside-review">
          <template v-if="fall">{{ t("cards.preview.downside", fall) }}</template>
          <template v-else>{{ t("cards.review.noFiat") }}</template>
        </p>
      </section>
      <section>
        <h3 class="bc-review-h">{{ t("cards.review.stop") }}</h3>
        <p class="bc-review-p">{{ t("cards.review.stopText") }}</p>
      </section>
      <label class="bc-check" :for="ackId">
        <input
          :id="ackId"
          type="checkbox"
          :checked="acknowledged"
          @change="emit('update:acknowledged', ($event.target as HTMLInputElement).checked)"
        />
        <span>{{ t("cards.review.ack") }}</span>
      </label>
      <div class="bc-card-actions">
        <Button variant="secondary" @click="emit('close')">{{ t("cards.review.done") }}</Button>
      </div>
    </div>
  </Dialog>
</template>
