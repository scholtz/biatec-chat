# biatec-chat on AWS: AgentCore, Identity, Gateway, Policy, Guardrails

> **How AWS is actually set up today: [aws-setup.md](aws-setup.md).**
>
> **Superseded in part by `PRODUCT-DESCRIPTION.md` (2026-10-19).** The product puts an ASP.NET Core 10 **BFF** in
> charge. The browser never holds a token; the backend is a *confidential* OIDC client; the backend calls Bedrock
> Converse itself; signing goes to Biatec OIDC directly, never through MCP `signTransaction`. Sections 1, 3.1–3.3,
> 4 and 5 (AgentCore Runtime and Gateway, SPA token, public client) therefore **don't apply to v1**.
> Still valid: section 2 constants (hosts, token audience, tool names), section 7 Guardrails (create JSON,
> versioning, `ApplyGuardrail` on untrusted tool results, sync streaming, logging caveat), the Bedrock notes on
> `maxTokens`, caching and throttling, and section 8 cost and cleanup. Section 6 Cedar is optional.

Implementation guide for coding agents. The design decisions below are already made and the API shapes were checked
against the AWS docs and the botocore model (AgentCore control plane `2023-06-05`) on 2026-10-19. Build from this
file; only go back to the docs for items marked **VERIFY**.

The hackathon rules in the parent `../AGENTS.md` (region, no IAM users, Claude 4.5 only, budget) still apply and win
on conflict.

---

## 1. Architecture (decided)

```
Browser SPA (biatec-chat)
  │ 1. OAuth code + PKCE against BiatecOIDC, client_id=biatec-chat,
  │    scope="openid profile email sign", resource=https://mcp.biatec.io/tools
  │ 2. POST /invocations with  Authorization: Bearer <BiatecOIDC access token>
  ▼
AgentCore Runtime  "biatec_chat_agent"  (Python, Strands, PUBLIC network mode)
  │  inbound: CUSTOM_JWT authorizer → BiatecOIDC discovery, allowedClients=[biatec-chat]
  │  requestHeaderAllowlist=[Authorization] → agent code reads the user's token
  │  model: eu.anthropic.claude-sonnet-4-5-20250929-v1:0 via Converse + Guardrail
  │ 3. MCP (streamable HTTP) with the same Bearer token
  ▼
AgentCore Gateway  "biatec-chat-gw"  (MCP, CUSTOM_JWT inbound, same issuer/client)
  │  REQUEST interceptor Lambda: copies the inbound Authorization header to the target
  │  Policy engine (Cedar): permit/forbid per tool and per tool argument
  ├─ target "biatec-mcp": MCP server https://mcp.biatec.io/tools, listingMode=DYNAMIC, no credential provider
  └─ (optional) more targets: Lambda/OpenAPI tools (sanctions check, TSYS API via API-key provider)
  ▼
BiatecMCP (k8s, outside AWS) validates aud=https://mcp.biatec.io/tools + `sign` claim,
forwards the token to BiatecOIDC wallet API, which signs within the user's spending limits.
```

### Why these choices

