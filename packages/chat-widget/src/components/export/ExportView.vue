<script setup lang="ts">
import { computed, onMounted, ref, watch } from "vue";
import type { ConversationSummary } from "@biatec/chat-protocol";
import { Download, Trash2 } from "lucide-vue-next";
import { Button, Dialog } from "../../ui";
import { nextUid } from "../../ui/uid";
import { i18n, t } from "../../i18n";
import { RetentionError, combineBundles, saveFile, type EvidenceFile, type RetentionApi, type RetentionErrorCode } from "./api";

/**
 * "Your conversations": the user's own conversations with a per-conversation evidence download, the receipts
 * carve-out note, and (signed in only) erasure behind a confirm step that offers the export first. Everything shown
 * comes from the backend and is rendered as text.
 */
const props = defineProps<{
  api: RetentionApi;
  /** Erasure is signed-in only; a guest gets the 30-day retention note instead. */
  signedIn: boolean;
  /**
   * Changes whenever the conversation moved on (a finished turn, a preview or receipt update, a reset): the list is
   * then re-read so the current conversation's evidence count is up to date. The view also loads when it is shown.
   */
  revision?: number;
}>();
const emit = defineEmits<{
  /** The backend moved the session to a fresh conversation (its id, when the backend returned one). */
  erased: [conversationId: string | null];
}>();

const headingId = `bc-export-h-${nextUid()}`;
const root = ref<HTMLElement | null>(null);
const conversations = ref<ConversationSummary[]>([]);
const loading = ref(false);
const loadFailed = ref(false);
const downloading = ref<string | null>(null);
/** The row whose own download failed last. */
const rowFailed = ref<string | null>(null);
const status = ref("");

const confirmOpen = ref(false);
const erasing = ref(false);
const exporting = ref(false);
const exported = ref(false);
/** Conversations "export first" could not fetch; the combined file then lacks them and the user is told which. */
const exportFailed = ref<ConversationSummary[]>([]);
const eraseError = ref<RetentionErrorCode | null>(null);

const entries = (c: ConversationSummary) => Number(c.evidenceEntries) || 0;
const withEvidence = computed(() => conversations.value.filter((c) => entries(c) > 0));

function when(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return new Intl.DateTimeFormat(String(i18n.global.locale.value), { dateStyle: "medium", timeStyle: "short" }).format(d);
}

let loadSeq = 0;
async function load(): Promise<void> {
  const seq = ++loadSeq;
  loading.value = true;
  loadFailed.value = false;
  try {
    const list = await props.api.list();
    if (seq === loadSeq) conversations.value = list;
  } catch {
    if (seq !== loadSeq) return;
    // A stale list (for example the one from before an erase) is worse than none.
    conversations.value = [];
    loadFailed.value = true;
  } finally {
    if (seq === loadSeq) loading.value = false;
  }
}
onMounted(load);
// Signing in adds the user's other conversations in this tenant; a new turn or receipt adds evidence entries.
watch(() => [props.signedIn, props.revision], load);

async function download(c: ConversationSummary): Promise<void> {
  downloading.value = c.conversationId;
  rowFailed.value = null;
  try {
    saveFile(await props.api.evidence(c.conversationId), root.value);
  } catch {
    rowFailed.value = c.conversationId;
  } finally {
    downloading.value = null;
  }
}

function openConfirm(): void {
  eraseError.value = null;
  exported.value = false;
  exportFailed.value = [];
  status.value = "";
  confirmOpen.value = true;
}
function closeConfirm(): void {
  if (erasing.value) return;
  confirmOpen.value = false;
}

/**
 * The confirm step's "export first": every conversation that has evidence, saved as ONE file (browsers block a
 * second programmatic download). "Evidence downloaded" only when every bundle made it into that file.
 */
async function exportAll(): Promise<void> {
  exporting.value = true;
  exported.value = false;
  exportFailed.value = [];
  try {
    const targets = withEvidence.value;
    const results = await Promise.allSettled(targets.map((c) => props.api.evidence(c.conversationId)));
    const files: EvidenceFile[] = [];
    const failed: ConversationSummary[] = [];
    results.forEach((r, i) => (r.status === "fulfilled" ? files.push(r.value) : failed.push(targets[i]!)));
    if (files.length) {
      try {
        saveFile(combineBundles(files), root.value);
      } catch {
        failed.splice(0, failed.length, ...targets);
      }
    }
    exportFailed.value = failed;
    exported.value = failed.length === 0;
  } finally {
    exporting.value = false;
  }
}

async function erase(): Promise<void> {
  erasing.value = true;
  eraseError.value = null;
  try {
    const r = await props.api.erase();
    confirmOpen.value = false;
    status.value = t("export.erased");
    emit("erased", r.conversationId ?? null);
    await load();
  } catch (e) {
    eraseError.value = e instanceof RetentionError ? e.code : "failed";
  } finally {
    erasing.value = false;
  }
}

