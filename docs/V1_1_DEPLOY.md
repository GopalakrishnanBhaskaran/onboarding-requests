# v1.1 deploy — P7 option 1: data and code, every new switch off

Chosen by Gokul on 30 Oct 2026 (P6 approved; decisions 12a and 13a kept). This file is the runbook. **Steps 1–5 ran on 30 Oct 2026** (what-if clean, "approved", `apply` of `9105ddd` with the app and the worker, tenant id and production flag on PEST and VESTACY; `verify_features` line 16 PASS; `docs/DEPLOYMENT_LOG.md`). Steps 6–7 are next. **31 Oct 2026:** the steps in *31 Oct 2026: steps run, in order* below ran (API back on .NET 9, offboarding account deletion, Databricks workspace, groups and Unity Catalog grants). CLAUDE.md rule 4 still applies to any further Azure change.

Run in Cloud Shell, or on a Mac after `bash tools/mac_setup.sh <subscriptionId>` (`docs/MAC_DEPLOY.md`). Use `main` (v1.1 merged with PR 5; today's changes with PR 4, before that merge `claude/nifty-bardeen-j0pqm3`). Cloud Shell is ephemeral: run each block below **whole**, from a fresh session (CLAUDE.md rule 12). Log every step in `docs/DEPLOYMENT_LOG.md`.

## What option 1 turns on, and what stays off
| Live after option 1 | Stays off (each needs its own approval later) |
|---|---|
| F1 Resource\* selector; F3 Prod read-only guard and ProdApprover escalations; F4 Data uploads page (in the app); F5 offboarding **plans** (read-only dry runs) | Agent TAP mail (option 2: `tap … on --sender`); offboarding writes (option 3: `AGENT_OFFBOARDING`, new permissions); the external upload API (`EXTERNAL_UPLOAD_API_ENABLED`, needs hosting option B or C) |

## Steps
1. **Parameters.** If `.local/params.json` is missing after a Cloud Shell reset, run `tools/deploy_free.sh params <subscriptionId>`.
2. **What-if (read-only).** Run `tools/deploy_free.sh whatif <subscriptionId> .local/params.json`, then send `.local/whatif.txt` to Gokul. Expect **only** these changes:
   - **Create:** 8 Cosmos SQL containers in `pmoreview`: `mappingVersions`, `peopleInventory`, `inventoryVersions`, `escalations`, `mappingQualityIssues`, `groupResolutions`, `offboardingRuns`, `auditEvents`.
   - **Create:** worker Cosmos data role assignments on `peopleInventory`, `escalations`, `mappingQualityIssues`, `groupResolutions`, `offboardingRuns`, `auditEvents`. The worker never gets `userAccess` or the upload versions.
   - **Modify:** worker app settings gain `AGENT_OFFBOARDING=false`, `OFFBOARD_DELETE_USER=false` and `OFFBOARD_SUBSCRIPTIONS` (empty).
   - **No delete, and no new resource type** (no Function App, storage account, Key Vault, App Service, VNet). If anything else appears, stop and report it.
   - **Expected noise (not a change):** about 29 "Modify" lines. They cover:
     - properties Azure fills in itself (Cosmos indexing and conflict defaults, read-only account properties, storage retention defaults, SWA provider and IP, the budget end date);
     - the worker identity shown as a `reference(...)` that resolves to the same principal at deploy time;
     - the template's own `ftpsState` and `minTlsVersion`.
   - **Worker app settings** are not part of the what-if, and the apply replaces all of them. The last section of `whatif.txt`, "Worker app settings", must say `removed by the apply: none` and `reset by the apply: none`. If it names anything, stop.
   - Two read-only checks before approving:
     - `az functionapp identity show -g rg-pmo-review-pilot -n <worker> --query principalId -o tsv` prints the principal id the what-if shows on the existing role assignments.
     - `az cosmosdb sql database throughput show -g rg-pmo-review-pilot -a <cosmos account> -n pmoreview --query resource.throughput` prints `1000`, so the database's `throughput: 1000` line changes nothing.
   - **Expected cost:** about ₹0 extra. The database stays on the free tier with 21 of the 25 containers allowed under shared throughput (F90), and there is no new compute. The ₹2,000/month budget and its alerts are unchanged.
3. **Approval.** Gokul replies "approved". Then run `tools/deploy_free.sh apply <subscriptionId> .local/params.json --approved-by "Gokul <date>"`.
4. **Code** (no approval needed): the apply already deploys the app and the worker, and grants the worker's Graph read roles. Run `tools/deploy_free.sh app <subscriptionId>` and `tools/deploy_free.sh worker <subscriptionId>` only if one of those steps failed. No SAMPLE seed: leave `SEED_SAMPLE` unset.
5. **Tenant id and production flag per tenant.** Re-run the import with the same workbook files. Without `--update`, existing rows are kept.
   `COSMOS_CONNECTION_STRING=… python3 tools/import_workbook.py --tenant PEST --project PEST --classification <xlsx> --response <xlsx> --tenant-id <PEST tenant guid> --production-tenant yes --apply`
   Do the same for Vestacy. A tenant left without the flag counts as production, so a second person confirms offboarding there (12a). Offboarding works only for a project that names the agent's own tenant (13a).
6. **People.** Registering **replaces** that person's roles on the project, so first list them (`register_user.py --project PEST --list`) and give the full list plus the new role, e.g. `--roles Requester,Owner,IdTeam,RoleTeam,PMO,Operator,DataSteward`. Deploy `app` and `worker` of `7de7d25` or later first: older code does not know the name `DataSteward`. Per project:
   `python3 tools/register_user.py --project PEST --user <mail> --roles ProdApprover` (a person with no other role there; pilot: a new Entra account `pmo.prodapprover@gopalakrishnanbaskarangmail.onmicrosoft.com`, display name *PMO Prod Approver (pilot)*)
   `python3 tools/register_user.py --project PEST --user <mail> --roles <their roles>,DataSteward` (the Data Steward; `DataMaintainer` until 31 Oct 2026, now refused by the tools with the new name)
   The ProdApprover must not be a usual requester or approver of the same requests, because the app refuses those people at the Prod stage.
7. **People inventory (I2).** The Data Steward opens the Data uploads page: *People inventory* → validate → check the diff → apply. The TAP mail (option 2) and offboarding read it.
8. **Check.** Run `bash tools/verify_features.sh <subscriptionId> | tee .local/verify.txt`.
   - Line 16 should be **PASS**: the containers are present, the new switches are off, and every project has its tenant id and production flag.
   - Lines 1–15 should read as before the deploy.
   - Then open one request page as PMO, click **Offboard user** on a SAMPLE-free test person, and check that the plan appears within about 5 minutes. With the switch off, nothing changes.

## Fresh Cloud Shell (ephemeral: run the whole block every time)
Azure keeps every resource, setting and permission. A new session has no clone, no `.local/` (params, workbooks, deploy stamps), no `~/.dotnet` and no shell variables. Start every block with these lines (they are already in each block below):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
```
Only when needed inside a block: `tools/deploy_free.sh params $SUB` before a what-if or `databricks`; upload the workbooks again before an import; `PY=$(tools/cosmos_python.sh)` and `CS=$(az cosmosdb keys list … -o tsv 2>/dev/null)` for `register_user.py` / `import_workbook.py` (never printed; `unset CS` after). Quote `grep` patterns that contain `!` in single quotes.

## 31 Oct 2026: steps run, in order (full blocks)
Results and evidence: `docs/DEPLOYMENT_LOG.md` (31 Oct rows). Every approval-gated step has a read-only block first; run the `--approved-by` block only after reading it.

### A. App redeploy: API back on .NET 9, clear gateway message, offboarding *Make a new plan* (code only, no approval)
Why: the first `dotnet-isolated:10.0` deploy left every page with *Backend call failure* (F21). Expect `API runtime: dotnet-isolated 9.0`, `Project deployed`, `Deployed commit …`.
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
rm -rf ~/.nuget/packages ~/.npm ~/.cache/pip ~/onboarding-requests/src/web/node_modules ~/onboarding-requests/.local/api ~/onboarding-requests/.local/worker*
tools/deploy_free.sh app $SUB --approved-by "Gokul 2026-10-31"
rm -rf ~/.nuget/packages ~/.npm src/web/node_modules .local/api .local/worker*; df -h ~
```
Ran 31 Oct ~05:30 UTC (`ca3ceaf`). The worker was not changed (only on a worker code change: the same block with `worker` instead of `app`).

### B. Offboarding account deletion (security change, approval)
Dry run (expect `User.ReadWrite.All` NEW or already held, `OFFBOARD_DELETE_USER=true`, cost 0):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
tools/deploy_free.sh offboarding $SUB on --delete-user
```
After reading it:
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
tools/deploy_free.sh offboarding $SUB on --delete-user --approved-by "Gokul 2026-10-31"
```
Ran 31 Oct. Then in the app: on a run planned **before** the switch, click **Make a new plan** (needs block A): the new plan lists *DELETE /users/{id} — soft delete*. A delete refused within ~24 h is the token delay (F61). Check settings and runs: `bash tools/workflow_diag.sh $SUB` (after the prelude).

### C. Databricks serverless workspace (new resource, approval)
Dry run (expect: policy `pmo-pilot-allowed-types` gains `Microsoft.Databricks/workspaces`; one workspace, premium, `computeMode: Serverless`; budget and Cosmos lines are what-if noise; worker settings outside the template: none):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
tools/deploy_free.sh params $SUB
tools/deploy_free.sh databricks $SUB > .local/dbx-dryrun.txt 2>&1; tail -3 .local/dbx-dryrun.txt
grep -A12 'policyAssignments/pmo-pilot-allowed-types' .local/dbx-dryrun.txt
grep -E 'Resource changes|to create|to delete' .local/dbx-dryrun.txt
grep -E '^  [-+~x!] Microsoft' .local/dbx-dryrun.txt | grep -v -E 'containers/|sqlRoleAssignments|blobServices|roleAssignments/|Microsoft.Web/sites/|staticSites/'
grep 'outside the template' .local/dbx-dryrun.txt; grep 'expected cost' .local/dbx-dryrun.txt
```
After reading it (up to ~30 minutes while the policy takes effect; if Cloud Shell drops, run it again, it continues):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
tools/deploy_free.sh params $SUB
tools/deploy_free.sh databricks $SUB --approved-by "Gokul 2026-10-31" 2>&1 | tee .local/dbx-apply.txt
```
Ran 31 Oct: `dbw-pmo-pkt2ofpqmlf3e`, https://adb-7405607339118600.0.azuredatabricks.net. Cost: no compute while idle; default storage is billed while it holds data (F119).

### D. First Databricks account admin (browser, once)
1. The Gmail account cannot do this: the account console opens the account of the user's **default** tenant (F120). Find a **member** Global Administrator (read-only):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570
az account set --subscription $SUB
az rest --method GET --url "https://graph.microsoft.com/v1.0/directoryRoles(roleTemplateId='62e90394-69f5-4237-9190-012177145e10')/members?\$select=userPrincipalName,userType" --query "value[].{upn:userPrincipalName, type:userType}" -o table
```
   31 Oct: the pilot admin (member account) is one.
2. Private browser window → https://accounts.azuredatabricks.net → sign in as that member account (it becomes account admin).
3. Down arrow next to the username (top right) → copy the **Account ID** (never shown in a workspace).
4. **Security → Identity provider setup → Automatic identity management**: Enabled (default for accounts created after 1 Aug 2025, F116).
5. **User management** → add Gokul's account → **Roles** → **Account admin**. Then the blocks below run in Gokul's own Cloud Shell (the device-code sign-in as the pilot admin was refused on 31 Oct).

### E. Entra groups on the workspace (approval)
Plan (read-only; `ACC` = account id from D3, `G` = the Entra group; 31 Oct: the PEST Dev data engineers group):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
A=tools/databricks/aim_setup.sh; ACC='<Databricks account id>'; G='<Entra group>'
bash $A plan --account-id "$ACC" --workspace 7405607339118600 --group "$G"
```
After reading it (stops before assigning anything if automatic identity management is off):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
A=tools/databricks/aim_setup.sh; ACC='<Databricks account id>'; G='<Entra group>'
bash $A apply --account-id "$ACC" --workspace 7405607339118600 --group "$G" --approved-by "Gokul 2026-10-31"
bash $A status --account-id "$ACC" --workspace 7405607339118600
```
Ran 31 Oct: the group USER on the workspace; Gokul ADMIN. Admins: `--admin` (privileged).

### F. Unity Catalog data access (approval)
Catalog and schema from **Catalog** in the workspace (31 Oct: `dbw_pmo_pkt2ofpqmlf3e` / `default`). READ = `USE_CATALOG` + `USE_SCHEMA`, `SELECT`; WRITE adds `MODIFY`, `CREATE_TABLE` (F123). Plan (read-only):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
A=tools/databricks/aim_setup.sh; G='<Entra group>'
bash $A grants --workspace dbw-pmo-pkt2ofpqmlf3e --catalog dbw_pmo_pkt2ofpqmlf3e --schema default --group "$G=WRITE"
```
After reading it (adds only what is missing, reads it back: PASS / FAIL):
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
A=tools/databricks/aim_setup.sh; G='<Entra group>'
bash $A grants --workspace dbw-pmo-pkt2ofpqmlf3e --catalog dbw_pmo_pkt2ofpqmlf3e --schema default --group "$G=WRITE" --approved-by "Gokul 2026-10-31"
```
Ran 31 Oct: PASS `USE_CATALOG` on the catalog, PASS `CREATE_TABLE,MODIFY,SELECT,USE_SCHEMA` on `default`. A reader group: the same blocks with `"$G=READ"`.

### G. Member test (workspace SQL editor) and clean-up
As a member of the group (SAMPLE data only):
```sql
CREATE TABLE IF NOT EXISTS dbw_pmo_pkt2ofpqmlf3e.default.sample_access_test (id INT, name STRING, created_at TIMESTAMP)
  COMMENT 'SAMPLE: PMO onboarding pilot access test - drop after the test';
INSERT INTO dbw_pmo_pkt2ofpqmlf3e.default.sample_access_test VALUES (1, 'SAMPLE row one', current_timestamp());
SELECT * FROM dbw_pmo_pkt2ofpqmlf3e.default.sample_access_test;
```
A READ member: `SELECT` works, `INSERT` is refused. Afterwards (as Gokul): `DROP TABLE IF EXISTS dbw_pmo_pkt2ofpqmlf3e.default.sample_access_test;` and stop the SQL warehouse (DBUs while it runs).

### H. Cost check (read-only)
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; BR=main   # before PR 4 is merged: BR=claude/nifty-bardeen-j0pqm3
az account set --subscription $SUB
[ -d ~/onboarding-requests ] || git clone -q --depth 1 -b $BR https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git ~/onboarding-requests
cd ~/onboarding-requests && git fetch -q --depth 1 origin $BR && git checkout -q -B $BR FETCH_HEAD && git log --oneline -1
tools/cost_check.sh rg-pmo-review-pilot
```
Expect `OK: only allowed resource types` with the workspace listed; record the budget and month-to-date lines in `docs/COSTS.md`.

### Still open
- The PEST Dev Support Reader group does not exist in the pilot tenant: submit that request in the app (the agent creates the group after both approvals), then E and F with `=READ`.
- Retry the SWA API on .NET 10 before **10 Nov 2026** (F21), with a way to see the start-up error first.
- DBU and default-storage (DSU) rates not read yet (F119).

## Rollback
- **Code:** check out the previous commit and run `tools/deploy_free.sh app <subscriptionId>` and `tools/deploy_free.sh worker <subscriptionId>` again.
- **Data:** the new containers are empty or hold only v1.1 records. Leave them; deleting them is a resource change that needs approval.
- **Switches:** they are already off. `AGENT_OFFBOARDING=false` and `EXTERNAL_UPLOAD_API_ENABLED=false` are always allowed; account deletion off: `tools/deploy_free.sh offboarding $SUB delete-off`.
- **Databricks:** a grant is undone in the SQL editor (`REVOKE … ON SCHEMA … FROM \`<group>\``) and a workspace assignment in the account console (Workspaces → Permissions); the tools never remove. Deleting the workspace is a resource change (approval).

## Next options (each with its own dry run and approval)
- **Option 2:** `tools/deploy_free.sh tap <subscriptionId> on --sender <mailbox>`, plus the Exchange RBAC for Applications scope on that mailbox (F81).
- **Option 3, Entra only (chosen by Gokul on 30 Oct 2026, decision F5a):** the agent does the Entra steps of a confirmed offboarding; Azure role assignments stay with the role assignment team.
  1. Dry run: `tools/deploy_free.sh offboarding <subscriptionId> on`. It lists the Graph permissions (2 new: `User.RevokeSessions.All`, `User.EnableDisableAccount.All`; the others already held), the settings (worker `AGENT_OFFBOARDING=true`, `OFFBOARD_SUBSCRIPTIONS=<subscriptionId>`, `OFFBOARD_DELETE_USER=false`; website `AGENT_OFFBOARDING=true`) and cost ₹0. Nothing changes.
  2. Gokul replies "approved". Then: `tools/deploy_free.sh offboarding <subscriptionId> on --approved-by "Gokul <date>"` (needs a Global Administrator or Privileged Role Administrator for the Graph grants).
  3. `tools/deploy_free.sh worker <subscriptionId>` (code with the "no subscription visible" fix), then `bash tools/verify_features.sh <subscriptionId>`: line 17 **PASS**.
  4. Wait up to 24 hours for the new permissions to reach the worker's token (F61). Then test on a pilot test account: *Offboard user* → plan → tick → *Confirm*. Expected: sessions revoked, TAP and groups removed, account disabled. **Completed** with one mail to the PMO when nothing is left (live: the worker can list the subscription's role assignments); **Partially completed** + an escalation, no mail, when a direct Azure role assignment is left that the agent may not remove. A removal Entra shows late is looked at again on the next runs (F104).
  - Off at any time, no approval: `tools/deploy_free.sh offboarding <subscriptionId> off`.
  - **Account deletion (decision F5b, Gokul 30 Oct 2026):** `tools/deploy_free.sh offboarding <subscriptionId> on --delete-user` (dry run). It lists `User.ReadWrite.All` as NEW (tenant-wide; the only permission Graph offers for deleting a user, F75) and `OFFBOARD_DELETE_USER=true`. After "approved": the same command with `--approved-by "Gokul <date>"` (Global Administrator or Privileged Role Administrator). Then `bash tools/verify_features.sh <subscriptionId>`: line 17 PASS with "delete accounts on". New plans get a *Delete account* step after *Disable account*; plans made before stay as they were. Wait up to 24 hours for the permission (F61): until then a delete is refused and the run ends Partially completed with the account disabled. Restore within 30 days: Entra admin center → Users → Deleted users → Restore. Switch deletion off (no approval): `tools/deploy_free.sh offboarding <subscriptionId> delete-off`.
  - Azure role removal by the agent (P3 §3 D) is a later, separate choice: a custom delete-only role, or a proven condition on RBAC Administrator.