| Decision | Reason |
|---|---|
| **The user's BiatecOIDC token is used end to end** | BiatecMCP accepts only BiatecOIDC tokens whose `aud` contains `https://mcp.biatec.io/tools`. Its signing tools also require the `sign` claim. A token requested with `resource=https://mcp.biatec.io/tools` carries `aud=["biatec-chat","https://mcp.biatec.io/tools"]` and `client_id=biatec-chat`, so a single token satisfies Runtime, Gateway and BiatecMCP. |
| **Gateway → BiatecMCP uses an interceptor, not Gateway OAuth** | MCP-server targets don't support `JWT_PASSTHROUGH` (it's for Runtime HTTP targets only). `TOKEN_EXCHANGE` needs RFC 8693, which BiatecOIDC doesn't implement. `AUTHORIZATION_CODE` (3LO) would add a second consent flow, a confidential client, callback registration and a `CompleteResourceTokenAuth` session-binding endpoint. An `Authorization` header returned by a REQUEST interceptor *is* forwarded to the target, which is the documented way to do this. |
| **`listingMode=DYNAMIC`** | BiatecMCP's whole `/tools` endpoint requires auth (`MapMcp("/tools").RequireAuthorization()`). DEFAULT mode syncs tools at target-creation time without a user token, so it would get 401. DYNAMIC forwards `tools/list` at call time, so the user's token (via the interceptor) is present. Trade-off: no semantic tool search, which we don't need for 20 tools. |
| **Runtime rather than Harness** | We need custom code: reading the user's token, building a per-user MCP client, and a guardrail hook on tool results. Harness is config-only. |
| **PUBLIC network mode** | VPC mode would need a NAT gateway (bills while idle, against the hackathon rules) to reach `mcp.biatec.io` and `oidc.biatec.io`. The Runtime still sits behind the JWT authorizer. |
| **AgentCore Payments: not used** | It supports Ethereum/Base and Solana only (Coinbase CDP, Stripe Privy), not Algorand. Biatec's own x402 handling stays. |
| **Phase 0 shortcut** | Until the Gateway exists, the agent may point its MCP client straight at `https://mcp.biatec.io/tools` with the same token. Everything else stays the same, so Gateway can be added later by changing one URL. |

---

## 2. Constants

| Name | Value |
|---|---|
| AWS account | `057691001760` |
| Region | `eu-central-1` for everything in this guide |
| Tag on every resource | `Project=biatec` |
| BiatecOIDC issuer (prod / stage) | `https://oidc.biatec.io` / `https://stage.oidc.biatec.io` |
| OIDC discovery URL | `<issuer>/.well-known/openid-configuration` (RS256, JWKS at `/.well-known/jwks.json`) |
| BiatecMCP endpoint = token audience (prod / stage) | `https://mcp.biatec.io/tools` / `https://stage.mcp.biatec.io/tools` |
| OAuth client for the chat | `biatec-chat` (public client, PKCE, no secret) |
| Access token lifetime | **15 min** (refresh token 30 days), so the SPA must refresh before each invoke |
| Default model | `eu.anthropic.claude-sonnet-4-5-20250929-v1:0` (Haiku 4.5 `eu.anthropic.claude-haiku-4-5-20251001-v1:0` for cheap steps) |
| Gateway tool name format | `<targetName>___<toolName>` (three underscores), e.g. `biatec-mcp___submitTransactionToBlockchain` |

BiatecMCP tool names (camelCase): `listAlgorandAddresses`, `listSupportedNetworks`, `getCryptoAddress`,
`getCryptoBalance`, `getAddressInfo`, `listActiveAddresses`, `getMultisigAddress`, `getBridgeConfiguration`,
`createPaymentTransaction`, `createOptInTransaction`, `createAssetCreateTransaction`, `createSwapTransaction`,
`createBridgeTransaction`, `createMultisigTransaction`, `convertToMultisigTransactions`, `createBitcoinTransaction`,
`mergeMultisigTransactions`, `activateCryptoAddress`, `signTransaction`, `submitTransactionToBlockchain`.
The flow is always **build → sign → submit**, and every tool takes a `network` that must stay the same across all
three steps.

---

## 3. Identity

### 3.1 Register the `biatec-chat` client in BiatecOIDC (human/operator task)

Ask the user to do this; agents can't change the production k8s secret. Prod `JwtIssuer:*` lives in the
`google-account-main-app-secret` k8s secret as env vars, and stage lives in `k8s/stage/conf-oidc-stage/appsettings.json`
(repo `AlgorandGoogleDriveAccount`). Entry:

```json
{
  "ClientId": "biatec-chat",
  "DisplayName": "Biatec Chat",
  "ClientSecret": null,
  "RedirectUris": ["https://<chat-host>/auth/callback", "http://localhost:5173/auth/callback"],
  "PostLogoutRedirectUris": ["https://<chat-host>/", "http://localhost:5173/"],
  "AllowedScopes": ["openid", "profile", "email", "sign"]
}
```

