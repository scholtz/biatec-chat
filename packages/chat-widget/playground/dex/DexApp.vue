<script setup lang="ts">
// A trading page shaped like BiatecDEX (header, pair header, order card) with the Biatec AI entry points from
// docs/integration-biatec-dex.md. Host strings come from the host's own vue-i18n; the widget has its own catalogs.
import { computed, onBeforeUnmount, onMounted, ref } from "vue";
import { useI18n } from "vue-i18n";
import Button from "primevue/button";
import Card from "primevue/card";
import Dialog from "primevue/dialog";
import InputNumber from "primevue/inputnumber";
import Select from "primevue/select";
import SelectButton from "primevue/selectbutton";
import Tag from "primevue/tag";
import Toast from "primevue/toast";
import ToggleSwitch from "primevue/toggleswitch";
import { useToast } from "primevue/usetoast";
import { BiatecAiChip, BiatecChatInline, useBiatecChat, type AskResult } from '@biatec/chat-widget'
import { SUPPORTED_LOCALES, getCurrentLocale, setLocale, type SupportedLocale } from "@/i18n";
import { useTheme } from "@/theme";
import { askAboutOrder, clearTradingContext, setTradingContext } from "@/biatecChat";

const BASE = "ALGO";
const QUOTE = "USDC";
const NETWORK = "testnet";
/** Illustrative pool id for the page context string; nothing on this page talks to a chain. */
const POOL_APP_ID = 1000;
const PRICE = 0.1873;

const { t } = useI18n();
const toast = useToast();
const theme = useTheme();
const q = new URLSearchParams(location.search);

const locale = ref<SupportedLocale>(getCurrentLocale());
const localeOptions = SUPPORTED_LOCALES.map((l) => ({ value: l, label: l.toUpperCase() }));
function onLocale(l: SupportedLocale) {
  locale.value = l;
  void setLocale(l);
}

/** Wide trading layout: the widget sits in a column next to the order card (BiatecChatInline, step 5). */
const wide = ref(q.has("wide"));

const side = ref<"buy" | "sell">("buy");
const sideOptions = computed(() => [
  { value: "buy", label: t("order.buy") },
  { value: "sell", label: t("order.sell") },
]);
const amount = ref<number | null>(50);
const spendSymbol = computed(() => (side.value === "buy" ? QUOTE : BASE));
const receiveSymbol = computed(() => (side.value === "buy" ? BASE : QUOTE));
const receive = computed(() => {
  const a = amount.value ?? 0;
  return side.value === "buy" ? a / PRICE : a * PRICE;
});
const fmt = (n: number, d = 4) => n.toLocaleString(locale.value, { maximumFractionDigits: d, minimumFractionDigits: 2 });

const dialogOpen = ref(false);
const slippage = ref<number | null>(0.5);

const MESSAGES: Record<Exclude<AskResult, "sent">, { severity: "info" | "warn"; key: string }> = {
  busy: { severity: "info", key: "toast.busy" },
  unavailable: { severity: "warn", key: "toast.unavailable" },
  empty: { severity: "warn", key: "toast.empty" },
};
async function ask() {
  // The prompt is shown in the panel as the user's own message; the assistant can only answer with a preview that
  // the user still has to review and confirm. Nothing on this page executes anything.
  const result = amount.value ? await askAboutOrder(side.value, amount.value, BASE, QUOTE) : "empty";
  if (result !== "sent") toast.add({ severity: MESSAGES[result].severity, summary: t(MESSAGES[result].key), life: 4000 });
}

function saveSettings() {
  dialogOpen.value = false;
  toast.add({ severity: "success", summary: t("toast.saved"), life: 3000 });
}

onMounted(() => {
  setTradingContext(NETWORK, BASE, QUOTE, POOL_APP_ID);
  if (q.has("open")) useBiatecChat().open();
  if (q.has("dialog")) dialogOpen.value = true;
});
onBeforeUnmount(clearTradingContext);
</script>

