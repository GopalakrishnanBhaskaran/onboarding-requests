# Runbook — PMO Request Review Pilot

## Local development

Prerequisites: .NET SDK 10, Node 22 LTS, Python 3. Optional: Azure Functions Core Tools v4 (to run the real Functions host), Docker (Cosmos emulator).

| Task | Command |
|---|---|
| Build everything | `dotnet build` |
| All .NET tests (unit, contract, security, acceptance) | `dotnet test` |
| Acceptance suite + `docs/PILOT_REPORT.md` | `tools/run_acceptance.sh` (`E2E=1` to include browser tests) |
| SPA unit checks | `npm --prefix src/web test` |
| Local end-to-end (SWA CLI emulator + in-memory SAMPLE API) | `tools/run_local.sh` → open http://localhost:4280 |
| Browser E2E (with `run_local.sh` running) | `npm --prefix src/web run e2e` |
| Validate sample data | `python3 tools/seed_sample_data.py --dry-run` |
| Validate a result file | `python3 tools/validate_result_schema.py <file.json>` |

**Mock sign-in locally.** The SWA CLI emulator shows a login form: enter a SAMPLE user id from `tools/sample_data/sample-data.json`, for example `sample-user-alice` (PMO, Alpha), `sample-user-bob` (PMO, Beta), `sample-user-rita` (Reviewer, Alpha), `sample-user-mark` (Maintainer, Alpha), `sample-user-oscar` (Operator, Alpha). Choose provider `aad`.

**Why `tools/local-api` exists.** Azure Functions Core Tools could not be downloaded in the build environment. `tools/local-api` is a loopback-only, dev-only host that serves the *same* `ApiHost` (routing, auth, processing) as the Functions entry point. It refuses to start without `PILOT_LOCAL_DEV=true`. With Core Tools installed you can instead run `func start` in `src/api/PmoReview.Api` (copy `local.settings.example.json` to `local.settings.json`).

**API runtime.** The managed API runs on `dotnet-isolated:9.0` because SWA CLI 2.0.10 rejects 10.0 (F21). To move to .NET 10 later: set `<TargetFramework>net10.0</TargetFramework>` in `src/api/PmoReview.Api/PmoReview.Api.csproj` and `platform.apiRuntime` to `dotnet-isolated:10.0`, run `dotnet test`, then `tools/deploy_free.sh app <subscriptionId>`.

## Deploy (Phase 6 — only after approval)

1. `az login`; pick the subscription.
2. Copy `infra/main.parameters.example.json` → `.local/params.json`; fill email, region, `budgetStartDate` (first of the current month).
3. `tools/deploy_free.sh whatif <subscriptionId> .local/params.json` → send `.local/whatif.txt` to Gokul.
4. After the written "approved": `tools/deploy_free.sh apply <subscriptionId> .local/params.json --approved-by "Gokul <date>"`.
5. Each test user signs in once at the app URL, opens `/.auth/me`, copies `userId`; register:
   `COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py --register-user <userId> --project SAMPLE-ALPHA --roles PMO`
   (get the connection string with `az cosmosdb keys list --type connection-strings`; do not paste it anywhere else).
6. Run one synthetic SAMPLE request in the UI; save a screenshot of the result.
7. `tools/cost_check.sh rg-pmo-review-pilot` now and again after 24–48 h → paste output into `docs/COSTS.md`.

## Phase 2 — Entra hand-off (after "approved" on the Phase 2 what-if)

Full steps and the E2E test script: `workflows/08-entra-handoff.md`. In short:
1. Cloud admin account → start the Entra ID P2 / Governance trial (portal).
2. `bash tools/setup_entra.sh identities <tenant>.onmicrosoft.com <approver-upn>` → group + test joiner + licences.
3. Portal: catalog + access package (approval by the approver, requestor timeline allowed, expiry ≤ 180 days); copy the My Access link.
4. `.local/params.json`: `budgetAmount 2000`, `enableHandoff true`, `workerLocation`, `workerRuntimeVersion` → `tools/deploy_free.sh whatif …` → approval → `apply … --approved-by "Gokul <date>"`.
5. `tools/deploy_free.sh grant-graph <sub>` if apply could not grant it (needs Global Administrator).
6. App: Maintainer publishes a new `A-MAP-ENG-DEV` version with the My Access link.
7. Run the E2E script; record results in `docs/DEPLOYMENT_LOG.md`.

| Situation | Action |
|---|---|
| Worker code change | `tools/deploy_free.sh worker <sub>` |
| Hand-off stuck on an old status | Check the worker's `handoff-sync done` log line and *Last status check failed (…)* in the app |
| Turn the hand-off off | `az staticwebapp appsettings set -n <swa> -g rg-pmo-review-pilot --setting-names ENTRA_HANDOFF_ENABLED=false`; `az functionapp stop …` |

## Operations

| Situation | Action |
|---|---|
| Review stuck in Queued/Running | Operator page → "Recover stuck reviews" (marks > `STUCK_RUN_MINUTES` as Failed/Timeout); PMO retries. |
| Review Failed | PMO opens the request → "Retry review". Failure category only; details are in logs by correlation id. |
| User reports an error | Ask for the "Reference" (correlation id) shown in the error bar; search logs for it. |
| New test user | Step 5 above. Unregistered users see their own user id and no data. |
| Change a rule or mapping | Maintainer creates a new **Draft** version and publishes it. Published versions are immutable; old runs keep their pinned versions. |
| Redeploy app only (code change, failed upload) | `tools/deploy_free.sh app <subscriptionId>` — no infrastructure change. |
| Rotate Cosmos key | `az cosmosdb keys regenerate --key-kind primary`; then re-run the app-setting step from `deploy_free.sh` (secondary first if you need zero downtime). |
| Cost alert | `workflows/incident-recovery.md`. |
| Tear down | `az group delete -n rg-pmo-review-pilot` (deletes data; the Cosmos free-tier slot is freed for the subscription). |