- `sign` **must** be in `AllowedScopes`. Requesting a scope that isn't allowlisted fails the whole `/authorize` with
  `invalid_scope`.
- Leave out `manage-limits` and `rekey`. The chat agent must not change spending limits or rekey accounts.
- `JwtIssuer:ProtectedResources` must already contain `https://mcp.biatec.io/tools`; it does in prod and stage.

### 3.2 Token the SPA obtains

Authorization code + PKCE (S256) against `<issuer>/authorize`, then `<issuer>/token`, with
`resource=https://mcp.biatec.io/tools` on **both** requests. The access token then contains:
`iss`, `sub`, `client_id=biatec-chat`, `aud=["biatec-chat","https://mcp.biatec.io/tools"]`, `scope`, `sign`,
`primary_seed_address`, `biatec_idp`, `exp` (15 min). Refresh with `grant_type=refresh_token` (no secret).
`?idp=google` or `?idp=microsoft` on `/authorize` skips the provider picker. The full contract is in
`AlgorandGoogleDriveAccount/BiatecOIDC/OIDC_INTEGRATION_GUIDE.md`.

### 3.3 Where each layer validates the token

| Hop | Validator | Config |
|---|---|---|
| SPA → Runtime | AgentCore `customJWTAuthorizer` | `discoveryUrl`=BiatecOIDC, `allowedClients=["biatec-chat"]` (matches the `client_id` claim) |
| Runtime → Gateway | AgentCore `customJWTAuthorizer` on the gateway | same as above |
| Gateway → BiatecMCP | BiatecMCP `AddJwtBearer` | `aud` must contain `https://mcp.biatec.io/tools` |
| BiatecMCP → BiatecOIDC wallet API | BiatecOIDC | `sign` claim for signing/activation; spending limits enforced server-side |

AgentCore Identity creates a workload identity for the Runtime and the Gateway automatically; we use no outbound
credential provider for BiatecMCP. Use an **API-key credential provider** only for extra third-party targets (for
example TSYS). Never put the key on the command line: `export API_KEY=...` and then
`aws bedrock-agentcore-control create-api-key-credential-provider --name tsys-api-key --api-key "$API_KEY" --region eu-central-1`.

---

## 4. Runtime (the agent)

### 4.1 Project layout to create in this repo

```
agent/
  main.py            # BedrockAgentCoreApp entrypoint (below)
  guard.py           # ApplyGuardrail hook for tool results
  pyproject.toml     # strands-agents, strands-agents-tools (optional), bedrock-agentcore, mcp, boto3
infra/               # CDK (Python or TS) or scripts; tag everything Project=biatec
  interceptor/handler.py
web/                 # SPA
```

Fastest deploy path: the **AgentCore CLI** (`agentcore create` / `agentcore add agent ... --authorizer-type
CUSTOM_JWT --discovery-url ... --allowed-clients biatec-chat --request-header-allowlist Authorization` /
`agentcore deploy`). It creates the execution role and packages the code. Check CLI flags with `agentcore --help`
before use.

The raw API alternative needs no container: `agentRuntimeArtifact.codeConfiguration` with
`runtime=PYTHON_3_13`, `entryPoint=["main.py"]`, `code.s3={bucket,prefix}`. Runtime runs on **ARM64**, so vendored
dependencies must be aarch64 wheels (e.g. `uv pip install --python-platform aarch64-manylinux2014 --target pkg ...`).
If you use a container instead, it must be `linux/arm64` and listen on port 8080 (`/ping`, `/invocations`).

### 4.2 Agent code

