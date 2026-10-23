<script setup lang="ts">
import type { ActionPreview, ConsentStatus } from "@biatec/chat-protocol";
import { ShieldCheck } from "lucide-vue-next";
import { computed, onBeforeUnmount, onMounted, ref, watch } from "vue";
import { t } from "../../i18n";
import { Button, StatusBadge } from "../../ui";
import ReviewDialog from "./ReviewDialog.vue";
import {
  CONFIRM_MIN_SECONDS,
  SESSION_WARN_SECONDS,
  actionWord,
  amountText,
  consentFor,
  downside,
  formatClock,
  networkLabel,
  primaryAmount,
  remainingSeconds,
  safeUrl,
  sessionSecondsLeft,
  statusKey,
  stepStatusKey,
} from "./model";

const props = defineProps<{
  preview: ActionPreview;
  consent?: ConsentStatus | null;
  /** The session's absolute expiry (SessionState.expiresAt). */
  sessionExpiresAt?: string | null;
  /** True while the confirm call is in flight; locks Confirm. */
  submitting?: boolean;
  /** Test hook and host override for the clock (epoch ms). */
  nowMs?: number;
}>();
const emit = defineEmits<{
  confirm: [previewId: string];
  cancel: [previewId: string];
  /** Authorize signing: the host runs the elevate popup for this scope. */
  authorize: [scope: string];
  /** Under 20 minutes of session left: finish the action now, or save it and sign in again. */
  finish: [previewId: string];
  save: [previewId: string];
}>();

const clock = ref(Date.now());
let timer: ReturnType<typeof setInterval> | undefined;
onMounted(() => {
  timer = setInterval(() => (clock.value = Date.now()), 1000);
});
onBeforeUnmount(() => clearInterval(timer));
const now = computed(() => props.nowMs ?? clock.value);

const reviewOpen = ref(false);
const reviewed = ref(false);
const acknowledged = ref(false);
const confirmed = ref(false);
const expanded = ref(false);

const activeConsent = computed(() => consentFor(props.preview, props.consent));
const key = computed(() => statusKey(props.preview.status, activeConsent.value?.state));
const waitingForPhone = computed(
  () => activeConsent.value?.state === "Pending" || (!activeConsent.value && props.preview.status === "AwaitingApproval"),
);
const remaining = computed(() => remainingSeconds(props.preview.expiresAt, props.sessionExpiresAt, now.value));
const previewRemaining = computed(() => remainingSeconds(props.preview.expiresAt, null, now.value));
const sessionLeft = computed(() => sessionSecondsLeft(props.sessionExpiresAt, now.value));
const sessionBinds = computed(() => sessionLeft.value !== null && sessionLeft.value < previewRemaining.value);
const warnSession = computed(() => sessionLeft.value !== null && sessionLeft.value < SESSION_WARN_SECONDS);
const consentRemaining = computed(() => {
  const end = activeConsent.value?.expiresAt;
  return end ? remainingSeconds(end, null, now.value) : null;
});

const isOpen = computed(() => props.preview.status === "AwaitingConfirmation" && !activeConsent.value);
const dashed = computed(() => !confirmed.value && ["awaitingConfirmation", "awaitingScope"].includes(key.value));
const net = computed(() => networkLabel(props.preview));
const fall = computed(() => downside(props.preview));
const multi = computed(() => props.preview.steps.length > 1);
const live = computed(() => !["completed", "failed", "cancelled", "expired", "rejected", "timedOut"].includes(key.value));
const countdownShown = computed(() => live.value && !waitingForPhone.value);

const confirmLabel = computed(() => {
  const action = actionWord(props.preview);
  const amount = primaryAmount(props.preview);
  if (action && amount) return t("cards.preview.confirmAction", { action, amount });
  if (action) return t("cards.preview.confirmActionOnly", { action });
  return t("cards.preview.confirmPlain");
});
const tooLittle = computed(() => remaining.value < CONFIRM_MIN_SECONDS);
const canConfirm = computed(
  () => isOpen.value && reviewed.value && acknowledged.value && !tooLittle.value && !props.submitting && !confirmed.value,
);
const hint = computed(() => {
  if (!isOpen.value) return "";
  if (tooLittle.value) return t(sessionBinds.value ? "cards.preview.signInAgain" : "cards.preview.tooLittleTime");
  if (!reviewed.value || !acknowledged.value) return t("cards.preview.needReview");
  return "";
});

