# onboarding-requests — PMO Request Review (zero-cost pilot)

Checks whether an onboarding/access request is ready for **human** review. It never approves, routes or grants anything.

Runs on Azure free tiers only (Static Web Apps Free + managed Functions, Cosmos DB free tier). Paid components (Azure OpenAI reviewer, queue processing, App Insights, managed identity) are coded behind interfaces and switched off.

| Start here | |
|---|---|
| `CLAUDE.md` | rules, stack, commands |
| `SCOPE.md` | included / excluded |
| `docs/ARCHITECTURE.md` | components, API, statuses, design decisions |
| `docs/VERIFIED_FACTS.md` | platform facts with source and status |
| `docs/PILOT_REPORT.md` | acceptance results |
| `docs/RUNBOOK.md` | run locally, deploy, operate |
| `docs/COSTS.md` | zero-cost basis and evidence log |
| `docs/PROMOTION.md` | how to switch on each paid component |
| `workflows/` | SOPs per phase |

Quick start: `dotnet test` · `tools/run_acceptance.sh` · `tools/run_local.sh` (then http://localhost:4280).