```python
# agent/main.py
import os
import boto3
from botocore.config import Config
from bedrock_agentcore.runtime import BedrockAgentCoreApp
from mcp.client.streamable_http import streamablehttp_client
from strands import Agent
from strands.models import BedrockModel
from strands.tools.mcp import MCPClient
from guard import ToolResultGuard

REGION = "eu-central-1"
MCP_URL = os.environ["MCP_URL"]  # Gateway URL (…/mcp) or, in phase 0, https://mcp.biatec.io/tools
GUARDRAIL_ID = os.environ["GUARDRAIL_ID"]
GUARDRAIL_VERSION = os.environ["GUARDRAIL_VERSION"]  # numbered, never DRAFT

SYSTEM_PROMPT = """You are Biatec Chat, a self-custody wallet assistant. ...
Always: build → show the user the exact transaction → wait for explicit confirmation → sign → submit.
Never ask for, accept, or repeat a mnemonic or private key. Keep `network` identical across build/sign/submit."""

app = BedrockAgentCoreApp()
_sessions: dict[str, list] = {}  # session_id → messages; lives as long as the Runtime session microVM

def _model() -> BedrockModel:
    return BedrockModel(
        model_id="eu.anthropic.claude-sonnet-4-5-20250929-v1:0",
        region_name=REGION,
        max_tokens=2048,
        temperature=0.2,
        guardrail_id=GUARDRAIL_ID,
        guardrail_version=GUARDRAIL_VERSION,
        guardrail_trace="disabled",
        guardrail_stream_processing_mode="sync",  # never "async": it streams before the guardrail decides
        cache_tools="default",                     # tool list (~20 MCP tools) is resent every turn
        boto_client_config=Config(retries={"max_attempts": 6, "mode": "adaptive"}),
    )

@app.entrypoint
async def invoke(payload, context):
    headers = getattr(context, "request_headers", None) or {}
    auth = next((v for k, v in headers.items() if k.lower() == "authorization"), "")
    if not auth.startswith("Bearer "):
        yield {"error": "missing bearer token"}
        return

    # One MCP client per request: the token is per user and expires after 15 min.
    mcp = MCPClient(lambda: streamablehttp_client(MCP_URL, headers={"Authorization": auth}))
    with mcp:
        agent = Agent(
            model=_model(),
            system_prompt=[{"text": SYSTEM_PROMPT}, {"cachePoint": {"type": "default"}}],
            tools=mcp.list_tools_sync(),
            messages=_sessions.get(context.session_id, []),
            hooks=[ToolResultGuard(GUARDRAIL_ID, GUARDRAIL_VERSION, REGION)],
        )
        async for event in agent.stream_async(payload.get("prompt", "")):
            if "data" in event:
                yield {"text": event["data"]}
        _sessions[context.session_id] = agent.messages

if __name__ == "__main__":
    app.run()
```

Notes:
- Never log `auth`, and never put the token into the prompt or a tool argument. It travels only in HTTP headers.
- In-memory `_sessions` is lost when the session microVM is recycled (idle timeout, configurable via
  `lifecycleConfiguration.idleRuntimeSessionTimeout`). For durable history use **AgentCore Memory** (short-term
  events) with the Strands AgentCore Memory session manager. This is optional for the demo.
- Strands also offers `cache_config=CacheConfig(strategy="auto")`, which replaces the deprecated `cache_tools`. Check
  the import path in `strands.models.bedrock`.
- Prompt caching only kicks in above the model's minimum prefix length (about 1K tokens for Sonnet 4.5). The tool
  list usually exceeds it. Confirm with `cacheReadInputTokens` > 0 in the usage metrics.

### 4.3 Create the Runtime (API shape)