// A confirm that ends (submitting back to false) while the preview still awaits confirmation failed: allow a retry.
watch(
  () => props.submitting,
  (now, was) => {
    if (was && !now && props.preview.status === "AwaitingConfirmation") confirmed.value = false;
  },
);

// Everything the review shows the user. A preview replaced in place (same ids, different content) must not keep the earlier
// review and acknowledgement (BCHAT-026). Status, countdown and limit headroom are not part of it: they are not what is reviewed.
const reviewedContent = computed(() =>
  JSON.stringify([
    props.preview.intent,
    props.preview.network,
    props.preview.genesisId,
    props.preview.expectsAuthenticatorApproval ?? null,
    props.preview.steps.map((s) => [
      s.stepId,
      s.title,
      s.groupHash,
      s.from ?? null,
      s.to ?? null,
      (s.assets ?? []).map((a) => [a.assetId, a.symbol, a.amount, a.decimals, a.fiatValue ?? null, a.fiatCurrency ?? null]),
      s.feeAmount ?? null,
      s.slippagePercent ?? null,
      s.simulationSummary ?? null,
    ]),
  ]),
);

// A reused card instance must not carry review state over to a different action, nor over to the next step of a
// multi-step preview (the user reviews and acknowledges each group on its own), nor over changed content.
watch(
  () => `${props.preview.previewId}:${props.preview.steps.find((s) => s.status === "AwaitingConfirmation")?.stepId ?? ""}:${reviewedContent.value}`,
  () => {
    reviewed.value = false;
    acknowledged.value = false;
    confirmed.value = false;
    expanded.value = false;
    reviewOpen.value = false;
  },
);

function openReview() {
  reviewOpen.value = true;
  reviewed.value = true;
}
function onConfirm() {
  if (!canConfirm.value) return;
  confirmed.value = true;
  emit("confirm", props.preview.previewId);
}
</script>

