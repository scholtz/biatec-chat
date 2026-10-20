# Public chat transport contract (`/public/*`)

The widget's cookie-less transport (`packages/chat-widget/src/transport/public.ts`) depends on the following. The backend
owns the implementation; the widget owns the expectations.

- `POST /public/chat` answers `text/event-stream`. Its response carries two headers the widget stores in memory:
  - `X-Biatec-Conversation`: the anonymous conversation capability.
  - `X-Biatec-Continuation`: the single-use continuation token for the next message.
- `/public/*` is answered cross-origin with `Access-Control-Allow-Origin: *`. Browsers hide non-safelisted response headers
  from cross-origin script, so **`Access-Control-Expose-Headers: X-Biatec-Conversation, X-Biatec-Continuation` is required**.
  `CorsMiddleware.PublicExposedHeaders` sets it; `PublicChatCors_ExposesCapabilityAndContinuationHeaders` fails if a name is dropped.
  Without it every message would start a new conversation.
- `GET /public/origin` answers `{ "registered": true|false }`. Only a 200 with `registered:false` marks the origin unregistered;
  any error, timeout or non-2xx is treated as transient and the widget keeps retrying so an outage never hides sign-in.
- Closed idle conversations answer `{ "code": "conversation_idle_closed" }`.
- SSE frames are `data: <json>` with a `type` of `text_delta`, `tool_status`, `preview_card`, `consent_status`, `sign_in_card`,
  `safe_card` or `error`, carrying that type's required fields. Incomplete final frames are discarded by the widget.

## Backend behaviour (T-038, `backend/Biatec.Chat.Api/Public/`)

- Request body: `{ message, conversationId, continuationToken, context, widgetVersion }`. The first message sends neither id nor token.
  `conversationId` is the 128-bit capability (stored only as a hash); every reply carries a fresh single-use token.
- Presenting the previous token once replays the stored assistant text and issues a new token; a second use is refused.
- `POST /session/adopt` (cookie transport, normal CSRF rules) takes `{ conversationId, continuationToken }` and retires the identifier.
- Refusals are `{ code, message }`: `rate_limited` (429, `Retry-After`), `conversation_unknown` (404), `conversation_adopted` (410),
  `conversation_authenticated` (403), `conversation_idle_closed` (410, after 30 min), `continuation_required` / `continuation_invalid` /
  `turn_in_progress` (409), `invalid_message` (400).
- Rate limits: Redis counters per salted address hash (salt rotates daily) and per Origin, in-memory fallback when Redis is down.
  Config section `PublicChat` (`IpPermitsPerMinute`, `OriginPermitsPerMinute`, `IdleMinutes`, `IpSalt`).