```python
ctl = boto3.client("bedrock-agentcore-control", region_name="eu-central-1")
ctl.create_agent_runtime(
    agentRuntimeName="biatec_chat_agent",
    agentRuntimeArtifact={"codeConfiguration": {
        "code": {"s3": {"bucket": "<team-bucket>", "prefix": "agent/agent.zip"}},
        "runtime": "PYTHON_3_13", "entryPoint": ["main.py"]}},
    roleArn="<runtime-execution-role-arn>",
    networkConfiguration={"networkMode": "PUBLIC"},
    protocolConfiguration={"serverProtocol": "HTTP"},
    authorizerConfiguration={"customJWTAuthorizer": {
        "discoveryUrl": "https://oidc.biatec.io/.well-known/openid-configuration",
        "allowedClients": ["biatec-chat"]}},
    requestHeaderConfiguration={"requestHeaderAllowlist": ["Authorization"]},
    environmentVariables={"MCP_URL": "...", "GUARDRAIL_ID": "...", "GUARDRAIL_VERSION": "1"},  # no secrets here
    tags={"Project": "biatec"},
)
```

Poll `get_agent_runtime` until `READY`. Use the `DEFAULT` endpoint, or create a named one with
`create_agent_runtime_endpoint`.

**Execution role** (trust `bedrock-agentcore.amazonaws.com`, with `aws:SourceAccount=057691001760`). The policy needs:
- `bedrock:InvokeModel` and `bedrock:InvokeModelWithResponseStream` on
  `arn:aws:bedrock:eu-central-1:057691001760:inference-profile/eu.anthropic.claude-*` **and**
  `arn:aws:bedrock:*::foundation-model/anthropic.claude-*` (EU profiles route to several EU regions).
- `bedrock:ApplyGuardrail` on the guardrail ARN.
- CloudWatch Logs write.
- `s3:GetObject` on the code bucket and prefix (if using code deploy), or ECR pull (if using a container).

The AgentCore CLI generates this role for you.

### 4.4 Invoke from the SPA

OAuth-authorized runtimes **can't** be called through the AWS SDK. Use plain HTTPS:

```
POST https://bedrock-agentcore.eu-central-1.amazonaws.com/runtimes/<URL-encoded runtime ARN>/invocations?qualifier=DEFAULT
Authorization: Bearer <BiatecOIDC access token>
Content-Type: application/json
X-Amzn-Bedrock-AgentCore-Runtime-Session-Id: <stable per chat, >= 33 chars, e.g. uuid4 + uuid4 hex>

{"prompt": "What is my Algorand balance on testnet?"}
```

The response streams (SSE) the chunks yielded by the entrypoint.

**VERIFY first, before building the UI:** check whether the Runtime endpoint answers CORS preflight for a browser
origin. If it doesn't, put a thin **Lambda Function URL** proxy in front (it forwards `Authorization` and the
session header and streams the body). It bills nothing while idle. Don't use API Gateway REST plus a NAT.

---

## 5. Gateway

### 5.1 Interceptor Lambda (copies the user's token to the target)

```python
# infra/interceptor/handler.py — REQUEST interceptor, passRequestHeaders=True
def handler(event, context):
    req = event["mcp"]["gatewayRequest"]
    headers = req.get("headers") or {}
    auth = next((v for k, v in headers.items() if k.lower() == "authorization"), None)
    out = {"body": req["body"]}
    if auth:
        out["headers"] = {"Authorization": auth}  # Authorization from an interceptor is forwarded to the target
    return {"interceptorOutputVersion": "1.0", "mcp": {"transformedGatewayRequest": out}}
```

- Python 3.13, arm64, 128 MB, tag `Project=biatec`. Never log `headers`.
- The Gateway has already validated the JWT before the interceptor runs.
- To block a request from the interceptor, return `transformedGatewayResponse` (statusCode + JSON-RPC error body)
  instead. The gateway responds with it immediately.
- Add a resource policy on the Lambda allowing `bedrock-agentcore.amazonaws.com` with `aws:SourceAccount` and
  `aws:SourceArn` = gateway ARN, and allow `lambda:InvokeFunction` on it in the gateway role.

### 5.2 Create the gateway and the target

