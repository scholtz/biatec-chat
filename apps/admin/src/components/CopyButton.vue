<script setup lang="ts">
import { ref } from "vue";

const props = defineProps<{ value: string; label: string }>();
const done = ref(false);

async function copy() {
  try {
    await navigator.clipboard.writeText(props.value);
    done.value = true;
    setTimeout(() => (done.value = false), 1500);
  } catch { /* clipboard unavailable: the value stays selectable on screen */ }
}
</script>

<template>
  <button type="button" class="btn btn-small btn-ghost" :aria-label="`Copy ${label}`" data-test="copy" @click.stop="copy">
    {{ done ? "Copied" : "Copy" }}
  </button>
</template>
