<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from "vue";
import { api } from "../api";
import { REASON_MAX, cleanReason, shortMiddle, validateReason } from "../format";
import { navigate } from "../router";
import { setImpersonation } from "../session";

const props = defineProps<{ target: { tenantId: string; sub: string } }>();
const emit = defineEmits<{ (e: "close"): void }>();

const reason = ref("");
const touched = ref(false);
const submitting = ref(false);
const serverError = ref("");
const area = ref<HTMLTextAreaElement | null>(null);
const dialog = ref<HTMLElement | null>(null);
let opener: Element | null = null;

const validation = computed(() => validateReason(reason.value));
const shownError = computed(() => (touched.value ? validation.value : null));

async function submit() {
  touched.value = true;
  serverError.value = "";
  if (validation.value || submitting.value) return;
  submitting.value = true;
  try {
    const imp = await api.startImpersonation(props.target.tenantId, props.target.sub, cleanReason(reason.value));
    setImpersonation(imp);
    emit("close");
    navigate("/view");
  } catch (e) {
    serverError.value = e instanceof Error ? e.message : "Could not start.";
  } finally {
    submitting.value = false;
  }
}

function onKey(e: KeyboardEvent) {
  if (e.key === "Escape") {
    emit("close");
    return;
  }
  if (e.key !== "Tab" || !dialog.value) return;
  const items = Array.from(dialog.value.querySelectorAll<HTMLElement>("button, textarea, [href]")).filter(
    (el) => !(el as HTMLButtonElement).disabled,
  );
  if (!items.length) return;
  const first = items[0];
  const last = items[items.length - 1];
  if (e.shiftKey && document.activeElement === first) {
    e.preventDefault();
    last.focus();
  } else if (!e.shiftKey && document.activeElement === last) {
    e.preventDefault();
    first.focus();
  }
}

onMounted(async () => {
  opener = document.activeElement;
  document.addEventListener("keydown", onKey);
  await nextTick();
  area.value?.focus();
});
onBeforeUnmount(() => {
  document.removeEventListener("keydown", onKey);
  if (opener instanceof HTMLElement) opener.focus();
});
</script>

<template>
  <div class="modal-backdrop" @mousedown.self="emit('close')">
    <div ref="dialog" class="modal card" role="dialog" aria-modal="true" aria-labelledby="imp-title" data-test="modal">
      <h2 id="imp-title">View as user</h2>
      <p class="muted">
        You will see the conversations of subject <strong class="mono" :title="target.sub">{{ shortMiddle(target.sub, 24) }}</strong>, read-only.
        The start, every conversation you open and the stop are written to the audit chain with your reason, permanently.
        Give a ticket or case reference; do not put the user's personal data in it.
      </p>
      <form novalidate @submit.prevent="submit">
        <label for="imp-reason" class="label">Reason (shown in the audit trail)</label>
        <textarea
          id="imp-reason"
          ref="area"
          v-model="reason"
          class="input"
          rows="4"
          :maxlength="REASON_MAX + 200"
          :aria-invalid="shownError ? 'true' : 'false'"
          aria-describedby="imp-help"
          @blur="touched = true"
        ></textarea>
        <div id="imp-help" class="field-help">
          <span v-if="shownError" class="field-error" role="alert" data-test="reason-error">{{ shownError }}</span>
          <span v-else class="muted">8 to {{ REASON_MAX }} characters.</span>
          <span class="muted counter" data-test="counter">{{ cleanReason(reason).length }} / {{ REASON_MAX }}</span>
        </div>
        <p v-if="serverError" class="notice notice-error" role="alert" data-test="server-error">{{ serverError }}</p>
        <div class="modal-actions">
          <button type="button" class="btn" data-test="cancel" @click="emit('close')">Cancel</button>
          <button type="submit" class="btn btn-primary" :disabled="submitting" data-test="start">
            {{ submitting ? "Starting…" : "Start viewing" }}
          </button>
        </div>
      </form>
    </div>
  </div>
</template>
