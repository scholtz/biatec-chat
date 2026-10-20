<script setup lang="ts">
import type { SafeCard, SafeCardProposal, SafeCardSafe } from "@biatec/chat-protocol";
import { computed, type Component } from "vue";
import { CircleCheck, Clock, CirclePause, ShieldCheck } from "lucide-vue-next";
import { t } from "../../i18n";
import { networkLabel } from "./model";
import type { StatusTone } from "../../ui/status";

/**
 * A Biatec Safe card (T-074), built only from a safe_card event. Names, kinds and addresses come from the chain or
 * the user and are rendered as text (interpolation), never as HTML.
 */
const props = defineProps<{ card: SafeCard }>();

interface Badge {
  tone: StatusTone;
  icon: Component;
  label: string;
}

const net = computed(() => networkLabel({ network: props.card.network, genesisId: "" }));
const title = computed(() => t(props.card.source === "Create" ? "safe.createTitle" : "safe.listTitle"));

/** Integers from the contract may arrive as numbers or numeric strings (int32/int64 in the schema). */
const num = (v: number | string | null | undefined): number => {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
};

function safeBadge(s: SafeCardSafe): Badge {
  if (s.paused) return { tone: "neutral", icon: CirclePause, label: t("safe.paused") };
  if (s.creation === "Created") return { tone: "success", icon: CircleCheck, label: t("safe.creation.Created") };
  if (s.creation) return { tone: "warning", icon: Clock, label: t(`safe.creation.${s.creation}`) };
  return { tone: "info", icon: ShieldCheck, label: t("safe.active") };
}

function proposalBadge(p: SafeCardProposal): Badge {
  return p.status === "ReadyToExecute"
    ? { tone: "info", icon: CircleCheck, label: t("safe.state.ReadyToExecute") }
    : { tone: "warning", icon: Clock, label: t("safe.state.AwaitingApprovals") };
}

function percent(p: SafeCardProposal): number {
  const threshold = num(p.threshold);
  return threshold > 0 ? Math.min(100, Math.round((num(p.approvals) / threshold) * 100)) : 0;
}

const progressText = (p: SafeCardProposal) => t("safe.progress", { approvals: num(p.approvals), threshold: num(p.threshold) });
const hasEpoch = (e: number | string | null | undefined) => e !== null && e !== undefined && e !== "";
</script>

<template>
  <article class="bc-card bc-safe" data-border="solid" data-test="safe-card" :data-source="card.source" :aria-label="t('safe.label')">
    <header class="bc-card-head">
      <h3 class="bc-card-title">{{ title }}</h3>
      <span class="bc-tag" :data-network="net">{{ t(`cards.preview.${net}`) }}</span>
    </header>

    <section v-for="s in card.safes" :key="s.appId" class="bc-safe-item" data-test="safe" :aria-label="s.name">
      <div class="bc-card-head">
        <h4 class="bc-safe-name" data-test="safe-name">{{ s.name }}</h4>
        <span class="bc-badge" data-test="safe-status" :data-tone="safeBadge(s).tone">
          <component :is="safeBadge(s).icon" :size="16" :stroke-width="2" aria-hidden="true" />
          <span class="bc-badge-label">{{ safeBadge(s).label }}</span>
        </span>
      </div>
      <dl class="bc-kv">
        <dt>{{ t("safe.appId") }}</dt>
        <dd class="bc-num" data-test="safe-app-id">{{ s.appId }}</dd>
        <dt>{{ t("safe.address") }}</dt>
        <dd class="bc-num bc-break" data-test="safe-address">{{ s.address }}</dd>
        <template v-if="s.yourAddresses?.length">
          <dt>{{ t("safe.yourAddresses") }}</dt>
          <dd class="bc-num bc-break">
            <span v-for="a in s.yourAddresses" :key="a" class="bc-safe-line">{{ a }}</span>
          </dd>
        </template>
      </dl>

      <template v-if="s.signerGroups?.length">
        <h5 class="bc-safe-h">{{ t("safe.groups") }}</h5>
        <ul class="bc-steps">
          <li v-for="(g, gi) in s.signerGroups" :key="g.groupId ?? gi" class="bc-step" data-test="safe-group">
            <span class="bc-step-title">{{ t("safe.group", { name: g.name, threshold: num(g.threshold), members: num(g.memberCount) }) }}</span>
            <span v-if="g.isAdminGroup" class="bc-tag">{{ t("safe.admin") }}</span>
            <span class="bc-muted bc-num" data-test="safe-epoch">{{
              hasEpoch(g.membershipEpoch) ? t("safe.epoch", { epoch: String(g.membershipEpoch) }) : t("safe.epochUnknown")
            }}</span>
          </li>
        </ul>
      </template>

      <template v-if="card.source === 'List'">
        <h5 class="bc-safe-h">{{ t("safe.proposals") }}</h5>
        <p v-if="s.proposalsCouldNotBeRead" class="bc-hint" data-test="safe-proposals-unreadable">{{ t("safe.proposalsUnreadable") }}</p>
        <p v-else-if="!s.pendingProposals?.length" class="bc-hint">{{ t("safe.noProposals") }}</p>
        <ul v-else class="bc-steps">
          <li v-for="p in s.pendingProposals" :key="p.proposalId" class="bc-step bc-safe-proposal" data-test="safe-proposal">
            <div class="bc-card-head">
              <span class="bc-step-title">{{ t("safe.proposal", { id: p.proposalId, kind: p.kind }) }}</span>
              <span class="bc-badge" data-test="proposal-status" :data-tone="proposalBadge(p).tone">
                <component :is="proposalBadge(p).icon" :size="16" :stroke-width="2" aria-hidden="true" />
                <span class="bc-badge-label">{{ proposalBadge(p).label }}</span>
              </span>
            </div>
            <div
              class="bc-progress"
              role="progressbar"
              :aria-valuemin="0"
              :aria-valuemax="num(p.threshold)"
              :aria-valuenow="num(p.approvals)"
              :aria-valuetext="progressText(p)"
              :aria-label="progressText(p)"
            >
              <span class="bc-progress-fill" :style="{ width: `${percent(p)}%` }" />
            </div>
            <span class="bc-num" data-test="proposal-progress">{{ progressText(p) }}</span>
            <span v-if="p.approvedByYou !== null && p.approvedByYou !== undefined" class="bc-muted" data-test="proposal-you">{{
              t(p.approvedByYou ? "safe.approvedByYou" : "safe.notApprovedByYou")
            }}</span>
            <dl class="bc-kv bc-safe-kv">
              <dt>{{ t("safe.payloadVersion") }}</dt>
              <dd class="bc-num" data-test="proposal-version">{{ p.payloadVersion }}</dd>
              <dt>{{ t("safe.expiry") }}</dt>
              <dd class="bc-num" data-test="proposal-expiry">{{ p.expiryRound }}</dd>
              <dt>{{ t("safe.proposer") }}</dt>
              <dd class="bc-num bc-break">{{ p.proposer }}</dd>
            </dl>
          </li>
        </ul>
        <p v-if="s.proposalsTruncated" class="bc-hint">{{ t("safe.proposalsTruncated") }}</p>
      </template>
    </section>

    <p v-if="card.couldNotRead?.length" class="bc-hint" data-test="safe-could-not-read">
      {{ t("safe.couldNotRead", { ids: card.couldNotRead.join(", ") }) }}
    </p>
  </article>
</template>
