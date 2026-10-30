# v1.1 deploy — P7 option 1: data and code, every new switch off

Chosen by Gokul on 30 Oct 2026 (P6 approved; decisions 12a and 13a kept). This file is the runbook. **Steps 1–5 ran on 30 Oct 2026** (what-if clean, "approved", `apply` of `9105ddd` with the app and the worker, tenant id and production flag on PEST and VESTACY; `verify_features` line 16 PASS; `docs/DEPLOYMENT_LOG.md`). Steps 6–7 are next. CLAUDE.md rule 4 still applies to any further Azure change.

Run in Cloud Shell, or on a Mac after `bash tools/mac_setup.sh <subscriptionId>` (`docs/MAC_DEPLOY.md`). Use the branch with v1.1 (`claude/elegant-darwin-nfot4p`, or `main` once it is merged), and run `git pull` first. Log every step in `docs/DEPLOYMENT_LOG.md`.

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
6. **People.** Registering **replaces** that person's roles on the project, so first list them (`register_user.py --project PEST --list`) and give the full list plus the new role, e.g. `--roles Requester,Owner,IdTeam,RoleTeam,PMO,Operator,DataMaintainer`. Per project:
   `python3 tools/register_user.py --project PEST --user <mail> --roles ProdApprover` (a person with no other role there)
   `python3 tools/register_user.py --project PEST --user <mail> --roles <their roles>,DataMaintainer`
   The ProdApprover must not be a usual requester or approver of the same requests, because the app refuses those people at the Prod stage.
7. **People inventory (I2).** The DataMaintainer opens the Data uploads page: *People inventory* → validate → check the diff → apply. The TAP mail (option 2) and offboarding read it.
8. **Check.** Run `bash tools/verify_features.sh <subscriptionId> | tee .local/verify.txt`.
   - Line 16 should be **PASS**: the containers are present, the new switches are off, and every project has its tenant id and production flag.
   - Lines 1–15 should read as before the deploy.
   - Then open one request page as PMO, click **Offboard user** on a SAMPLE-free test person, and check that the plan appears within about 5 minutes. With the switch off, nothing changes.

## Rollback
- **Code:** check out the previous commit and run `tools/deploy_free.sh app <subscriptionId>` and `tools/deploy_free.sh worker <subscriptionId>` again.
- **Data:** the new containers are empty or hold only v1.1 records. Leave them; deleting them is a resource change that needs approval.
- **Switches:** they are already off. `AGENT_OFFBOARDING=false` and `EXTERNAL_UPLOAD_API_ENABLED=false` are always allowed.

## Next options (each with its own dry run and approval)
- **Option 2:** `tools/deploy_free.sh tap <subscriptionId> on --sender <mailbox>`, plus the Exchange RBAC for Applications scope on that mailbox (F81).
- **Option 3, Entra only (chosen by Gokul on 30 Oct 2026, decision F5a):** the agent does the Entra steps of a confirmed offboarding; Azure role assignments stay with the role assignment team.
  1. Dry run: `tools/deploy_free.sh offboarding <subscriptionId> on`. It lists the Graph permissions (2 new: `User.RevokeSessions.All`, `User.EnableDisableAccount.All`; the others already held), the settings (worker `AGENT_OFFBOARDING=true`, `OFFBOARD_SUBSCRIPTIONS=<subscriptionId>`, `OFFBOARD_DELETE_USER=false`; website `AGENT_OFFBOARDING=true`) and cost ₹0. Nothing changes.
  2. Gokul replies "approved". Then: `tools/deploy_free.sh offboarding <subscriptionId> on --approved-by "Gokul <date>"` (needs a Global Administrator or Privileged Role Administrator for the Graph grants).
  3. `tools/deploy_free.sh worker <subscriptionId>` (code with the "no subscription visible" fix), then `bash tools/verify_features.sh <subscriptionId>`: line 17 **PASS**.
  4. Wait up to 24 hours for the new permissions to reach the worker's token (F61). Then test on a pilot test account: *Offboard user* → plan → tick → *Confirm*. Expected: sessions revoked, TAP and groups removed, account disabled. **Completed** with one mail to the PMO when nothing is left (live: the worker can list the subscription's role assignments); **Partially completed** + an escalation, no mail, when a direct Azure role assignment is left that the agent may not remove. A removal Entra shows late is looked at again on the next runs (F104).
  - Off at any time, no approval: `tools/deploy_free.sh offboarding <subscriptionId> off`.
  - Azure role removal by the agent (P3 §3 D) is a later, separate choice: a custom delete-only role, or a proven condition on RBAC Administrator.
