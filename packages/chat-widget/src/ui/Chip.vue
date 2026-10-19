<script setup lang="ts">
/** A static label by default; `interactive` renders a native button (optionally a toggle via `selected`). */
defineProps<{ interactive?: boolean; selected?: boolean; disabled?: boolean }>();
</script>

<template>
  <button
    v-if="interactive"
    type="button"
    class="bc-ui-chip"
    data-interactive="true"
    :aria-pressed="selected === undefined ? undefined : selected"
    :disabled="disabled"
  >
    <slot />
  </button>
  <span v-else class="bc-ui-chip" data-interactive="false"><slot /></span>
</template>
