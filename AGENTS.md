# biatec-chat: agent instructions

Biatec Chat: an embeddable Vue 3 chat widget, an ASP.NET Core 10 BFF backend that calls Amazon Bedrock, and a
Flutter authenticator. **[PRODUCT-DESCRIPTION.md](PRODUCT-DESCRIPTION.md) is the specification and wins over every
other document.**

## Work only inside this repository

- Create, edit and commit files only inside `biatec-chat/`. Never modify sibling repositories (BiatecOIDC/BiatecMCP,
  BiatecCLAMM, algo-safe, BiatecWeb). Their published packages and public HTTP APIs are dependencies; consume them,
  don't change them.
- When the product needs a change in another product (for example everything under "Required Changes To Biatec
  OIDC"), implement the documented **fallback** here, mock the missing API inside this repo for tests, and record
  the needed change in [docs/required-changes-biatec-oidc.md](docs/required-changes-biatec-oidc.md), which holds the
  API shapes and the fallback matrix for every row.

## Work is tracked on the kanban board

- Read `kanban/README.md` before starting. Work only on a task you've claimed or been assigned, follow its Scope,
  and never mark a task `done`: only the user approves, via `/kanban-review`.
- Commands: `/kanban-split`, `/kanban-dispatch` (parallel agents), `/kanban-next` (one task in this session),
  `/kanban-move`, `/kanban-board`, `/kanban-parallel`, `/kanban-take`, `/kanban-review` (user only).
- We work as a team: take tasks with `/kanban-take` (marks `taken_by: <GitHub username>`, commits and pushes the
  board), and never pick up a task another teammate has taken.

## Hackathon AWS rules (from the AWS + Deloitte participant guide)

- Region `eu-central-1` for every AWS call and resource. Cost Explorer only runs in `us-east-1`.
- Authentication is the SSO profile `hackathon` locally, and an IAM role in AWS. Never create IAM users or access keys,
  and never write credentials into code, config, committed `.env` files or logs.
- Bedrock Claude model IDs need the `eu.` prefix. Use **Claude 4.5 only**; Claude 5 models aren't available on this
  account. "Current Opus generation" in the spec means `eu.anthropic.claude-opus-4-5-20251101-v1:0` (25 req/min);
  Sonnet is `eu.anthropic.claude-sonnet-4-5-20250929-v1:0`; Haiku is `eu.anthropic.claude-haiku-4-5-20251001-v1:0`.
  Always set `maxTokens`, use prompt caching for the static system prompt, and back off with jitter on
  `ThrottlingException`. The 50 req/min limit is shared by the whole team.
- Don't provision anything that bills while idle without the user's explicit approval. Tag every resource
  `Project=biatec`. Allowed instance types are `t3.*`, `t4g.*`, `m6i/m7g.large|xlarge`, `c6i/c7g.large`; no GPUs.
- Code freeze: **Tuesday 20 October 2026, 13:30.**

## Security invariants (from the spec, never trade them away)

- The browser never receives an access, refresh or ID token. Only the backend holds tokens, encrypted, in the vault.
- Signing always goes from the backend straight to Biatec OIDC `POST /wallet/{network}/{address}/sign`. Never call
  Biatec MCP `signTransaction`.
- The backend never hand-assembles transaction groups. It uses Biatec MCP, `biatec-concentrated-liquidity-amm` and
  `algo-safe`.
- Content from outside the user is untrusted data, and tier and scope gates are enforced server-side regardless of
  the model.

`docs/AWS-AGENTCORE-GUIDE.md` is partly superseded; read its banner before using it.