const eraseErrorText = computed(() => {
  switch (eraseError.value) {
    case null:
      return "";
    case "action_in_flight":
      return t("export.actionInFlight");
    case "unauthorized":
      return t("export.unauthorized");
    default:
      return t("export.eraseFailed");
  }
});
</script>

<template>
  <section ref="root" class="bc-export" data-testid="export" :aria-labelledby="headingId">
    <h3 :id="headingId" class="bc-export-h">{{ t("export.title") }}</h3>
    <p class="bc-export-sub">{{ t("export.intro") }}</p>

    <p v-if="loading && !conversations.length" class="bc-export-sub" role="status">{{ t("export.loading") }}</p>
    <div v-else-if="loadFailed" class="bc-export-row-actions">
      <p class="bc-export-error" role="alert" data-testid="export-load-failed">{{ t("export.loadFailed") }}</p>
      <Button variant="secondary" size="sm" data-testid="export-retry" @click="load()">{{ t("export.retry") }}</Button>
    </div>
    <p v-else-if="!conversations.length" class="bc-export-sub" data-testid="export-empty">{{ t("export.empty") }}</p>

    <ul v-if="conversations.length" class="bc-export-list">
      <li
        v-for="c in conversations"
        :key="c.conversationId"
        class="bc-export-item"
        data-testid="export-conversation"
        :data-id="c.conversationId"
        :data-current="c.current ? '' : undefined"
      >
        <p class="bc-export-title">
          <span class="bc-export-num">{{ t("export.started", { date: when(c.createdAt) }) }}</span>
          <span v-if="c.current" class="bc-export-current" data-testid="export-current">{{ t("export.current") }}</span>
        </p>
        <p class="bc-export-meta bc-export-num">{{ t("export.entries", { count: entries(c) }) }}</p>
        <Button
          v-if="entries(c) > 0"
          variant="secondary"
          size="sm"
          data-testid="export-download"
          :loading="downloading === c.conversationId"
          :aria-label="t('export.downloadLabel', { date: when(c.createdAt) })"
          @click="download(c)"
        >
          <Download :size="16" aria-hidden="true" />
          {{ t("export.download") }}
        </Button>
        <p v-else class="bc-export-meta">{{ t("export.noEvidence") }}</p>
        <p v-if="rowFailed === c.conversationId" class="bc-export-error" role="alert" data-testid="export-download-failed">
          {{ t("export.downloadFailed") }}
        </p>
      </li>
    </ul>

    <p class="bc-export-note" data-testid="export-carve-out">{{ t("export.carveOut") }}</p>

    <Button v-if="signedIn" variant="danger" size="sm" data-testid="export-erase" @click="openConfirm()">
      <Trash2 :size="16" aria-hidden="true" />
      {{ t("export.erase") }}
    </Button>
    <p v-else class="bc-export-sub" data-testid="export-erase-guest">{{ t("export.eraseGuest") }}</p>
    <p class="bc-export-sub" role="status" data-testid="export-status">{{ status }}</p>

    <Dialog :open="confirmOpen" :title="t('export.eraseTitle')" @close="closeConfirm()">
      <div class="bc-export-confirm" data-testid="erase-confirm">
        <p>{{ t("export.eraseText") }}</p>
        <p class="bc-export-sub">{{ t("export.eraseKept") }}</p>
        <p class="bc-export-note">{{ t("export.carveOut") }}</p>
        <Button
          v-if="withEvidence.length"
          variant="secondary"
          size="md"
          data-testid="erase-export-first"
          :loading="exporting"
          :disabled="erasing"
          @click="exportAll()"
        >
          <Download :size="16" aria-hidden="true" />
          {{ exported ? t("export.exportFirstDone") : t("export.exportFirst") }}
        </Button>
        <div v-if="exportFailed.length" class="bc-export-error" role="alert" data-testid="erase-export-failed">
          <p>{{ t("export.exportPartial", { count: exportFailed.length }) }}</p>
          <ul class="bc-export-failed">
            <li v-for="c in exportFailed" :key="c.conversationId" :data-id="c.conversationId">
              {{ t("export.started", { date: when(c.createdAt) }) }}
            </li>
          </ul>
        </div>
        <p v-if="eraseErrorText" class="bc-export-error" role="alert" data-testid="erase-error" :data-code="eraseError">
          {{ eraseErrorText }}
        </p>
        <div class="bc-export-footer">
          <Button variant="ghost" size="md" data-testid="erase-cancel" :disabled="erasing" @click="closeConfirm()">
            {{ t("export.cancel") }}
          </Button>
          <Button variant="danger" size="md" data-testid="erase-confirm-button" :loading="erasing" :disabled="exporting" @click="erase()">
            <Trash2 :size="16" aria-hidden="true" />
            {{ t("export.eraseConfirm") }}
          </Button>
        </div>
      </div>
    </Dialog>
  </section>
</template>
