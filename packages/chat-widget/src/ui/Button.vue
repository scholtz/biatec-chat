<script setup lang="ts">
import Spinner from "./Spinner.vue";

export type ButtonVariant = "primary" | "secondary" | "ghost" | "danger" | "soft";
export type ButtonSize = "sm" | "md" | "lg";

withDefaults(
  defineProps<{ variant?: ButtonVariant; size?: ButtonSize; disabled?: boolean; loading?: boolean; type?: "button" | "submit" }>(),
  { variant: "primary", size: "md", type: "button" },
);
</script>

<template>
  <button
    :type="type"
    class="bc-btn"
    :data-variant="variant"
    :data-size="size"
    :disabled="disabled || loading"
    :aria-busy="loading ? 'true' : undefined"
  >
    <Spinner v-if="loading" decorative />
    <slot />
  </button>
</template>