```python
gw = ctl.create_gateway(
    name="biatec-chat-gw",
    roleArn="<gateway-role-arn>",     # trust bedrock-agentcore.amazonaws.com; lambda:InvokeFunction on the interceptor
    protocolType="MCP",
    protocolConfiguration={"mcp": {"supportedVersions": ["2025-06-18", "2025-11-25"]}},  # VERIFY vs BiatecMCP SDK
    authorizerType="CUSTOM_JWT",
    authorizerConfiguration={"customJWTAuthorizer": {
        "discoveryUrl": "https://oidc.biatec.io/.well-known/openid-configuration",
        "allowedClients": ["biatec-chat"]}},
    interceptorConfigurations=[{
        "interceptor": {"lambda": {"arn": "<interceptor-lambda-arn>"}},
        "interceptionPoints": ["REQUEST"],
        "inputConfiguration": {"passRequestHeaders": True}}],
    exceptionLevel="DEBUG",          # remove for the demo/prod
    tags={"Project": "biatec"},
)
# gw["gatewayUrl"] → MCP_URL for the agent. Poll get_gateway until READY.

ctl.create_gateway_target(
    gatewayIdentifier=gw["gatewayId"],
    name="biatec-mcp",
    targetConfiguration={"mcp": {"mcpServer": {
        "endpoint": "https://mcp.biatec.io/tools",
        "listingMode": "DYNAMIC"}}},
    # no credentialProviderConfigurations → "no authorization"; the interceptor supplies Authorization
)
# Poll get_gateway_target until READY. Error states: FAILED, UPDATE_UNSUCCESSFUL, SYNCHRONIZE_UNSUCCESSFUL (see statusReasons).
```

Extra tool targets: a Lambda target (`targetConfiguration.mcp.lambda` with an inline tool schema, using the gateway
IAM role) for things like a sanctions or licence check against the `MostlyRIght/` dataset. Or an OpenAPI target with
an API-key provider for the TSYS API.

**Smoke test:** with a fresh user token, send JSON-RPC `initialize`, then `tools/list` to `gatewayUrl`. You should
get 20 tools named `biatec-mcp___…`. A 401 from BiatecMCP means the interceptor isn't adding the header, or the
token's `aud` lacks the MCP resource.

---

## 6. Policy (Cedar on Gateway tool calls)

Guardrails filter text. **Policy is what stops a tool call.** It is deny-by-default once attached.

```python
pe = ctl.create_policy_engine(name="biatec_chat_policies")   # poll until ACTIVE (VERIFY status name)
ctl.create_policy(policyEngineId=pe["policyEngineId"], name="baseline_permit",
                  validationMode="FAIL_ON_ANY_FINDINGS", definition={"cedar": {"statement": BASELINE}})
ctl.update_gateway(gatewayIdentifier=gw["gatewayId"], ...,   # update_gateway needs the full config again
                   policyEngineConfiguration={"arn": pe["policyEngineArn"], "mode": "LOG_ONLY"})  # → "ENFORCE" once logs look right
```

Starter policies (replace `<gw-arn>`):

```cedar
// BASELINE: allow every tool on this gateway for authenticated users
permit(principal, action, resource == AgentCore::Gateway::"<gw-arn>");

// Demo safety: no broadcasting to mainnet from the chat
forbid(principal, action == AgentCore::Action::"biatec-mcp___submitTransactionToBlockchain",
       resource == AgentCore::Gateway::"<gw-arn>")
when { context.input has network && ["algorand", "mainnet-v1.0"].contains(context.input.network) };

// Cap single payments built by the agent (amount is in base units: 100 ALGO = 100000000 microAlgo)
forbid(principal, action == AgentCore::Action::"biatec-mcp___createPaymentTransaction",
       resource == AgentCore::Gateway::"<gw-arn>")
when { context.input has amount && context.input.amount > 100000000 };

// The chat never rekeys or creates assets
forbid(principal, action in [AgentCore::Action::"biatec-mcp___activateCryptoAddress",
                             AgentCore::Action::"biatec-mcp___createAssetCreateTransaction"],
       resource == AgentCore::Gateway::"<gw-arn>");
```

