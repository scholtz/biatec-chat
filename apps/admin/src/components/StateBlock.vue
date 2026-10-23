<script setup lang="ts">
// Loading skeleton, error with retry, or empty message; the wrapper is an aria-live region so changes are announced.
defineProps<{ loading: boolean; error: string; empty?: boolean; emptyText?: string; rows?: number }>();
const emit = defineEmits<{ (e: "retry"): void }>();
</script>

<template>
  <div aria-live="polite" class="state-block">
    <div v-if="loading" class="skeleton-list" aria-busy="true">
      <span class="sr-only">Loading…</span>
      <div v-for="n in rows ?? 4" :key="n" class="skeleton" aria-hidden="true"></div>
    </div>
    <div v-else-if="error" class="notice notice-error" role="alert">
      <span data-test="error-text">{{ error }}</span>
      <button type="button" class="btn btn-small" data-test="retry" @click="emit('retry')">Retry</button>
    </div>
    <p v-else-if="empty" class="empty" data-test="empty">{{ emptyText ?? "Nothing to show." }}</p>
    <slot v-else />
  </div>
</template>
