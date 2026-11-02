# Next session — handoff (updated 31 Oct 2026: API on .NET 10 and account deletion switch, both code only; v1.1 live, agent offboarding switched on)

**Start the next session with:**
> Continue the PMO Access Portal v1.1 rollout on branch `claude/elegant-darwin-nfot4p`. Read `CLAUDE.md`, `docs/NEXT_SESSION.md`, `docs/V1_1_DEPLOY.md` and the last 8 rows of `docs/DEPLOYMENT_LOG.md`. Then guide me through *Next steps* below, one at a time, from a fresh Cloud Shell. Log every result in `DEPLOYMENT_LOG.md`.

Read first: `CLAUDE.md`, this file, `docs/V1_1_DEPLOY.md` (runbook incl. *Fresh Cloud Shell* and *Option 3*), last rows of `docs/DEPLOYMENT_LOG.md`, `REVIEW.md` (open findings).
Branch **`claude/elegant-darwin-nfot4p`**, pushed (latest commit or later; 31 Oct session: `a88e69a`, `2ccb5f1`, `163a9ed`, then the handoff commit). No PR. v1.1 instructions: `PMO_Access_Portal_v1.1_Feature_Prompt.md`; decisions `docs/V1_1_PLAN.md` (12a, 13a–c, F3a, F4a, **F5a**); phase results `docs/V1_1_PHASES.md`.

## Live state (evidence in `DEPLOYMENT_LOG.md`, 30 Oct 2026)
- **v1.1 option 1 applied** (~16:40 UTC, `apply` of `9105ddd`, approved by Gokul): 8 new Cosmos containers, worker data roles, app + worker deployed, cost unchanged (budget ₹2,000, spent ₹12.57).
- **Runbook step 5 done:** PEST and VESTACY name the agent's (pilot) tenant and are marked *not production*.
- **`verify_features` at 17:08 UTC** (app + worker `9105ddd`): 21 PASS, 1 WAIT (15 go-live), 0 FAIL; line 16 PASS.
- **Decision F5a — option 3 "Entra only":** the agent revokes sessions, removes the TAP and group memberships and disables the account of a confirmed leaver. No new Azure right (P3 §3 D deferred). Trigger unchanged: a PMO or Operator clicks *Offboard user* and *Confirm*; the agent does the rest.
- **Agent offboarding switched on** — inferred from the live run (the confirm was accepted and the worker executed); the `offboarding … on --approved-by` output was **not seen**. Dry run reviewed: 2 new Graph permissions (`User.RevokeSessions.All`, `User.EnableDisableAccount.All`), 5 held, `OFFBOARD_SUBSCRIPTIONS` = pilot subscription, `OFFBOARD_DELETE_USER=false`.
- **Live check T (first offboarding, pilot test account): Partially completed.**
  - Done: TAP removed; membership of `azr-pes-rg-data-dev-ph-weu-dataengineers` removed.
  - Refused (403): revoke sessions and disable. The new permissions had not reached the worker's token yet (F61, up to ~24 h).
  - Leftover "still in" that same group: most likely Entra's index lag (F104). Fixed in `7922922`.
  - No Azure leftover: the worker could list the subscription's role assignments; the account had no direct Azure role.
  - The test account is still enabled until tomorrow's re-run, unless Gokul disabled it by hand.