- Principals from `CUSTOM_JWT` are `AgentCore::OAuthUser`; read claims with `principal.hasTag("sign")` and
  `principal.getTag("sub")`.
- **VERIFY:** that Policy evaluates `context.input` for `DYNAMIC`-listed MCP-server targets. Policy validation may
  need the tool schema. If it can't, keep the tool-level permit/forbid rules and enforce argument rules in the
  interceptor Lambda instead (inspect `body.params.name` and `body.params.arguments`).
- Defence in depth for the pitch: Cedar policy at the Gateway → BiatecOIDC spending limits → algo-safe on-chain
  group limits.

---

## 7. Guardrails

### 7.1 Create (once) and publish a version

```bash
aws bedrock create-guardrail --region eu-central-1 --cli-input-json file://infra/guardrail.json
aws bedrock create-guardrail-version --region eu-central-1 --guardrail-identifier <id>   # → "1"; pin this
```

`infra/guardrail.json`:

```json
{
  "name": "biatec-chat-guardrail",
  "blockedInputMessaging": "I can't help with that request.",
  "blockedOutputsMessaging": "I can't share that response.",
  "contentPolicyConfig": {"filtersConfig": [
    {"type": "PROMPT_ATTACK", "inputStrength": "HIGH", "outputStrength": "NONE"},
    {"type": "MISCONDUCT", "inputStrength": "MEDIUM", "outputStrength": "MEDIUM"},
    {"type": "HATE", "inputStrength": "MEDIUM", "outputStrength": "MEDIUM"},
    {"type": "VIOLENCE", "inputStrength": "MEDIUM", "outputStrength": "MEDIUM"}
  ]},
  "topicPolicyConfig": {"topicsConfig": [{
    "name": "InvestmentAdvice",
    "definition": "Recommendations on which crypto assets to buy, sell or hold, price predictions, or personalised investment advice.",
    "examples": ["Should I buy ALGO now?", "Which token will go up next week?"],
    "type": "DENY"
  }]},
  "sensitiveInformationPolicyConfig": {"regexesConfig": [{
    "name": "Mnemonic25Words",
    "description": "Algorand 25-word recovery phrase",
    "pattern": "\\b([a-z]{3,8}\\s+){24}[a-z]{3,8}\\b",
    "action": "BLOCK"
  }]},
  "tags": [{"key": "Project", "value": "biatec"}]
}
```

- `PROMPT_ATTACK` only supports input, so `outputStrength` must be `NONE`.
- The mnemonic regex can false-positive on a 25-word lowercase sentence with no punctuation. That's acceptable;
  tune it if users hit it. Don't add a 64-hex private-key regex, because it would block legitimate EVM transaction
  hashes.
- Never use `DRAFT` in the Runtime env vars. After any change, run `create-guardrail-version` and bump
  `GUARDRAIL_VERSION`.

### 7.2 Where it applies

1. **Model input and output.** `guardrail_id` and `guardrail_version` on `BedrockModel` (section 4.2) put
   `guardrailConfig` on every Converse call. Use stream mode `sync`.
2. **Tool results (indirect prompt injection).** On-chain data (asset names, unit names, transaction notes) is
   attacker-controlled and comes back through tool results. Screen each tool result with the standalone
   `ApplyGuardrail` API before the model sees it:

```python
# agent/guard.py
import boto3, json
from strands.hooks import HookProvider, HookRegistry, AfterToolCallEvent

class ToolResultGuard(HookProvider):
    def __init__(self, gid, ver, region):
        self.rt = boto3.client("bedrock-runtime", region_name=region)
        self.gid, self.ver = gid, ver

    def register_hooks(self, registry: HookRegistry, **_):
        registry.add_callback(AfterToolCallEvent, self.check)

    def check(self, event: AfterToolCallEvent):
        text = json.dumps(event.result.get("content", []))[:20000]
        r = self.rt.apply_guardrail(guardrailIdentifier=self.gid, guardrailVersion=self.ver,
                                    source="INPUT", content=[{"text": {"text": text}}])
        if r["action"] == "GUARDRAIL_INTERVENED":
            event.result = {**event.result, "status": "error",
                            "content": [{"text": "Tool output withheld: it contained instructions or unsafe content."}]}
```

