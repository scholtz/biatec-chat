<script setup lang="ts">
import { ref } from "vue";
import { nextUid } from "./uid";

/** `label` is required: it is the accessible name and the tooltip text. */
const props = defineProps<{ label: string; disabled?: boolean }>();
if (!props.label) console.warn("[biatec-chat] IconButton requires a label (aria-label)");

const tipId = `bc-tip-${nextUid()}`;
const tip = ref(false);
</script>

<template>
  <span class="bc-iconbtn-wrap" @mouseenter="tip = true" @mouseleave="tip = false">
    <button
      type="button"
      class="bc-iconbtn"
      :aria-label="label"
      :aria-describedby="tip ? tipId : undefined"
      :disabled="disabled"
      @focus="tip = true"
      @blur="tip = false"
      @keydown.esc="tip = false"
    >
      <slot />
    </button>
    <span v-if="tip" :id="tipId" role="tooltip" class="bc-tooltip">{{ label }}</span>
  </span>
</template>
