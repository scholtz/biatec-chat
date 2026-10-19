import { Ban, CircleAlert, CircleCheck, CircleDashed, Clock, ShieldCheck, Timer, X } from "lucide-vue-next";
import type { Component } from "vue";

/** The design's fixed status set (docs/design/README.md "Preview status mapping"). Colour + icon + word, never colour alone. */
export type StatusKey =
  | "awaitingConfirmation"
  | "awaitingScope"
  | "awaitingApproval"
  | "signing"
  | "submitted"
  | "completed"
  | "rejected"
  | "failed"
  | "timedOut"
  | "cancelled"
  | "expired";

export type StatusTone = "warning" | "brand" | "info" | "success" | "neutral" | "danger";

export interface StatusSpec {
  tone: StatusTone;
  icon: Component;
  /** Dashed border until the user has confirmed. */
  dashed: boolean;
}

export const STATUS: Record<StatusKey, StatusSpec> = {
  awaitingConfirmation: { tone: "warning", icon: Clock, dashed: true },
  awaitingScope: { tone: "warning", icon: Clock, dashed: true },
  awaitingApproval: { tone: "warning", icon: Clock, dashed: true },
  signing: { tone: "brand", icon: ShieldCheck, dashed: false },
  submitted: { tone: "info", icon: CircleDashed, dashed: false },
  completed: { tone: "success", icon: CircleCheck, dashed: false },
  rejected: { tone: "neutral", icon: X, dashed: false },
  failed: { tone: "danger", icon: CircleAlert, dashed: false },
  timedOut: { tone: "danger", icon: Timer, dashed: false },
  cancelled: { tone: "neutral", icon: Ban, dashed: false },
  expired: { tone: "neutral", icon: Ban, dashed: false },
};

export const STATUS_KEYS = Object.keys(STATUS) as StatusKey[];
