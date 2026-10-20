<script setup lang="ts">
import { computed } from "vue";
import { t } from "../i18n";
import { chipVar } from "./chipColors";

/**
 * Biatec AI chip mark (docs/design/biatec-ai-widget/assets). `small` drops the pins and is used up to 24 px;
 * `mono-dark` is the dark glyph for the green launcher button.
 */
const props = withDefaults(defineProps<{ size?: number; variant?: "default" | "small" | "mono-dark" }>(), {
  size: 32,
  variant: "default",
});
const small = computed(() => props.variant === "small" || (props.variant === "default" && props.size <= 24));
const mono = computed(() => props.variant === "mono-dark");
const chip = computed(() => (mono.value ? chipVar.on : chipVar.primary));
const ink = computed(() => (mono.value ? chipVar.primary : chipVar.on));
</script>

<template>
  <svg
    class="bc-symbol"
    :width="size"
    :height="size"
    viewBox="0 0 64 64"
    fill="none"
    role="img"
    :aria-label="t('ui.logoLabel')"
    :data-variant="small ? 'small' : variant"
  >
    <template v-if="small">
      <rect x="4" y="4" width="56" height="56" rx="12" :fill="chip" />
      <text x="32" y="42.6" text-anchor="middle" font-family="Inter, system-ui, sans-serif" font-weight="800" font-size="30" letter-spacing="0.5" :fill="ink">AI</text>
    </template>
    <template v-else>
      <path
        d="M24 3.5V12M32 3.5V12M40 3.5V12M24 52V60.5M32 52V60.5M40 52V60.5M3.5 24H12M3.5 32H12M3.5 40H12M52 24H60.5M52 32H60.5M52 40H60.5"
        :stroke="mono ? chip : chipVar.pins"
        stroke-width="3.8"
        stroke-linecap="round"
      />
      <rect x="11" y="11" width="42" height="42" rx="9" :fill="chip" />
      <text x="32" y="39.8" text-anchor="middle" font-family="Inter, system-ui, sans-serif" font-weight="800" font-size="22" letter-spacing="0.5" :fill="ink">AI</text>
    </template>
  </svg>
</template>
