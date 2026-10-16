# Phase 0 — Findings, Questions, Schema, Plan

Pilot v1.0 · 16 Oct 2026 · Owner: Gokul · Status: **approved 16 Oct 2026** — Q1 A (.NET 10 isolated), Q2 yes, Q3 30 s, Q4 INR, Q5 yes. Q6–Q10 open (defaults used: region parameters, in-memory tests, 5 min stuck-run limit).

> The API table in §3 was refined during build: routes are project-scoped (`/api/projects/{projectId}/...`). Current routes: `docs/ARCHITECTURE.md`.

## 1. Findings

Facts are in `docs/VERIFIED_FACTS.md`. `learn.microsoft.com` is blocked from the build environment, so facts come from search extracts and are marked for re-check.

| # | Finding | Impact | Proposed handling |
|---|---|---|---|
| 1 | **.NET runtime on SWA managed Functions is unconfirmed.** Docs extract shows `dotnet:8.0` in-process; isolated 8.0 seen only in a user bug report. The in-process model **and** .NET 8 both reach end of support on **10 Nov 2026** (25 days away). | A C# isolated API on .NET 8 would be out of support within weeks of go-live. | Q1. |
| 2 | **SWA API requests are cut at 45 s.** The prompt sets a 60 s budget for the Azure OpenAI path. | Inline processing longer than 45 s returns 500 to the browser. | Pilot processing budget 30 s (RuleOnly needs < 1 s). Paid model path on SWA capped at 30 s, or 60 s only after moving to BYO Function App + queue. Q3. |
| 3 | **Pre-configured Entra ID provider lets any Microsoft account sign in** (any tenant, incl. personal). Tenant restriction needs a custom provider. | Sign-in is not tenant-limited at the edge. | `userAccess` is deny-by-default: signed-in but unregistered users get 403 on every API and see nothing. Q2. |
| 4 | SWA `userId` in the client principal is a SWA-issued id, not the Entra object id (to re-verify, F12). | Test users can't be pre-registered by email/OID. | Test user signs in once, reads id from `/.auth/me`, then `seed_sample_data.py --register-user` writes `userAccess`. |
| 5 | **Built-in "Allowed resource types" policy is mode Indexed.** Cosmos child types (`sqlDatabases`, `containers`) and SWA child types may be evaluated and denied if not listed. | Deployment could fail, or (mode `All`) child types must be listed. | List parent + required child types in the assignment; prove the deny with `az deployment group validate` on a Storage account template (preflight only, nothing created). Q5. |
| 6 | **Budget amount is in the billing account currency.** ₹1 works only for INR billing. | Wrong currency → budget creation fails or alert is meaningless. | Q4. |
| 7 | Cosmos free tier: 1000 RU/s + 25 GB; not for serverless; shared DB up to 25 containers (pilot uses 7). One-per-subscription and set-at-creation not confirmed by extract. | Must check before deploy. | `tools/check_free_tier.sh` in Phase 6; stop if one exists. |
| 8 | Managed Functions: no managed identity, no Key Vault references (F2). | Cosmos connection string must be an SWA app setting. | As planned; never committed, never logged. Rotation in RUNBOOK. |
| 9 | SWA custom roles via function are Standard-only (F9). | None — app roles live in Cosmos `userAccess`. | No change. |
| 10 | Locked prompt pack not attached. | `SCOPE.md` included/deferred table is a draft. | Q7. |
| 11 | Build container has no .NET SDK, Functions Core Tools or SWA CLI; Azure CLI present but not signed in. | Phase 1 build/test needs the .NET SDK. | Install in Phase 1; if blocked by network policy, ask to allow the .NET/NuGet hosts. |

## 2. Questions (one list, blocking items first)

