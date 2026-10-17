# Promotion Pack — from zero-cost pilot to paid environment

Each swap below is independent, switched by configuration, and **needs Gokul's written approval** (it creates paid resources). Nothing here was executed in the pilot. Prices are deliberately not given: use the **cost inputs** list with the Azure Pricing Calculator for your region and agreement.

Templates: `config/pilot.settings.json` (current), `config/paid.settings.template.json` (placeholders only).
Proof: `PromotionPathTests` (in `tests/PmoReview.Tests/Contract/ResultContractTests.cs`) builds the paid settings and runs the Azure OpenAI + queue paths end to end **with mocks** (no network, no cost). `AzureOpenAIModelTests.cs` covers the model adapter, the assistant and the strict-mode schema rules (F53).

Safety switch: every paid provider is refused at startup unless `ALLOW_PAID_SERVICES=true` (`ReviewModelFactory`, `DispatcherFactory`).

---

## 1. Reviewer: RuleOnly → Azure OpenAI

| Item | Detail |
|---|---|
| What changes | `AzureOpenAIReviewModel` adds an AI opinion to the Stage 2 review. Host logic is unchanged: the deterministic checklist decides PASS/FAIL; the model can add concerns (a failed AI check returns the request) but never remove a failed check, and it never approves, creates or assigns. Optional `AI_REPORTS=true` (worker only) adds AI analysis to the daily summary and developer reports. |
| Config keys | `REVIEW_MODEL_PROVIDER=AzureOpenAI`, `ALLOW_PAID_SERVICES=true`, `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT`, `AZURE_OPENAI_API_VERSION` |
| Code switch | `src/adapters/.../Review/ReviewModelFactory.cs` (already coded). One call per review, no tools; limits in `ReviewLimits` (time budget, input chars, output tokens, max 3 retries on 429 with `retry-after-ms`). Secrets/TAP masked before the prompt; token usage logged. |
| Prerequisites | Azure OpenAI resource with a custom subdomain; a deployment of a model that supports **structured outputs (json_schema, strict)**; API version 2024-08-01-preview or later, or the GA `v1` endpoint (F53). Managed identity with role **Cognitive Services OpenAI User** on the resource. No keys. **Go-live check (F53):** Learn now shows the `/openai/v1/` endpoint with token audience `https://ai.azure.com/.default`; the code uses the deployments endpoint + `api-version` with `https://cognitiveservices.azure.com/.default` — confirm on the first SAMPLE call or switch to v1. |
| Hosting constraint | SWA managed Functions **have no managed identity** (F2) and a **45 s** request limit (F3). The worker (Flex, managed identity) can use Azure OpenAI today for `AI_REPORTS`; for the review itself do swap 2 (BYO Function App) and ideally swap 3 (queue) first, otherwise reviews started by the API fail and go to Failed-Needs-Dev after 3 attempts. Keep `PROCESSING_BUDGET_SECONDS` < 45 while processing is inline. |
| Approval needed | Gokul (cost + data processing). Data owner sign-off: request text is sent to the model. |
| Validation | `dotnet test` (contract, schema and injection tests run against a mock); then a SAMPLE-only live smoke test. |
| Rollback | Set `REVIEW_MODEL_PROVIDER=RuleOnly`. No data migration; runs record `reviewerProvider` and `promptVersion`. |
| Cost inputs | Model and deployment type; requests per month; average input and output tokens per review (≤ 4 turns); region; whether provisioned or pay-as-you-go. |

## 2. API host: SWA managed Functions → bring-your-own Function App + managed identity

| Item | Detail |
|---|---|
| What changes | Same `src/api` code deployed to a Function App linked to SWA (**Standard** required for BYO, F8). Cosmos access by Entra ID instead of a connection string. |
| Config keys | Remove `COSMOS_CONNECTION_STRING`; set `COSMOS_ENDPOINT`. App Insights (swap 4) and Azure OpenAI (swap 1) settings as needed. |
| Code switch | `Program.cs` uses `CosmosDataStores.CreateClientWithIdentity` when `COSMOS_ENDPOINT` is set and no connection string exists (already coded). |
| Prerequisites | Function App on the **currently recommended plan** (Flex Consumption at the time of writing — **verify**), .NET 10 isolated; system-assigned identity; Cosmos data-plane role **Cosmos DB Built-in Data Contributor** scoped to the account; optionally `disableLocalAuth: true` on Cosmos once keys are no longer used; Key Vault for any remaining secrets. |
| Approval needed | Gokul (new paid resources: Function App plan, storage account the plan requires, SWA Standard). |
| Validation | Deploy to a non-production slot/environment; run the Playwright suite against it with real Entra test users; confirm the connection string is gone from all settings. |
| Rollback | Re-link the SWA to managed Functions and restore `COSMOS_CONNECTION_STRING` (keys rotated first). |
| Cost inputs | Plan type; executions and execution time (GB-s) per month; always-ready instances (if any); storage account for the Function App; SWA Standard per-app monthly price. |

## 3. Processing: InlineDispatcher → QueueDispatcher (or Power Automate)