- **New on 2 Nov (code only, not deployed; branch `claude/portal-ux-redesign`):**
  - **Agent TAP to the Enterprise ID on the request** (Gokul: "send the tap … to the user enterprise id which was asked for … automatic … ask them to reset the password immediately"). Live symptom: the joiner was not in the people inventory, so F2 escalated *TAP recipient mismatch* and the ID team had to hand the pass over.
    - Recipient now: the Enterprise ID entered on the request, approved by the Owner and Security; the approval page and mail name it. Refused for a guest form, a personal domain, the requester's / Owner's / an approver's own address, or when the people inventory says otherwise. Then the old F2 inventory rule; else the escalation (`docs/ROOT_CAUSE.md`, "Change on 2 Nov 2026").
    - Mail: "IMPORTANT: right after you sign in with the Temporary Access Pass … change the temporary password" + step 4 `https://mysignins.microsoft.com/security-info/password/change` (F119).
    - Requests already left to the ID team keep their pass with the ID team (hand back does not re-send); test with a new request, with an Enterprise ID mailbox you control.
  - **Returned "Admin-level" for Data Engineer, fixed** (Gokul's live test, 2 Nov: Data Engineer on PEST ph returned with "Admin-level access is requested …").
    - Cause: rows whose roles include Contributor are flagged Privileged on import, and the review ranked every Privileged row as admin.
    - Now: an admin level (Admin, Owner …) still needs an administrative task. A job-role level on a Privileged row (Data Engineer …) needs the tasks that use the access (build or run pipelines, ingest, transform, deploy, support …). The reason names the row and its roles; the form warns before submit.
  - **Returned submission fixed in one go:** a request from an "All"/several-resources submission lists the other returned requests of that submission on the resubmit page. One correction resubmits them all (ticked by default); each keeps its own resource and level. One request is one group: more resources = a new request (said on the form).
  - **Form race fixed:** ticking resources quickly could show the groups of an earlier selection (a slower resolve answer overwrote the latest). Only the latest answer counts now.
  - **Databricks A3–A5 automated** (Gokul: "make A3, A4 and A5 automated … decisions on the entitlements according to the name of the group … tests part of automation … log it"). Policy `config/databricks-access.json`:
    - every level: Workspace access; readers: + Databricks SQL access;
    - Unity Catalog: reader `USE_CATALOG`, `USE_SCHEMA`, `SELECT`; writer + `MODIFY`; engineer + `CREATE_TABLE`;
    - admin/owner names, and names with no or two levels: people.
  - **By hand now:** `aim_setup.sh apply … --grant <catalog>.<schema>` (A3 + A4 + A5 + 4 tests + `.local/databricks-setup.log`; self-test passes, 2 mutation checks).
  - **Agent (off until approved):** `AGENT_DATABRICKS`; `deploy_free.sh databricks $SUB on --account-id … --workspace … --catalogs …` (dry run → "approved" → `--approved-by`), then `app` (the website follows the switch). Behaviour:
    - acts at With Role Team for `dbx:` rows;
    - tests live and logs to the trail, the checks and the audit log;
    - the verification re-tests every role-team report;
    - a `dbx:` row with Azure roles goes to people.
  - **Databricks-side rights of the worker identity** (account admin, workspace admin, `USE CATALOG` + `MANAGE` per catalog) are printed by the dry run. They are F118 and ASSUMED until the first run.
  - `verify_features` line 18 (WAIT until switched on).
  - **Open (rule 1):** the pilot Databricks workspace `dbw-pmo-pkt2ofpqmlf3e` has no recorded approval or cost line.
  - **Website redesign** (SPA only; same API, same flow, same guardrails; ships with the next `app` deploy, no approval):
    - **Shell:** sidebar navigation with a live *waiting on you* count on My actions; avatar with your roles; slimmer permanent scope strip; compact header on phones.
    - **Requests tracker:** pipeline tiles per step (click to filter), *In progress / Needs attention / Completed / Closed* chips, search (`/` focuses, Enter opens a single match), a 7-step progress bar per row, *more than 24 h* waits highlighted, and a *waiting on you* callout.
    - **Request page:** journey stepper with the time per step; two columns (actions on the left, facts, decisions and time bars on the right); the check digest shows failed, flagged and not-checked checks first, with the full table one click away; the decision panel is marked *Waiting on you*.
    - **New request:** four numbered parts, plus a live *Your request* summary with a readiness checklist; the submit bar stays in view; a justification character count.
    - **My actions:** cards grouped by action, longest wait first.
    - Logic is in `src/web/src/lib/tracker.ts` (unit tests in `tracker.test.ts`); new e2e test *tracker and inbox*.
  - **SWA API on .NET 10** (`2ccb5f1`, F21): SWA CLI 2.1.0 accepts `dotnet-isolated:10.0` and Learn lists it; .NET 9 support ends **10 Nov**. Needs `app`. Live proof = the deploy ends "Deployed commit …" and the site answers. Rollback: check out `a88e69a`, `app` (.NET 9).
  - **Account deletion switch** (`163a9ed`, decision F5b; Gokul asked "make it on"): `offboarding $SUB on --delete-user` adds Graph `User.ReadWrite.All` (F75, the only option, tenant-wide) and `OFFBOARD_DELETE_USER=true` — soft delete after disabling, restorable 30 days. Dry run → "approved" → `--approved-by`. `delete-off` switches it off. Fix with it: a refused or skipped planned delete is a leftover (was Completed + PMO mail, REVIEW 25).
  - `verify_features` line 17 now also checks deletion; line 16 no longer counts it as unapproved.
  - **Role renamed:** DataMaintainer → **DataSteward** (Data Steward), Gokul 31 Oct. Rows saved under the old name still load and are read as DataSteward; the tools register only the new name. Needs `app` before registering it.
- **Code not yet deployed** (unless Gokul ran `worker` + `app` after `7922922`; `verify_features` line 1 tells):
  - worker: a run that sees no subscription is never Completed; verify looks again before a removal of its own counts as a leftover; a 403 names the token delay;
  - app: the switched-off message is information, not an error; the website follows the worker's `AGENT_OFFBOARDING` on every `app` deploy.
- **Agent TAP:** `AGENT_TAP=true` live, but `TAP_MAIL_SENDER` is not set, so the v1.1 worker mails no pass and the ID team hands it over. Line 5b reads WAIT with the new checker.
- **Mail:** ACS, environment *test*, every mail redirected to the test mailbox.
- **People:** each project has the Gmail tester (Requester, Owner, IdTeam, RoleTeam, PMO, Operator) and the pilot admin (Security). Data Steward and ProdApprover not registered yet (output not seen). No people inventory uploaded yet.

## Next steps (in order)
1. **Fresh Cloud Shell** (`docs/V1_1_DEPLOY.md`): clone the branch, then `tools/deploy_free.sh worker $SUB` and `tools/deploy_free.sh app $SUB` (no approval). The `app` deploy now ships the **.NET 10 API**: it must end "Deployed commit …"; then sign in and open a request. If it fails with "Function language info isn't provided", roll back (`git checkout a88e69a && tools/deploy_free.sh app $SUB`) and log it under F21. Check the offboarding settings (read-only):
   `az functionapp config appsettings list -g rg-pmo-review-pilot -n func-pmo-worker-pkt2ofpqmlf3e --query "[?name=='AGENT_OFFBOARDING' || name=='OFFBOARD_SUBSCRIPTIONS' || name=='OFFBOARD_DELETE_USER']" -o table`
   Then `bash tools/verify_features.sh $SUB`. Expected: lines 1, 16, 17 PASS; 5b and 15 WAIT. Log it as live check U.
2. **Graph permissions held** (read-only): `tools/deploy_free.sh grant-graph $SUB` prints them. `User.RevokeSessions.All` and `User.EnableDisableAccount.All` must be listed. If they are missing, the switch-on did not run: dry run again, wait for "approved", then `--approved-by`.
3. **Entra check:** the test account should no longer be in `azr-pes-rg-data-dev-ph-weu-dataengineers`. That confirms F104 (index lag).
3b. **Account deletion (decision F5b):** `tools/deploy_free.sh offboarding $SUB on --delete-user` (dry run, after step 1's `worker`): expect `User.ReadWrite.All` NEW, the 7 others held, `OFFBOARD_DELETE_USER=true`, cost 0. Gokul replies "approved" → same command `--approved-by "Gokul <date>"` (Global Administrator or Privileged Role Administrator). `verify_features` line 17: PASS with "delete accounts on". The new permission can take up to 24 h (F61): until then a delete is refused and the run ends Partially completed (account disabled, not deleted).
4. **Re-test offboarding at least 24 h after the grant** (from about 31 Oct evening; for the delete, 24 h after step 3b). With deletion on, the pilot test account is soft-deleted (restore: Entra admin center → Users → Deleted users).
   - Same request page → *Offboard user* → tick → *Confirm*.
   - Expected: revoke and disable Done, status **Completed**, one mail to the PMO (redirected to the test mailbox).
   - If a removal shows late, the run stays *Executing* for up to ~10 minutes, then completes.
5. **Runbook steps 6–7.**
   - **Data Steward** (role `DataSteward`, renamed from DataMaintainer on 31 Oct; needs the `app` deploy of step 1 first): the Gmail tester on PEST and VESTACY. Give the full role list, because registering replaces a person's roles.
   - **ProdApprover:** a third account, `pmo.prodapprover@gopalakrishnanbaskarangmail.onmicrosoft.com` (display name *PMO Prod Approver (pilot)*), created in the Entra admin center with an auto-generated password, signed in once, then registered on PEST and VESTACY with `--roles ProdApprover` only.
   - Then the people inventory upload on the Data uploads page (validate → diff → apply).
6. **Quality before more changes.**
   - Re-check 5 is done (REVIEW.md), but **not independent** (same session as the code). A separate reviewer should re-read findings 25–27 and the `--delete-user` switch.
   - Playwright 11/11 on `163a9ed` (31 Oct). Open from it: Low 27 (a Leaver step skipped by a half-way switch-off is not a leftover).

## Open decisions (Gokul; any Azure or permission change: dry run or what-if, then "approved")
- **Option 2, agent TAP mail:** `tools/deploy_free.sh tap $SUB on --sender <shared mailbox>` plus the Exchange RBAC for Applications scope (F81). Until then the ID team issues passes.
- **Azure role removal by the agent** (F5a, P3 §3 D deferred). Two ways:
  - a custom delete-only role (read + delete role assignments) with the Learn-documented users-only delete condition;
  - a condition on RBAC Administrator, proven live first (Learn documents no way to switch off its create right).
- **Go-live** (line 15): real addresses on the Contact inventory page, then `tools/deploy_free.sh environment $SUB prod` (`docs/GO_LIVE.md`).
- **External upload API:** stays off; it needs hosting option B or C (`docs/HOSTING.md`).
- **Account deletion:** coded (step 3b); on only after its dry run and "approved".
- **REVIEW.md open:** Medium 7, 8, 9 (partly), 11, 12; Low 5 (partly), 15, 18, 19, 27.
- **Older items:**
  - a custom ACS mail domain if more than about one request an hour is expected (F70);
  - remove the unused `AuditLog.Read.All`;
  - remove the worker's old Cosmos roles on `handoffs`, `joinerPasses`, `roleMappings`;
  - Phase 2 SAMPLE leftovers in the tenant;
  - ADO, ARM role read and Azure OpenAI stay off;
  - a production import with real scopes, tenant ids and contacts.
- **Dates:** deploy the .NET 10 API (`app`, step 1) before **10 Nov** (.NET 9 end of support, F21); P2 trial ends ~15 Nov (F48).

## Tests at hand-off (31 Oct, on `163a9ed` + panel caption; website redesign: SPA 37/37, Playwright 12/12, InfraConsistency 16/16)
- .NET 501 (500 pass, 1 opt-in Cosmos emulator skip); all projects net10.0; `dotnet build PmoReview.slnx` with no warnings.
- SPA 24/24; web build OK.
- Python tool self-tests pass (verify_features, import, register, seed).
- Mutation checks: SWA CLI pin 2.0.10 fails the runtime test; the refused-delete test failed before its fix.
- Playwright 11/11 (SWA CLI 2.1.0 emulator with the .NET 10 config).
- Local: the container used Ubuntu's `dotnet-sdk-10.0` (10.0.114); Cloud Shell installs .NET 10 itself (`ensure_dotnet10`).

## Reference: re-import (only after workbook changes; files are not in git)
Upload both workbooks (Cloud Shell → *Manage files* → *Upload*), then run `mv ~/*.xlsx ~/onboarding-requests/.local/`. The response file name has spaces.
```bash
cd ~/onboarding-requests; SUB=$(az account show --query id -o tsv)
PY=$(tools/cosmos_python.sh); TID=$(az account show --query tenantId -o tsv)
export COSMOS_CONNECTION_STRING=$(az cosmosdb keys list -g rg-pmo-review-pilot -n cosmos-pmo-pilot-pkt2ofpqmlf3e --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
CLS=$(ls .local/Entra-Group-Classification*.xlsx | head -1)
COMMON="--test-scope /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access --domain-suffix @gopalakrishnanbaskarangmail.onmicrosoft.com --production-tenant no --apply"
$PY tools/import_workbook.py --tenant PEST --project PEST --project-name "PEST data platform" --classification "$CLS" \
  --response ".local/Data Response document - PEST Support Reader-Writer Groups 2026-10-15.xlsx" --tenant-id $TID $COMMON
$PY tools/import_workbook.py --tenant Vestacy --project VESTACY --project-name "Vestacy data platform" --classification "$CLS" --tenant-id $TID $COMMON
$PY tools/register_user.py --project PEST --list      # register: full role list, it replaces the person's roles
unset COSMOS_CONNECTION_STRING
```
Without `--update`, existing rows are kept. Never type a literal `<…>` placeholder: bash reads `<` as a file redirect.

## Environment (no secrets)
- Subscription `5b9f9810-0512-49e5-b7f7-0159bc488570`.
- RG `rg-pmo-review-pilot`.
- SWA `swa-pmo-pilot-pkt2ofpqmlf3e` (https://icy-smoke-04dcd4300.6.azurestaticapps.net).
- Cosmos `cosmos-pmo-pilot-pkt2ofpqmlf3e` (db `pmoreview`, 21 of 25 containers).
- Worker `func-pmo-worker-pkt2ofpqmlf3e` (FC1) + storage `stpmoworkpkt2ofpqmlf3e`.
- Mail: ACS (Azure-managed domain).
- Access RG (test scope) `rg-pmo-sample-access`.
- Tenant domain `@gopalakrishnanbaskarangmail.onmicrosoft.com`.
- Test mailbox (all mail in the test environment): `gopalakrishnan.baskaran@gmail.com`.
- Cloud Shell is reset after a short idle time. It loses only the clone, `.local/` (params, workbooks, deploy stamps) and `~/.dotnet`; Azure keeps everything.

## Working rules
- Any Azure resource or permission change: show the what-if or dry run and wait for an explicit "approved".
  - Such changes: `provisioning … on`, `tap … on`, `privileged … on`, `offboarding … on`, `revoke-graph-writes`, `email`, `apply`.
  - No approval needed: `app`, `worker`, `environment`, imports, registrations, any `… off`.
- The agent never approves and never extends its own rights. Offboarding runs only for a plan a person confirmed.
- Evidence only: log what the pasted output shows. Mark inferences as inferred.
- Never paste or log a TAP, password or connection string. Never record personal user ids in docs. Real workbooks stay out of git.
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`. Push to `claude/elegant-darwin-nfot4p` (`git push -u origin claude/elegant-darwin-nfot4p`). No PR unless asked.
