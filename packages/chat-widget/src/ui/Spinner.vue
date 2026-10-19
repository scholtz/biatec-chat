<script setup lang="ts">
import { t } from "../i18n";

/** `decorative` hides it from assistive tech when the parent already announces the busy state. */
defineProps<{ decorative?: boolean }>();
</script>

<template>
  <span v-if="decorative" class="bc-spinner" aria-hidden="true" />
  <span v-else class="bc-spinner" role="status" :aria-label="t('ui.loading')" />
</template>
