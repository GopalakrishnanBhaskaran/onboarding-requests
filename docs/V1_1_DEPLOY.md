# v1.1 deploy — P7 option 1: data and code, every new switch off

Chosen by Gokul on 30 Oct 2026 (P6 approved; decisions 12a and 13a kept). This file is the runbook. **Nothing here has run yet.** CLAUDE.md rule 4 still applies: the what-if output is shown first, and nothing is applied before an explicit "approved".

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
   - **Expected cost:** about ₹0 extra. The database stays on the free tier with 21 of the 25 containers allowed under shared throughput (F90), and there is no new compute. The ₹2,000/month budget and its alerts are unchanged.
3. **Approval.** Gokul replies "approved". Then run `tools/deploy_free.sh apply <subscriptionId> .local/params.json --approved-by "Gokul <date>"`.
4. **Code** (no approval needed): run `tools/deploy_free.sh app <subscriptionId>`, then `tools/deploy_free.sh worker <subscriptionId>`. No SAMPLE seed: leave `SEED_SAMPLE` unset.
5. **Tenant id and production flag per tenant.** Re-run the import with the same workbook files. Without `--update`, existing rows are kept.
   `COSMOS_CONNECTION_STRING=… python3 tools/import_workbook.py --tenant PEST --project PEST --classification <xlsx> --response <xlsx> --tenant-id <PEST tenant guid> --production-tenant yes --apply`
   Do the same for Vestacy. A tenant left without the flag counts as production, so a second person confirms offboarding there (12a). Offboarding works only for a project that names the agent's own tenant (13a).
6. **People.** Run this per project:
   `python3 tools/register_user.py --project PEST --user <mail> --roles ProdApprover`
   Then the same with `--roles DataMaintainer`. The ProdApprover must not be a usual requester or approver of the same requests, because the app refuses those people at the Prod stage.
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
- **Option 3:** the offboarding permissions (`docs/PERMISSIONS.md` §3 B, C, D) on the pilot subscription only, plus an `offboarding … on|off` command, still to be built, with a dry run and `--approved-by`.
