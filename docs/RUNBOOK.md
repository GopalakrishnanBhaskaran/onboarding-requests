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

**API runtime.** The managed API runs on `dotnet-isolated:10.0` (30 Oct 2026; SWA CLI 2.1.0 accepts it, F21). Rollback if the first deploy is refused: check out the commit before the switch and run `tools/deploy_free.sh app <subscriptionId>` (it deploys .NET 9, supported until 10 Nov 2026).

## Deploy (only after approval)

Full v2 steps, approvals and the live end-to-end test: `workflows/09-onboarding-flow-v2.md`. In short: `params` → `whatif` → **approved** → `apply --approved-by` → `grant-graph` (read roles; **approval**) → `revoke-graph-writes` dry run → **approved** → `--approved-by` → seed with `--domain-suffix` → register testers with v2 roles → `tools/workflow_diag.sh`.

| Situation | Action |
|---|---|
| App code change | `tools/deploy_free.sh app <sub>` (no infrastructure change) |
| Worker code change | `tools/deploy_free.sh worker <sub>` |
| Where is a request stuck? | `bash tools/workflow_diag.sh <sub>` (worker, settings, Graph roles, latest requests, open issues) |
| Stop the agent (cost or incident) | `az functionapp stop -g rg-pmo-review-pilot -n <worker>`; requests keep their state; Entra checks, SLA reminders and the outbox wait until it is started again |

## Pilot session start (Azure Cloud Shell, Bash)

Cloud Shell's home folder is reset after a short idle time. Start every session with:
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; az account set --subscription $SUB
cd ~/onboarding-requests 2>/dev/null || { cd ~ && git clone -q -b claude/eager-ride-lx6he8 https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests; }
git fetch -q origin claude/eager-ride-lx6he8 && git checkout -q claude/eager-ride-lx6he8 && git pull -q origin claude/eager-ride-lx6he8   # an old clone gets the latest code
git log --oneline -1
```

## Register people (who may use the app)

- **Who runs it:** someone who can read the Cosmos keys, i.e. Contributor on `rg-pmo-review-pilot`.
- **Before:** the person needs an account that can sign in to the app.
- They don't need to sign in first: registration by **sign-in name** works straight away. The user id also works.
- **Roles** (comma-separated; one person can have several):

| Role | Can do |
|---|---|
| `Requester` | submit and resubmit requests |
| `Owner` | approve or reject at the Owner stage, only for requests that name them as Owner |
| `Security` | approve or reject at the Security stage (never on a request they approved as Owner); issues and reports |
| `IdTeam` | ID team tasks: pick up, hand back, report |
| `RoleTeam` | role assignment team tasks: pick up, hand back, report |
| `PMO` | edit the mapping sheet and the Contact inventory; reports |
| `Operator` | issues, reports, recover runs, resume after a fix; the Contact inventory |

```bash
# after "Pilot session start"
PY=$(tools/cosmos_python.sh)
CS=$(az cosmosdb keys list -g rg-pmo-review-pilot -n cosmos-pmo-pilot-pkt2ofpqmlf3e --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)

# Register by sign-in name (pick the roles this person needs)
COSMOS_CONNECTION_STRING="$CS" $PY tools/register_user.py --project PEST --user <sign-in name> --roles Requester

# Several roles, another project
COSMOS_CONNECTION_STRING="$CS" $PY tools/register_user.py --project VESTACY --user <sign-in name> --roles Owner,Security

# Check who is registered
COSMOS_CONNECTION_STRING="$CS" $PY tools/register_user.py --project PEST --list

# Remove access
COSMOS_CONNECTION_STRING="$CS" $PY tools/register_user.py --project PEST --user <sign-in name> --remove

unset CS
```

**Check:**
- the person signs in, picks the tenant at the top right, and sees the pages for their roles;
- `--list` shows them.

**Rules:**
- Give the sign-in name as a plain mail address, not the `#EXT#` or `live.com#` form.
- Security must be a different person from the Owner approver on a request.
- Never paste or record personal user ids in docs.
- A Contact inventory address only routes mail. It gives no right in the app.

## Go live to prod (pilot tenant)

Full detail: `docs/GO_LIVE.md`. No Azure resource or permission change, so no approval is needed. Gokul gives the go / no-go.

1. **Before:**
   - `worker` and `app` are deployed at the latest commit (`tools/deploy_free.sh worker $SUB`, `tools/deploy_free.sh app $SUB`);
   - the Contact inventory is filled for **PEST and Vestacy** (PMO, in the app);
   - the smoke test passed in test mode (the completion mail is meant for the requester, not the Owner).
2. **Switch and check** (after "Pilot session start"):
   ```bash
   tools/deploy_free.sh environment $SUB prod          # settings only: APP_ENVIRONMENT=prod, no redirect, on worker and website
   bash tools/verify_features.sh $SUB | tee .local/verify.txt
   ```
   - Lines **6c** (contacts), **14** (no failed or stuck mail) and **15** (prod, no redirect) must be PASS.
   - The website header shows **PROD**, and the yellow test bar is gone.
3. **Announce:** send the go-live mail (template in `docs/GO_LIVE.md`).
4. **Rollback** (mail back to the test mailbox at once):
   ```bash
   tools/deploy_free.sh environment $SUB test --redirect gopalakrishnan.baskaran@gmail.com
   ```

## Actual tenant (private endpoint only)

- Steps, roles and inputs: `docs/GO_LIVE_ACTUAL_TENANT.md` section 6.
- The same `register_user.py` and `environment prod` commands run from the jump box or agent **inside the VNet**, signed in with Entra instead of the Cosmos key (after prompt A task A12).

## Operations

| Situation | Action |
|---|---|
| Review stuck in Queued/Running | Issues and reports → "Recover stuck reviews" (Operator; marks > `STUCK_RUN_MINUTES` as Failed/Timeout); the agent retries, at most 3 times, then Failed-Needs-Dev. |
| Request in Failed-Needs-Dev | Issues and reports → developer issue report; fix the cause (and redeploy if it was code); Operator opens the request → **Resume after fix** (back to the stage that failed, attempts reset; a failed review starts a new review). |
| Approval waiting too long | Agent reminds at SLA, escalates to the manager at 2× SLA (`OWNER_SLA_HOURS`, `SECURITY_SLA_HOURS`). Never auto-approves. |
| Team task not picked up | Same SLA handling (`ID_TEAM_SLA_HOURS`, `ROLE_TEAM_SLA_HOURS`); issue `TaskSlaBreached`. |
| Unauthorized approval issue | Someone other than the named Owner / a Security member tried to decide. Ignored; Security reviews. |
| User reports an error | Ask for the "Reference" (correlation id) shown in the error bar; search logs for it. |
| New person needs access | Register them by sign-in name (section *Register people*). They don't need to sign in first. Unregistered people see no data. |
| Change the mapping sheet | PMO edits it in the app (Mapping sheet page). In-flight requests for that resource are re-reviewed automatically. |
| Rotate Cosmos key | `az cosmosdb keys regenerate --key-kind primary`; re-run the app-setting step from `deploy_free.sh`. |
| Cost alert | `workflows/incident-recovery.md`. |
| Tear down | `az group delete -n rg-pmo-review-pilot` (deletes data; frees the Cosmos free-tier slot). Needs Gokul's written decision. |
