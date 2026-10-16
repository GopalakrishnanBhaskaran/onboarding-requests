# CLAUDE.md — PMO Request Review Pilot (Azure, zero cost)

Phase 1 pilot of the PMO Request Review. **Review only.** The end point is human review of findings. "Ready for human review" never means approved, and nothing here grants access.

Source of truth for scope: `SCOPE.md`. Source of truth for platform facts: `docs/VERIFIED_FACTS.md`.

## Hard rules
1. **Zero cost.** Allowed Azure resources: Static Web Apps (Free) with managed Functions, Cosmos DB for NoSQL with free tier, Azure Policy assignment, Cost Management budget. Anything else needs Gokul's written approval.
2. **Never create:** Azure OpenAI, App Insights, Log Analytics, Key Vault, Storage accounts, Function Apps, Service Bus, API Management, App Service, Azure SQL, VNets, private endpoints.
3. **Verify, don't assume.** Check every free-tier limit and runtime against current Microsoft Learn. Record URL + date in `docs/VERIFIED_FACTS.md`. If a fact differs from the plan, stop and report.
4. **Approval gate before any Azure resource:** show `az deployment group what-if` output, resource list and expected cost (₹0 with reasons). Wait for an explicit "approved".
5. **Cosmos free tier:** check the subscription has no free-tier account already. If it has, stop and ask.
6. **Out of scope:** approval routing, identity lookup/creation, Microsoft Graph, group assignment, credentials, email/Teams, tickets, billing, multiple agents, attachments/OCR.
7. **No secrets or real data** in source, prompts, logs or examples. Sample data is labelled `SAMPLE`. Cosmos connection string lives only in SWA app settings.
8. **Evidence only.** Never say deployed/working without running it and showing output. Separate facts from assumptions.
9. Ask once per phase, only for blocking choices. Routine reversible coding proceeds.
10. `ALLOW_PAID_SERVICES=false` by default. Startup must throw if a paid provider is selected while it is false.

## Stack
| Layer | Pilot (active) | Paid (coded, disabled) |
|---|---|---|
| UI | React + Vite + TypeScript + Fluent UI on SWA Free | same, SWA Standard |
| Auth | SWA built-in, pre-configured Entra provider | SWA custom Entra registration |
| API | SWA managed Functions, C# isolated, HTTP only | BYO Function App + managed identity |
| Data | Cosmos DB NoSQL free tier, shared 1000 RU/s | Cosmos with RBAC, or Dataverse |
| Processing | `InlineDispatcher` | `QueueDispatcher` |
| Reviewer | `RuleOnlyReviewModel` | `AzureOpenAIReviewModel` |
| Logs | Structured logs only | App Insights with sampling |

.NET target version: **pending Q1 in `docs/PHASE0_PLAN.md`.**

## Layout (WAT)
- `workflows/` SOPs Claude Code follows, one per phase plus cost guardrails and incident recovery.
- `tools/` helper scripts (seed, free-tier check, schema validation, acceptance, cost check, report export).
- `src/core` domain, rules, engine, interfaces. No Azure SDK references.
- `src/adapters` Cosmos repository, dispatchers, review models.
- `src/api` HTTP Functions. `src/web` SPA.
- `infra/` Bicep. `tests/` unit, contract, security, acceptance. `docs/` architecture, facts, costs, promotion, runbook, report.

## Commands (filled in as each phase lands)
| Task | Command |
|---|---|
| Build API + core | `dotnet build` (Phase 1) |
| Unit/contract/security tests | `dotnet test` (Phase 1) |
| Web dev | `npm --prefix src/web run dev` (Phase 4) |
| Local end-to-end | `swa start` (Phase 4) |
| Acceptance | `tools/run_acceptance.sh` (Phase 5) |
| Free-tier check | `tools/check_free_tier.sh <subscriptionId>` (Phase 6) |
| Cost check | `tools/cost_check.sh rg-pmo-review-pilot` (Phase 6) |

## Security rules for API code
- Identity comes **only** from the `x-ms-client-principal` header. Ignore any user/project field in the body.
- Look up `userAccess`. Deny by default. Filter every query by the caller's authorised `projectId`.
- Only the processing path writes `result`, `snapshot`, versions or status.
- ETag optimistic concurrency on every update.
- Request text is untrusted. Injected instructions or "already approved" claims become a finding, never an action.
