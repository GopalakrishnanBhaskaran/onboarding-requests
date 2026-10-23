# onboarding-requests — PMO onboarding flow (v2)

Onboarding / access requests from submission to verified access:

1. Requester submits the request
2. AI review (mapping sheet + response document)
3. Owner approval
4. Security approval
5. ID team creates user/group (+ TAP)
6. Role assignment team assigns roles
7. AI verifies the access, notifies the requester (Completed), audit — no wait for the user's sign-in

The **AI agent reviews, validates, summarises, routes, tracks, verifies and reports. It never approves.** The named Owner and Security approve; the ID team and the role assignment team make the changes in their own tools and record them here. Optional v2.1 (approval-gated): after both approvals the agent creates the user, group and membership and assigns allow-listed Azure roles. The Temporary Access Pass never enters the app (metadata only) and always comes from the ID team.

Runs on Static Web Apps Free + managed Functions, Cosmos DB free tier and one Flex Consumption worker (managed identity; Microsoft Graph read, plus the v2.1 create permissions only when approved). Azure OpenAI, mail and Azure DevOps adapters are coded and off.

| Start here | |
|---|---|
| `CLAUDE.md` | hard rules, stack, commands |
| `SCOPE.md` | flow, who does what, TAP rules, included / excluded |
| `docs/NEXT_SESSION.md` | current state and the approval-gated deploy steps |
| `workflows/09-onboarding-flow-v2.md` | SOP: deploy, live end-to-end test, failure handling |
| `docs/ARCHITECTURE.md` | components, lifecycle, stage → code, API |
| `docs/VERIFIED_FACTS.md` | platform facts with Microsoft Learn source and status |
| `docs/PILOT_REPORT.md` | acceptance results (local, SAMPLE data) |
| `docs/RUNBOOK.md` | run locally, operate |
| `docs/PROMOTION.md` | how to switch on each paid component |
| `docs/DEPLOYMENT_LOG.md` | what was deployed, approved and tested, with evidence |

Quick start: `dotnet test --project tests/PmoReview.Tests -f net10.0` · `npm --prefix src/web test` · `tools/run_local.sh` (then http://localhost:4280 and `npm --prefix src/web run e2e`) · `tools/run_acceptance.sh`.
