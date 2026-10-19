<script setup lang="ts">
import { ref } from "vue";
import { BaiSymbol } from "../ui";
import { t } from "../i18n";

/** Floating launcher: 60 px brand button with the mono-dark chip, a tooltip and the unread badge. */
defineProps<{ controls: string; unread: number }>();
defineEmits<{ open: [] }>();
const tip = ref(false);
const el = ref<HTMLButtonElement | null>(null);
defineExpose({ focus: () => el.value?.focus() });
</script>

<template>
  <div class="bc-launcher-wrap" @mouseenter="tip = true" @mouseleave="tip = false">
    <span v-if="tip" id="bc-launcher-tip" role="tooltip" class="bc-launcher-tip">{{ t("shell.launcher") }}</span>
    <button
      ref="el"
      type="button"
      class="bc-launcher"
      aria-expanded="false"
      aria-haspopup="dialog"
      :aria-controls="controls"
      :aria-label="t('shell.launcher')"
      :aria-describedby="unread ? 'bc-launcher-unread' : undefined"
      @focus="tip = true"
      @blur="tip = false"
      @keydown.esc="tip = false"
      @click="$emit('open')"
    >
      <BaiSymbol :size="44" variant="mono-dark" aria-hidden="true" />
      <span v-if="unread" class="bc-launcher-badge" data-testid="unread" aria-hidden="true">{{ unread > 9 ? "9+" : unread }}</span>
    </button>
    <span v-if="unread" id="bc-launcher-unread" class="bc-sr-only">{{ t("shell.unread", { count: unread }) }}</span>
  </div>
</template>
