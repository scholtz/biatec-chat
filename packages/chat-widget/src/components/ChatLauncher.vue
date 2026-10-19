<script setup lang="ts">
defineProps<{ open: boolean; controls: string }>();
defineEmits<{ toggle: [] }>();
</script>

<template>
  <button
    type="button"
    class="bc-launcher"
    :aria-expanded="open"
    :aria-controls="controls"
    aria-label="Open Biatec Chat"
    @click="$emit('toggle')"
  >
    <svg viewBox="0 0 24 24" width="26" height="26" aria-hidden="true" focusable="false">
      <path fill="currentColor" d="M4 4h16a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H9l-5 4v-4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2z" />
    </svg>
  </button>
</template>