| # | Question | Options | Recommendation |
|---|---|---|---|
| **Q1** | Which API runtime? | **A.** `dotnet-isolated` .NET 10 — only if SWA managed Functions lists it today. **B.** `dotnet-isolated` .NET 8 for the pilot only, accepting it is unsupported after 10 Nov 2026; production on BYO Function App with .NET 10. **C.** Keep C# for `src/core`, write the managed API in TypeScript. | **A** if verified; else **B**. `src/core` and `src/adapters` multi-target `net8.0;net10.0` so promotion is a retarget, not a rewrite. |
| **Q2** | Accept that any Microsoft account can sign in, with access controlled by `userAccess` (deny by default)? | Accept / require tenant restriction (custom provider — plan support unverified, F11) | Accept for pilot. |
| **Q3** | Model time budget on SWA path? | 30 s (fits 45 s cut-off) / 60 s only after BYO + queue | 30 s, config-driven. |
| **Q4** | Billing currency of the subscription? | INR → ₹1 budget / other → smallest unit in that currency | Tell me the currency. |
| **Q5** | Policy approach? | Built-in "Allowed resource types" with parent + child types / custom definition with mode `All` | Built-in with child types; prove with preflight validate. |
| Q6 | Region? | Must support SWA managed Functions and Cosmos free tier | Tell me preferred region; I verify availability. |
| Q7 | Please add the locked prompt pack to the repo (`docs/locked/`) so `SCOPE.md` can be reconciled. | — | Needed before Phase 1 exit. |
| Q8 | Allow `learn.microsoft.com` in this environment's network settings (Allowed domains), or verify F4, F11, F12, F16, F19 yourself. | — | Allow the domain. |
| Q9 | Local Cosmos: Docker available for the emulator? | Emulator / in-memory repo for tests + free-tier account after Phase 6 approval | In-memory for automated tests; emulator optional. |
| Q10 | Stuck-run timeout N? | — | 5 minutes. |

## 3. Schema

One database `pmoreview`, **shared throughput 1000 RU/s** (manual, no autoscale). All documents carry `type`, `schemaVersion`, `_etag` (Cosmos).

| Container | Partition key | Key fields | Unique key | Writers |
|---|---|---|---|---|
| `projects` | `/projectId` | projectId, name, supportedEnvironments[], supportedRequestTypes[], isSample | — | Maintainer (seed) |
| `requirementRules` | `/projectId` | ruleId, version, requestType, field, rule, requirementText, effectiveFrom, effectiveTo, owner, publicationStatus (Draft/Published/Retired), sourceReference | `/ruleId,/version` | Maintainer |
| `roleMappings` | `/projectId` | mappingId, version, businessRole, environment, groupReference (reference text only), effectiveFrom, effectiveTo, owner, publicationStatus, sourceReference | `/mappingId,/version` | Maintainer |
| `requests` | `/projectId` | id, projectId, requestType, role, environment, manager, justification, startDate, endDate, createdBy, createdAt, updatedAt, **revision** (server) | — | PMO (own project) |
| `reviewRuns` | `/projectId` | id, requestId, snapshot, snapshotHash (SHA-256 hex), requestRevision, initiatedBy, processingStatus, readiness, ruleVersions[], mappingVersions[], reviewerProvider, reviewerVersion, promptVersion (null for RuleOnly), result, correlationId, queuedAt/startedAt/completedAt, errorCategory, retryOfRunId, **idempotencyKey** | `/idempotencyKey` | Processing path only; Operator (Running→Failed) |
| `reviewDecisions` | `/projectId` | id, reviewRunId, reviewer, outcome (Accepted/Corrected/Escalated), comment, timestamp | `/reviewRunId,/reviewer` | Reviewer |
| `userAccess` | `/userId` | id = `userId:projectId`, userId (SWA client principal), projectId, roles [PMO, Reviewer, Maintainer, Operator], isSample | — | Seed tool only (no API) |

Enums: `processingStatus` = Queued · Running · Completed · Failed · Superseded. `readiness` = NeedsInformation · ReadyForHumanReview · ManualReviewRequired. `errorCategory` = Timeout · MalformedResult · Conflict · DependencyUnavailable · Internal.