<template>
  <article
    class="bc-card"
    :data-border="dashed ? 'dashed' : 'solid'"
    :data-status="key"
    :data-network="net"
    :aria-label="t('cards.preview.label')"
  >
    <header class="bc-card-head">
      <h3 class="bc-card-title">{{ preview.intent }}</h3>
      <StatusBadge :status="key" />
    </header>
    <div class="bc-card-meta">
      <span class="bc-tag" :data-network="net">{{ t(`cards.preview.${net}`) }}</span>
      <span class="bc-muted">{{ preview.network }}</span>
    </div>

    <ol v-if="multi" class="bc-steps" :aria-label="t('cards.preview.steps')">
      <li v-for="s in preview.steps" :key="s.stepId" class="bc-step" :data-status="stepStatusKey(s)">
        <span class="bc-step-n bc-num">{{ t("cards.preview.step", { n: Number(s.index) + 1 }) }}</span>
        <span class="bc-step-title">{{ s.title }}</span>
        <StatusBadge :status="stepStatusKey(s)" />
        <template v-if="s.transactionId">
          <a v-if="safeUrl(s.explorerUrl)" class="bc-link bc-num bc-break" :href="safeUrl(s.explorerUrl)!" target="_blank" rel="noopener noreferrer">{{ s.transactionId }}</a>
          <span v-else class="bc-num bc-break">{{ s.transactionId }}</span>
        </template>
      </li>
    </ol>

    <dl class="bc-kv bc-summary">
      <template v-for="s in preview.steps" :key="s.stepId">
        <template v-for="a in s.assets ?? []" :key="s.stepId + a.assetId">
          <dt>{{ multi ? `${Number(s.index) + 1}. ${a.symbol}` : a.symbol }}</dt>
          <dd class="bc-num">{{ amountText(a) }}</dd>
        </template>
      </template>
      <template v-for="s in preview.steps.filter((x) => x.feeAmount)" :key="'f' + s.stepId">
        <dt>{{ t("cards.preview.fee") }}</dt><dd class="bc-num">{{ s.feeAmount }}</dd>
      </template>
      <template v-for="s in preview.steps.filter((x) => x.slippagePercent)" :key="'s' + s.stepId">
        <dt>{{ t("cards.preview.slippage") }}</dt><dd class="bc-num">{{ s.slippagePercent }}%</dd>
      </template>
      <template v-if="preview.limit">
        <dt>{{ t("cards.preview.limit") }}</dt>
        <dd class="bc-num">{{ t("cards.preview.limitRemaining", { bucket: preview.limit.bucket, remaining: preview.limit.remaining, currency: preview.limit.currency }) }}</dd>
      </template>
    </dl>
    <p class="bc-phone">{{ t(preview.expectsAuthenticatorApproval ? "cards.preview.phoneYes" : "cards.preview.phoneNo") }}<template v-if="preview.approvalReason"> · {{ preview.approvalReason }}</template></p>
    <p v-if="fall" class="bc-downside" data-test="downside">{{ t("cards.preview.downside", fall) }}</p>

    <button type="button" class="bc-linkbtn" :aria-expanded="expanded" @click="expanded = !expanded">
      {{ t(expanded ? "cards.preview.hideDetails" : "cards.preview.details") }}
    </button>
    <div v-if="expanded" class="bc-details" data-test="details">
      <dl v-for="s in preview.steps" :key="s.stepId" class="bc-kv">
        <dt>{{ s.title }}</dt><dd />
        <template v-if="s.from"><dt>{{ t("cards.preview.from") }}</dt><dd class="bc-num bc-break">{{ s.from }}</dd></template>
        <template v-if="s.to"><dt>{{ t("cards.preview.to") }}</dt><dd class="bc-num bc-break">{{ s.to }}</dd></template>
        <template v-if="s.simulationSummary"><dt>{{ t("cards.preview.simulation") }}</dt><dd>{{ s.simulationSummary }}</dd></template>
      </dl>
    </div>

    <p v-if="countdownShown" class="bc-countdown">
      <span>{{ t("cards.preview.validFor") }}</span>
      <time class="bc-num" data-test="countdown">{{ formatClock(remaining) }}</time>
    </p>
    <p v-if="live && remaining === 0 && !waitingForPhone" class="bc-note" role="status">{{ t("cards.preview.expired") }}</p>

    <div v-if="warnSession && live" class="bc-note" role="status" data-test="session-warning">
      <p>{{ t("cards.preview.sessionWarn", { time: formatClock(sessionLeft ?? 0) }) }}</p>
      <div class="bc-card-actions">
        <Button size="sm" variant="secondary" @click="emit('finish', preview.previewId)">{{ t("cards.preview.finishNow") }}</Button>
        <Button size="sm" variant="ghost" @click="emit('save', preview.previewId)">{{ t("cards.preview.saveAction") }}</Button>
      </div>
    </div>

    <div v-if="waitingForPhone" class="bc-consent" data-test="consent">
      <p class="bc-consent-title">{{ t("cards.consent.waiting") }}</p>
      <template v-if="activeConsent?.bindingCode">
        <p class="bc-consent-hint">{{ t("cards.consent.codeHint") }}</p>
        <p class="bc-code bc-num" :aria-label="t('cards.consent.code')" data-test="binding-code">{{ activeConsent.bindingCode }}</p>
      </template>
      <p v-if="consentRemaining !== null" class="bc-countdown">
        <span>{{ t("cards.consent.timeLeft") }}</span>
        <time class="bc-num" data-test="consent-countdown">{{ formatClock(consentRemaining) }}</time>
      </p>
      <div class="bc-card-actions">
        <Button variant="secondary" @click="emit('cancel', preview.previewId)">{{ t("cards.consent.cancel") }}</Button>
      </div>
    </div>

    <div v-else-if="preview.status === 'AwaitingScope'" class="bc-card-actions" data-test="scope">
      <p class="bc-hint">{{ t("cards.preview.authorizeHint") }}</p>
      <Button variant="soft" @click="emit('authorize', 'sign')">{{ t("cards.preview.authorize") }}</Button>
      <Button variant="ghost" @click="emit('cancel', preview.previewId)">{{ t("cards.preview.cancel") }}</Button>
    </div>

    <template v-else-if="isOpen">
      <p v-if="hint" class="bc-hint" data-test="hint">{{ hint }}</p>
      <div class="bc-card-actions">
        <Button variant="secondary" data-test="review" @click="openReview">{{ t("cards.preview.review") }}</Button>
        <Button variant="ghost" @click="emit('cancel', preview.previewId)">{{ t("cards.preview.cancel") }}</Button>
      </div>
      <Button
        class="bc-confirm"
        size="lg"
        variant="primary"
        :disabled="!canConfirm"
        :loading="submitting"
        data-test="confirm"
        @click="onConfirm"
      >
        <ShieldCheck :size="20" :stroke-width="2" aria-hidden="true" />
        {{ confirmLabel }}
      </Button>
    </template>

    <ReviewDialog v-model:acknowledged="acknowledged" :open="reviewOpen" :preview="preview" @close="reviewOpen = false" />
  </article>
</template>
