import type { components } from "./generated/schema.js";

type Schemas = components["schemas"];

export type { components, paths } from "./generated/schema.js";

export type SessionState = Schemas["SessionState"];
export type AgentMode = Schemas["AgentModeValue"];
export type AgentSettings = Schemas["AgentSettings"];
export type AgentSettingsRequest = Schemas["AgentSettingsRequest"];
export type SessionTier = Schemas["SessionTier"];
export type SignInStatus = Schemas["SignInStatus"];
export type Suggestion = Schemas["Suggestion"];
export type SuggestionList = Schemas["SuggestionList"];
export type ChatMessageRequest = Schemas["ChatMessageRequest"];
export type HostContext = Schemas["HostContext"];
export type ActionDecisionRequest = Schemas["ActionDecisionRequest"];
export type ActionPreview = Schemas["ActionPreview"];
export type ActionPreviewStatus = Schemas["ActionPreviewStatus"];
export type ActionStep = Schemas["ActionStep"];
export type AssetAmount = Schemas["AssetAmount"];
export type LimitHeadroom = Schemas["LimitHeadroom"];
export type ConsentStatus = Schemas["ConsentStatus"];
export type ConsentState = Schemas["ConsentState"];
export type SafeCard = Schemas["SafeCard"];
export type SafeCardSafe = Schemas["SafeCardSafe"];
export type SafeCardSignerGroup = Schemas["SafeCardSignerGroup"];
export type SafeCardProposal = Schemas["SafeCardProposal"];
export type SafeCardProposalState = Schemas["SafeCardProposalState"];
export type SafeCardSource = Schemas["SafeCardSource"];
export type SafeCardCreationState = Schemas["SafeCardCreationState"];

export type TextDeltaEvent = Schemas["ChatEventTextDeltaEvent"];
export type ToolStatusEvent = Schemas["ChatEventToolStatusEvent"];
export type PreviewCardEvent = Schemas["ChatEventPreviewCardEvent"];
export type ConsentStatusEvent = Schemas["ChatEventConsentStatusEvent"];
export type SignInCardEvent = Schemas["ChatEventSignInCardEvent"];
export type SafeCardEvent = Schemas["ChatEventSafeCardEvent"];
export type ErrorEvent = Schemas["ChatEventErrorEvent"];

/** Server-sent event of a chat reply, discriminated by `type`. */
export type ChatEvent =
  | TextDeltaEvent
  | ToolStatusEvent
  | PreviewCardEvent
  | ConsentStatusEvent
  | SignInCardEvent
  | SafeCardEvent
  | ErrorEvent;

export type ChatEventType = ChatEvent["type"];

/**
 * Compile-time check that the hand-written union covers every variant the backend declares: if the backend adds
 * an event type, this stops compiling until it is added to ChatEvent above.
 */
export type AssertExhaustive<T extends true> = T;
export type _EveryVariantListed = AssertExhaustive<
  Schemas["ChatEvent"] extends ChatEvent ? true : false
>;
