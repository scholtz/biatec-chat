---
id: T-112
title: "Move the business knowledge base into Bedrock Knowledge Base DZZGQITEAS and answer from it"
status: review
priority: P1
epic: "Knowledge base"
blocked_by: []
area: ["docs/kb/**", "backend/Biatec.Chat.Api/Chat/SignIn/AnonymousTierExecutors.cs", "backend/Biatec.Chat.Api/Chat/SignIn/BedrockKb*.cs", "backend/Biatec.Chat.Tools/Kb/KbSearchService.cs", "backend/Biatec.Chat.Api/appsettings.Development.json", "backend/Biatec.Chat.Api/Biatec.Chat.Api.csproj", "backend/Biatec.Chat.Tests/Kb/**", "infra/kb/**", "infra/aws/bedrock-role-policy.json", "docs/aws-setup.md", "k8s/base/backend.yaml"]
parallel: yes
conflicts_with: []
issue: "#237"
branch: "task/T-112"
owner: "session-20261020-kb"
taken_by: "LukassSoukup"
source: "PRODUCT-DESCRIPTION.md#capabilities-and-intents"
created: 2026-10-20
updated: 2026-10-20
---

# T-112 · Move the business knowledge base into Bedrock Knowledge Base DZZGQITEAS and answer from it

## Context

The spec answers `explain.product` and `explain.blockchain` from "model knowledge plus a curated knowledge base of
Biatec docs", reached only through the anonymous `kb.search` tool ("Knowledge-base content never enters the system
prompt; the model retrieves it through the knowledge-base search tool, and the results arrive as untrusted data like
any other tool result", PRODUCT-DESCRIPTION.md § Model Layer).

Today `kb.search` is a keyword search over seven short pages in `docs/kb/` (about 780 words in total) plus pages
operators upload (T-096). Answers about tiers, phone approval, spending limits, safes, fees, privacy, integration
and so on are thin, and keyword matching misses paraphrased questions.

The team created an Amazon Bedrock Knowledge Base in the hackathon account:

| Item | Value |
|---|---|
| Knowledge base | `DZZGQITEAS` (`knowledge-base-quick-start-a7z7w`), eu-central-1, managed embeddings, ACTIVE |
| Data source | `XYLE4VEJV6`, S3 connector on bucket `biantec-chatbot-knowladgebase` (empty on 2026-10-20) |
| Query API | `bedrock-agent-runtime` `Retrieve` (chunks + source URI), `AgenticRetrieveStream` (agent-driven, generates its own answer) |

## Goal

The assistant answers business questions from a rich, user-facing knowledge base held in Bedrock Knowledge Base
`DZZGQITEAS`, with the local pages as a fallback, and the KB content can be re-synced with one command.

## Scope

- In: rewrite and extend `docs/kb/` from PRODUCT-DESCRIPTION.md and the docs into user-facing pages (products, tiers,
  sign-in, previews, phone approval, spending limits, safes, DEX, fees, privacy and evidence, integration for banks
  and sites, agent modes, x402, FAQ, glossary, what's live versus roadmap); a sync script that uploads them to the
  S3 data source and runs an ingestion job; `kb.search` calls Bedrock `Retrieve` on the configured KB and falls back
  to the local keyword search when it's not configured, fails or returns nothing; the role policy gets
  `bedrock:Retrieve` on that KB; config and docs.
- Out: `AgenticRetrieveStream` with `GenerateResponse` (it would answer with its own model outside our system prompt,
  tier gate and guardrail; `Retrieve` keeps the results as untrusted tool data). Operator uploads (T-096) keep working
  against the local index only.

## Parallel work

Parallel work possible: **yes**. No other unfinished task that could run at the same time changes the same
files.

## Acceptance criteria

- [x] `docs/kb/` holds user-facing pages covering the topics in Scope, consistent with PRODUCT-DESCRIPTION.md and not
      claiming roadmap features as live.
- [x] `infra/kb/sync.sh` uploads `docs/kb/*.md` to `s3://biantec-chatbot-knowladgebase/`, starts an ingestion job and
      waits for it; the job indexes every page.
- [x] With `Kb__Bedrock__KnowledgeBaseId` set, `kb.search` returns Bedrock `Retrieve` chunks labelled with their source
      page; unset, failing or empty, it returns the local keyword hits. Unit tests cover all four.
- [x] `infra/aws/bedrock-role-policy.json` allows `bedrock:Retrieve` on the KB; `docs/aws-setup.md` documents the KB,
      the sync and the config.
- [x] `dotnet test` green.

## Verification

- `infra/kb/sync.sh` then `aws bedrock-agent-runtime retrieve --knowledge-base-id DZZGQITEAS ...` returns chunks.
- `dotnet test backend/Biatec.Chat.Tests --filter Kb`.

## Hand-off

- **Summary:** `docs/kb` rewritten and extended to 27 user-facing pages (~11k words, from 7 pages / ~780) drawn from
  PRODUCT-DESCRIPTION.md and docs/. Synced to Bedrock KB `DZZGQITEAS` (27 indexed, 0 failed; ingestion job
  GXBLZGZQNK). `kb.search` uses Bedrock `Retrieve` (managed search; the managed KB rejects `vectorSearchConfiguration`)
  plus the best uploaded-page hit, and falls back to the local keyword search. Local search ranks a page first when the
  query covers its whole title. PR #240.
- **Files changed:** docs/kb/*.md, Chat/SignIn/BedrockKbRetriever.cs (new), Chat/SignIn/AnonymousTierExecutors.cs,
  Biatec.Chat.Tools/Kb/KbSearchService.cs, Tests/Kb/BedrockKbTests.cs (new), infra/kb/sync.sh (new),
  infra/aws/bedrock-role-policy.json, k8s/base/backend.yaml, appsettings.Development.json, Api csproj
  (AWSSDK.BedrockAgentRuntime), docs/aws-setup.md §6.
- **How it was verified:** full `dotnet test` 1333 passed / 1 skipped; Kb tests rerun after the managed-search fix
  (53 passed, including the live `Live_retrieve_returns_cited_docs_kb_chunks` against DZZGQITEAS); local backend +
  Haiku: a guest question about a lost phone → kb.search → answer from the authenticator page.
- **Follow-up tasks created:** none.
- **Unblocks:** none.
- **Open questions:** the role policy is changed in the repo but not applied to `biatec-chat-bedrock-role` (the
  agent was not permitted to change IAM). Until an operator reruns `infra/aws/rolesanywhere.sh`, production kb.search
  falls back to the local pages (same content).

## Review

## Activity log

- 2026-10-20 12:00 · LukassSoukup · (new) → in-progress · created on user request; claimed by session-20261020-kb
- 2026-10-20 13:00 · session-20261020-kb · in-progress → review · PR #240; role policy not applied yet (see Open questions)