| Item | Detail |
|---|---|
| What changes | Submitting a request queues its review instead of running it inside the HTTP call; a queue-triggered Function runs the same `ReviewProcessor` via `QueueMessageHandler`. The SPA already refreshes while the agent has the next step. Removes the 45 s ceiling on review time. |
| Config keys | `REVIEW_DISPATCHER=Queue`, `ALLOW_PAID_SERVICES=true`, `REVIEW_QUEUE_NAME`. |
| Code switch | `DispatcherFactory` (coded). To add: an `IQueueClient` implementation (Storage Queues or Service Bus SDK) and a queue-triggered function that calls `QueueMessageHandler.HandleAsync` — ~30 lines; both blocked in the pilot because they need paid resources. |
| Prerequisites | Swap 2 (managed Functions are HTTP-only, F1). Storage account queue or Service Bus namespace; managed identity with *Storage Queue Data Contributor* / *Azure Service Bus Data Sender/Receiver*; poison-queue handling; operator recovery stays as the safety net. |
| Alternative | Power Automate flow: trigger on new `reviewRuns` with status Queued → call the processing endpoint. Requires premium connector licensing. |
| Approval needed | Gokul. |
| Rollback | `REVIEW_DISPATCHER=Inline`. Queued runs left behind are recovered by the Operator endpoint (marked Failed(Timeout)) and retried. |
| Cost inputs | Messages per month (≈ 1 per review + retries); Service Bus tier or Storage transactions; Power Automate licence model if used. |

## 4. Logs → Application Insights

| Item | Detail |
|---|---|
| What changes | Structured logs and request telemetry sent to Application Insights with sampling. |
| Config keys | `APPLICATIONINSIGHTS_CONNECTION_STRING` (Key Vault reference), `ALLOW_PAID_SERVICES=true`. Sampling in `host.json` (`logging.applicationInsights.samplingSettings`). |
| Code switch | `Program.cs` enables `AddApplicationInsightsTelemetryWorkerService` + `ConfigureFunctionsApplicationInsights` only when both settings are present (already coded). |
| Data rule | Logs carry run ids, correlation ids, status codes and error categories only. **Never** request text, names or connection strings. Keep it that way in code review. |
| Prerequisites | Log Analytics workspace + workspace-based Application Insights; daily cap; retention set to the minimum agreed. |
| Approval needed | Gokul. |
| Rollback | Remove the connection string; logging falls back to console. |
| Cost inputs | GB ingested per day after sampling; retention days; daily cap. |

## 5. SWA Free → Standard

| Item | Detail |
|---|---|
| What changes | SLA, custom authentication (tenant-restricted Entra app registration, F10/F11), bring-your-own Functions, larger quotas. |
| Config keys | `staticwebapp.config.json` → `auth.identityProviders.azureActiveDirectory.registration` with `openIdIssuer: https://login.microsoftonline.com/<tenant-id>/v2.0`, `clientIdSettingName`, `clientSecretSettingName` (secret in Key Vault). |
| Prerequisites | Entra app registration (single tenant, assignment required); test that a personal/other-tenant account is rejected (see F10 Q&A edge case). |
| Approval needed | Gokul + Entra admin for the app registration. |
| Rollback | Change SKU back to Free (custom auth and BYO Functions stop working — do swap 2 rollback first). |
| Cost inputs | Standard plan per app per month; bandwidth above the included amount. |

## 6. Optional: Cosmos → Dataverse + Power Apps

| Container | Dataverse table | Notes |
|---|---|---|
| projects | Project | contacts, domain suffix, group naming pattern |
| mappingSheet | Mapping Sheet Row | PMO edits; versioned; fingerprint per resource |
| responseDocument | Response Record | updated by the agent and the role team report |
| requests | Access Request | workflow state (`flow`) written only by the agent identity |
| reviewRuns | Review Run | snapshot + SHA-256; status changes only by the processing identity |
| notifications | Notification | outbox |
| issues | Issue | issues list |
| userAccess | (security roles + business units/teams) | replaces deny-by-default table with Dataverse row-level security |

Code switch: implement `IDocumentStore<T>` over the Dataverse Web API (or keep Cosmos and only move the UI). Prerequisites: Dataverse capacity, Power Apps licences, premium connectors. Cost inputs: licences per user, Dataverse database/file capacity, Power Automate runs.

## 7. CI/CD: Azure Pipelines

`pipelines/azure-pipelines.yml` (provided, **not executed**): Build (tests, acceptance report, SPA build) → WhatIf → Deploy (environment with a manual approval check).

- Service connection: **workload identity federation** (no client secret). Grant it Contributor on the target RG only, plus Resource Policy Contributor if the pipeline manages policy.
- SWA deployment token: secret variable from a Key Vault-linked variable group.
- Cost inputs: parallel jobs needed beyond the free grant; agent minutes.

---

### Promotion order (recommended)
1. Swap 5 (SWA Standard) + swap 2 (BYO Function App, managed identity) — removes the connection string.
2. Swap 4 (App Insights) — observe before adding the model.
3. Swap 3 (queue) — removes the 45 s ceiling.
4. Swap 1 (Azure OpenAI review) — `AI_REPORTS` on the worker can come earlier (it already has a managed identity).
5. Swap 7 (pipelines) any time after 1.