**Minimum personal data:** `requests` holds no national IDs, phone numbers or addresses. `manager` and `createdBy` are display names/ids only. All seeded values prefixed `SAMPLE`.

### API (all require auth; identity from `x-ms-client-principal` only)
| Method + route | Role | Notes |
|---|---|---|
| GET `/api/me` | any registered | projects + roles |
| GET/POST `/api/requests`, GET/PUT `/api/requests/{id}` | PMO (own project) | PUT needs `If-Match`; server bumps revision |
| POST `/api/requests/{id}/analyze` | PMO | body: `{ nonce }` → 202 `{ runId }` |
| GET `/api/requests/{id}/runs`, GET `/api/runs/{runId}` | PMO, Reviewer | project-filtered |
| POST `/api/runs/{runId}/retry` | PMO | new run, `retryOfRunId` |
| POST `/api/runs/{runId}/decisions` | Reviewer | review-quality feedback only |
| GET `/api/queue/attention` | Reviewer | Failed + ManualReviewRequired |
| GET/POST `/api/rules`, `/api/mappings`, POST `/{id}/publish` | Maintainer | SAMPLE only in pilot |
| POST `/api/operator/runs/recover` | Operator | Running > N min → Failed(Timeout) |

### Result contract `review-result.v1.json`
`schemaVersion`, `reviewRunId`, `requestRevision`, `readiness`, `summary`, `findings[{code, field, severity (Info/Warning/Blocking), message, ruleId, ruleVersion, sourceReference}]`, `mappingAssessment{status (Matched/NotFound/Ambiguous/NotApplicable), mappingId, mappingVersion, explanation}`, `clarificationQuestions[]`, `humanReviewRequired` (const `true`), `limitations[]`. `additionalProperties: false` throughout.

## 4. Plan

| Phase | Deliverables | Exit gate | Needs from Gokul |
|---|---|---|---|
| 0 Setup | This file, CLAUDE.md, SCOPE.md, 10 SOPs, ARCHITECTURE.md, VERIFIED_FACTS.md | Plan approved | Answers Q1–Q10 |
| 1 Data + auth | core model, Cosmos repo, authz guard, snapshot, SWA config, seed tool, security tests | Cross-project access blocked incl. forged body fields | — |
| 2 Review engine | checks, 2 tools, RuleOnly, AzureOpenAI (mock-tested, disabled), schema, host validation | Contract + citation + injection tests pass (both providers) | — |
| 3 Processing | Inline + Queue stub, status machine, idempotency, retry, recover | Duplicate/edit/concurrency/timeout/retry tests pass | — |
| 4 UI | SPA pages, polling, banner, a11y | Local E2E with two mock users | — |
| 5 Validate | acceptance suite, PILOT_REPORT.md | All critical gates pass | Review report |
| 6 Deploy (free) | Bicep, policy, budget, SWA Free, Cosmos free tier | **"approved" on what-if**; live synthetic run; ₹0 after 24–48 h | Subscription, sign-in, approval |
| 7 Promotion | PROMOTION.md, config templates, Pipelines YAML | Each paid path compiles + passes contract tests with mocks | — |

Phases 1–5 and 7 run fully locally and create no Azure resources.

## 5. Manual prerequisites (Gokul)
1. **Azure subscription ID** and role: Owner, or Contributor + Resource Policy Contributor on the subscription/RG.
2. **Region** (Q6).
3. **Entra tenant** for test users; 2–3 test accounts in different SAMPLE projects (no real employee data).
4. **Who assigns Azure Policy** if not you.
5. **Budget alert email** and billing currency (Q4).
6. Confirm no existing Cosmos DB free-tier account in the subscription.
7. Local tools for your own runs: .NET SDK (per Q1), Node LTS, Azure CLI + Bicep, Azure Functions Core Tools v4, SWA CLI, Docker (optional, Cosmos emulator).
8. Locked prompt pack in `docs/locked/` (Q7); `learn.microsoft.com` allowed (Q8).

**Next step:** reply "approved" (with answers to Q1–Q5 at minimum) to start Phase 1.