**VERIFY** that `AfterToolCallEvent.result` is writable in the installed Strands version. If it isn't, wrap the MCP
tools instead.

3. **Logging caveat.** Guardrail masking affects only the API response. Runtime and model-invocation logs still hold
   the raw text, so keep model invocation logging **off**, set CloudWatch retention (7 days) on
   `/aws/bedrock-agentcore/runtimes/*`, and never let secrets reach the prompt.

Guardrail cost is roughly USD 0.15 per 1,000 text units (1 unit = 1,000 characters) per enabled policy type.
That's negligible here.

---

## 8. Cost and cleanup

| Item | Billing | Idle cost |
|---|---|---|
| Runtime | per-second active vCPU and GB (≈ USD 0.09 per vCPU-hour, 0.0095 per GB-hour) | none |
| Gateway | ≈ USD 0.005 per 1,000 tool calls (+ search/indexing, unused with DYNAMIC) | none |
| Interceptor Lambda | per invoke | none |
| Policy, Identity | per request | none |
| Guardrails | per text unit | none |
| Bedrock Sonnet 4.5 | USD 3.30 / 16.50 per 1M in/out (cached in 0.33) | none |
| CloudWatch Logs | per GB | small; set retention |

A typical turn (~8K input mostly cached, ~500 output) costs about USD 0.01. Nothing in this design bills while idle.

Cleanup before the event ends (confirm with the user first):
`delete-gateway-target` → `delete-gateway` → `delete-policy` / `delete-policy-engine` → `delete-agent-runtime`
→ `delete-guardrail` → delete the Lambda, IAM roles, S3 code bucket and log groups.
Find leftovers by the `Project=biatec` tag.

---

## 9. Troubleshooting

| Symptom | Likely cause |
|---|---|
| 401/403 from Runtime or Gateway | `allowedClients` ≠ the token's `client_id`; expired token (15 min); wrong issuer (stage vs prod) |
| 401 from BiatecMCP through Gateway | interceptor missing, or `passRequestHeaders` false; token requested without `resource=https://mcp.biatec.io/tools` |
| 403 on `signTransaction` | token lacks the `sign` claim: scope not requested, or not in the client's `AllowedScopes` |
| `invalid_scope` at /authorize | requested `sign`, `manage-limits` or `rekey` that isn't allowlisted for `biatec-chat` |
| Target stuck or FAILED with DEFAULT listing | BiatecMCP requires auth for `tools/list`; use `listingMode=DYNAMIC` |
| Agent code sees no Authorization header | `requestHeaderAllowlist` doesn't include `Authorization` |
| Runtime won't start | non-ARM64 container or x86 wheels in the code zip |
| ValidationException on the model | missing `eu.` prefix, or a Claude 5 model ID |
| ThrottlingException | shared team limit of 50 req/min; adaptive retries are on, so reduce concurrency |
| Policy blocks everything | no baseline `permit`; Policy is deny-by-default |
| Transaction "fails" after a network retry | the agent changed `network` between build, sign and submit; the system prompt must forbid that |

## 10. Open VERIFY items (check in this order)

1. Runtime CORS from a browser origin (section 4.4). This decides whether the Lambda proxy is needed.
2. The MCP protocol versions BiatecMCP's `ModelContextProtocol.AspNetCore` serves vs the gateway's
   `supportedVersions` (section 5.2).
3. Whether Cedar `context.input` works with `DYNAMIC` MCP-server targets (section 6).
4. Whether Strands `AfterToolCallEvent.result` is mutable (section 7.2).
