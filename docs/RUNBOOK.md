# Runbook — PMO onboarding flow (v2)

## Local development

Prerequisites: .NET SDK 10, Node 22 LTS, Python 3. Optional: Azure Functions Core Tools v4 (to run the real Functions host), Docker (Cosmos emulator).

**Cosmos contract test.** The in-memory store cannot show Cosmos-only behaviour (17 Oct 2026: v1 review runs broke the live submit). Before a release that touches stored documents: `docker run -d -p 8081:8081 mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-preview`, set `PMO_COSMOS_EMULATOR` to the emulator connection string (well-known emulator key from Microsoft Learn; never a real account), run `dotnet test --project tests/PmoReview.Tests -f net10.0 -- --filter-class '*CosmosEmulatorTests'`. It runs the whole flow to Completed next to v1 documents; without the variable it is skipped.

| Task | Command |
|---|---|
| Build everything | `dotnet build PmoReview.slnx` |
| All .NET tests (unit, contract, security, acceptance) | `dotnet test --project tests/PmoReview.Tests -f net10.0` |
| Acceptance suite + `docs/PILOT_REPORT.md` | `tools/run_acceptance.sh` (`E2E=1` to include browser tests) |
| SPA unit checks | `npm --prefix src/web test` |
| Local end-to-end (SWA CLI emulator + in-memory SAMPLE API) | `tools/run_local.sh` → open http://localhost:4280 |
| Browser E2E (with `run_local.sh` running) | `npm --prefix src/web run e2e` |
| Validate sample data | `python3 tools/seed_sample_data.py --dry-run` |
| Validate a result file | `python3 tools/validate_result_schema.py <file.json>` |

**Mock sign-in locally.** The SWA CLI emulator shows a login form: choose provider `aad`, enter a SAMPLE user id from `tools/sample_data/sample-data.json` as the user id and `<id>@sample.invalid` as the user details (the named Owner is matched on that sign-in name). Alpha: `sample-user-alice` (Requester), `sample-user-olivia` / `sample-user-otto` (Owner), `sample-user-sam` (Security), `sample-user-dual` (Owner + Security), `sample-user-ivan` (ID team), `sample-user-rose` (role team), `sample-user-paul` (PMO), `sample-user-oscar` (Operator). Beta: `sample-user-bob` (Requester), `sample-user-bella` (Owner + Security + PMO). Locally there is no worker or Entra: the agent runs inline and Entra checks show "not checked (local development)", so a request can go all the way to Completed.

**Why `tools/local-api` exists.** Azure Functions Core Tools could not be downloaded in the build environment. `tools/local-api` is a loopback-only, dev-only host that serves the *same* `ApiHost` (routing, auth, processing) as the Functions entry point. It refuses to start without `PILOT_LOCAL_DEV=true`. With Core Tools installed you can instead run `func start` in `src/api/PmoReview.Api` (copy `local.settings.example.json` to `local.settings.json`).

**API runtime.** The managed API runs on `dotnet-isolated:9.0` because SWA CLI 2.0.10 rejects 10.0 (F21). To move to .NET 10 later: set `<TargetFramework>net10.0</TargetFramework>` in `src/api/PmoReview.Api/PmoReview.Api.csproj` and `platform.apiRuntime` to `dotnet-isolated:10.0`, run `dotnet test`, then `tools/deploy_free.sh app <subscriptionId>`.

## Deploy (only after approval)

Full v2 steps, approvals and the live end-to-end test: `workflows/09-onboarding-flow-v2.md`. In short: `params` → `whatif` → **approved** → `apply --approved-by` → `grant-graph` (read roles; **approval**) → `revoke-graph-writes` dry run → **approved** → `--approved-by` → seed with `--domain-suffix` → register testers with v2 roles → `tools/workflow_diag.sh`.

| Situation | Action |
|---|---|
| App code change | `tools/deploy_free.sh app <sub>` (no infrastructure change) |
| Worker code change | `tools/deploy_free.sh worker <sub>` |
| Where is a request stuck? | `bash tools/workflow_diag.sh <sub>` (worker, settings, Graph roles, latest requests, open issues) |
| Stop the agent (cost or incident) | `az functionapp stop -g rg-pmo-review-pilot -n <worker>`; requests keep their state; Entra checks, SLA reminders and the outbox wait until it is started again |

## Operations

| Situation | Action |
|---|---|
| Review stuck in Queued/Running | Issues and reports → "Recover stuck reviews" (Operator; marks > `STUCK_RUN_MINUTES` as Failed/Timeout); the agent retries, at most 3 times, then Failed-Needs-Dev. |
| Request in Failed-Needs-Dev | Issues and reports → developer issue report; fix the cause (and redeploy if it was code); Operator opens the request → **Resume after fix** (back to the stage that failed, attempts reset; a failed review starts a new review). |
| Approval waiting too long | Agent reminds at SLA, escalates to the manager at 2× SLA (`OWNER_SLA_HOURS`, `SECURITY_SLA_HOURS`). Never auto-approves. |
| Team task not picked up | Same SLA handling (`ID_TEAM_SLA_HOURS`, `ROLE_TEAM_SLA_HOURS`); issue `TaskSlaBreached`. |
| Unauthorized approval issue | Someone other than the named Owner / a Security member tried to decide. Ignored; Security reviews. |
| User reports an error | Ask for the "Reference" (correlation id) shown in the error bar; search logs for it. |
| New tester | Sign in once, `/.auth/me` → `userId`; `python3 tools/seed_sample_data.py --register-user <userId> --project SAMPLE-ALPHA --roles <v2 roles>`. Unregistered users see their own user id and no data. |
| Change the mapping sheet | PMO edits it in the app (Mapping sheet page). In-flight requests for that resource are re-reviewed automatically. |
| Rotate Cosmos key | `az cosmosdb keys regenerate --key-kind primary`; re-run the app-setting step from `deploy_free.sh`. |
| Cost alert | `workflows/incident-recovery.md`. |
| Tear down | `az group delete -n rg-pmo-review-pilot` (deletes data; frees the Cosmos free-tier slot). Needs Gokul's written decision. |
