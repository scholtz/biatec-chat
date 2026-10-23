<script setup lang="ts">
import { computed } from "vue";
import { statusGlyph, statusTone } from "../format";

const props = defineProps<{ status: string }>();
const tone = computed(() => statusTone(props.status));
</script>

<template>
  <span class="badge" :class="`badge-${tone}`"><span aria-hidden="true">{{ statusGlyph(tone) }}</span> {{ status }}</span>
</template>