<template>
  <div class="dex-ambient" aria-hidden="true" />
  <Toast />
  <header class="sticky top-0 z-10 border-b border-[var(--surface-border)] bg-[var(--surface-panel)] backdrop-blur">
    <div class="mx-auto flex max-w-7xl flex-wrap items-center gap-x-6 gap-y-2 px-4 py-3">
      <a href="#" class="text-lg font-semibold text-strong no-underline">Biatec <span class="text-[var(--brand)]">DEX</span></a>
      <nav class="hidden gap-4 text-sm sm:flex" aria-label="Main">
        <a href="#" class="font-semibold text-strong no-underline">{{ t("nav.trade") }}</a>
        <a href="#" class="text-muted no-underline">{{ t("nav.liquidity") }}</a>
        <a href="#" class="text-muted no-underline">{{ t("nav.portfolio") }}</a>
      </nav>
      <div class="ml-auto flex items-center gap-2">
        <Tag value="Testnet" severity="warn" />
        <label class="hidden items-center gap-2 text-sm text-muted md:flex">
          {{ t("header.wide") }}
          <ToggleSwitch v-model="wide" data-testid="dex-wide" />
        </label>
        <Select
          :model-value="locale"
          :options="localeOptions"
          option-label="label"
          option-value="value"
          :aria-label="t('header.language')"
          data-testid="dex-locale"
          class="w-24"
          @update:model-value="onLocale"
        />
        <Button
          :icon="theme.mode.value === 'dark' ? 'pi pi-sun' : 'pi pi-moon'"
          severity="secondary"
          text
          rounded
          :aria-label="t('header.theme')"
          data-testid="dex-theme"
          @click="theme.toggle()"
        />
      </div>
    </div>
  </header>

  <main
    class="mx-auto grid max-w-7xl gap-4 p-4"
    :class="wide ? 'lg:grid-cols-[minmax(0,1fr)_minmax(360px,420px)]' : ''"
  >
    <div class="grid min-w-0 content-start gap-4 lg:grid-cols-[minmax(0,1fr)_minmax(300px,380px)]">
      <section class="surface-card p-4 lg:col-span-2" aria-label="ALGO/USDC">
        <div class="flex flex-wrap items-end gap-x-8 gap-y-2">
          <div>
            <div class="eyebrow">{{ NETWORK }}</div>
            <h1 class="text-2xl">{{ BASE }}<span class="text-subtle">/{{ QUOTE }}</span></h1>
          </div>
          <div>
            <div class="eyebrow">{{ t("pair.last") }}</div>
            <div class="tabular-nums text-xl font-semibold">{{ fmt(PRICE) }}</div>
          </div>
          <div class="tabular-nums font-semibold text-[var(--pos)]">▲ +2.41%</div>
          <div>
            <div class="eyebrow">{{ t("pair.high") }}</div>
            <div class="tabular-nums">{{ fmt(0.1912) }}</div>
          </div>
          <div>
            <div class="eyebrow">{{ t("pair.low") }}</div>
            <div class="tabular-nums">{{ fmt(0.1821) }}</div>
          </div>
          <div>
            <div class="eyebrow">{{ t("pair.volume") }}</div>
            <div class="tabular-nums">1,284,310 {{ QUOTE }}</div>
          </div>
        </div>
      </section>

      <section class="surface-card hidden min-h-80 items-center justify-center p-4 text-subtle sm:flex" aria-hidden="true">
        <svg viewBox="0 0 400 160" class="h-full max-h-72 w-full" preserveAspectRatio="none">
          <polyline
            fill="none"
            stroke="var(--text-subtle)"
            stroke-width="2"
            points="0,120 40,110 80,118 120,90 160,96 200,70 240,78 280,52 320,60 360,40 400,44"
          />
        </svg>
      </section>

      <Card data-testid="dex-order-card">
        <template #content>
          <form class="flex flex-col gap-4 p-4" @submit.prevent>
            <div class="flex items-center justify-between gap-2">
              <h2 class="text-base">{{ t("order.title") }}</h2>
              <Button
                icon="pi pi-cog"
                severity="secondary"
                text
                rounded
                :aria-label="t('order.settings')"
                data-testid="dex-open-dialog"
                @click="dialogOpen = true"
              />
            </div>
            <SelectButton v-model="side" :options="sideOptions" option-label="label" option-value="value" :allow-empty="false" />
            <label class="flex flex-col gap-1 text-sm text-muted">
              {{ t("order.spend") }}
              <InputNumber v-model="amount" :min="0" :max-fraction-digits="6" :suffix="` ${spendSymbol}`" fluid />
            </label>
            <div class="surface-inset flex items-center justify-between px-3 py-2 text-sm">
              <span class="text-muted">{{ t("order.receive") }}</span>
              <span class="tabular-nums font-semibold">{{ fmt(receive) }} {{ receiveSymbol }}</span>
            </div>
            <Button :label="t('order.submit')" :severity="side === 'buy' ? 'success' : 'danger'" fluid />
            <!-- The chip sits next to the button, not in its #icon slot: it draws the letters "AI" as visible SVG text,
                 and inside the button that text would not be part of the button's name (WCAG 2.5.3, axe
                 label-content-name-mismatch). Out here it is decorative and the name is exactly the visible label. -->
            <div class="flex items-center self-start">
              <span class="inline-flex" aria-hidden="true"><BiatecAiChip :size="16" /></span>
              <Button :label="t('order.ask')" link size="small" data-testid="dex-ask" @click="ask" />
            </div>
          </form>
        </template>
      </Card>
    </div>

    <aside v-if="wide" class="min-w-0">
      <BiatecChatInline class="w-full" />   <!-- minmax(360px, 420px) column; same session as the floating widget -->
    </aside>
  </main>

  <Dialog v-model:visible="dialogOpen" modal :header="t('dialog.title')" class="w-[28rem] max-w-[calc(100vw-2rem)]">
    <p class="mb-4 text-sm text-muted">{{ t("dialog.body") }}</p>
    <label class="flex flex-col gap-1 text-sm text-muted">
      {{ t("dialog.slippage") }}
      <InputNumber v-model="slippage" :min="0.1" :max="5" :max-fraction-digits="2" suffix=" %" fluid data-testid="dex-slippage" />
    </label>
    <template #footer>
      <Button :label="t('dialog.close')" severity="secondary" text @click="dialogOpen = false" />
      <Button :label="t('dialog.save')" @click="saveSettings" />
    </template>
  </Dialog>
</template>
