# Next session — handoff (updated 30 Oct 2026, evening: v1.1 live, agent offboarding switched on)

**Start the next session with:**
> Continue the PMO Access Portal v1.1 rollout on branch `claude/elegant-darwin-nfot4p`. Read `CLAUDE.md`, `docs/NEXT_SESSION.md`, `docs/V1_1_DEPLOY.md` and the last 8 rows of `docs/DEPLOYMENT_LOG.md`. Then guide me through *Next steps* below, one at a time, from a fresh Cloud Shell. Log every result in `DEPLOYMENT_LOG.md`.

Read first: `CLAUDE.md`, this file, `docs/V1_1_DEPLOY.md` (runbook incl. *Fresh Cloud Shell* and *Option 3*), last rows of `docs/DEPLOYMENT_LOG.md`, `REVIEW.md` (open findings).
Branch **`claude/elegant-darwin-nfot4p`**, pushed (`d4070bf` or later). No PR. v1.1 instructions: `PMO_Access_Portal_v1.1_Feature_Prompt.md`; decisions `docs/V1_1_PLAN.md` (12a, 13a–c, F3a, F4a, **F5a**); phase results `docs/V1_1_PHASES.md`.

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
- **Code not yet deployed** (unless Gokul ran `worker` + `app` after `7922922`; `verify_features` line 1 tells):
  - worker: a run that sees no subscription is never Completed; verify looks again before a removal of its own counts as a leftover; a 403 names the token delay;
  - app: the switched-off message is information, not an error; the website follows the worker's `AGENT_OFFBOARDING` on every `app` deploy.
- **Agent TAP:** `AGENT_TAP=true` live, but `TAP_MAIL_SENDER` is not set, so the v1.1 worker mails no pass and the ID team hands it over. Line 5b reads WAIT with the new checker.
- **Mail:** ACS, environment *test*, every mail redirected to the test mailbox.
- **People:** each project has the Gmail tester (Requester, Owner, IdTeam, RoleTeam, PMO, Operator) and the pilot admin (Security). DataMaintainer and ProdApprover not registered yet (output not seen). No people inventory uploaded yet.

## Next steps (in order)
1. **Fresh Cloud Shell** (`docs/V1_1_DEPLOY.md`): clone the branch, then `tools/deploy_free.sh worker $SUB` and `tools/deploy_free.sh app $SUB` (no approval). Check the offboarding settings (read-only):
   `az functionapp config appsettings list -g rg-pmo-review-pilot -n func-pmo-worker-pkt2ofpqmlf3e --query "[?name=='AGENT_OFFBOARDING' || name=='OFFBOARD_SUBSCRIPTIONS' || name=='OFFBOARD_DELETE_USER']" -o table`
   Then `bash tools/verify_features.sh $SUB`. Expected: lines 1, 16, 17 PASS; 5b and 15 WAIT. Log it as live check U.
2. **Graph permissions held** (read-only): `tools/deploy_free.sh grant-graph $SUB` prints them. `User.RevokeSessions.All` and `User.EnableDisableAccount.All` must be listed. If they are missing, the switch-on did not run: dry run again, wait for "approved", then `--approved-by`.
3. **Entra check:** the test account should no longer be in `azr-pes-rg-data-dev-ph-weu-dataengineers`. That confirms F104 (index lag).
4. **Re-test offboarding at least 24 h after the grant** (from about 31 Oct evening).
   - Same request page → *Offboard user* → tick → *Confirm*.
   - Expected: revoke and disable Done, status **Completed**, one mail to the PMO (redirected to the test mailbox).
   - If a removal shows late, the run stays *Executing* for up to ~10 minutes, then completes.
5. **Runbook steps 6–7.**
   - DataMaintainer: the Gmail tester on PEST and VESTACY. Give the full role list, because registering replaces a person's roles.
   - ProdApprover: a third account, to be created in Entra, signed in once, then registered.
   - Then the people inventory upload on the Data uploads page (validate → diff → apply).
6. **Quality before more changes.**
   - Independent re-check (REVIEW.md *Re-check 5*) of the code changed after `35e7a62`:
     - `OffboardingJob` (no-subscription leftover, verify re-check, 403 detail);
     - `deploy_free.sh offboarding`;
     - `verify_features` 5b / 16 / 17;
     - `OffboardingPanel`.
   - Re-run Playwright (`tools/run_local.sh`, then `npm --prefix src/web run e2e`). The last run was on `35e7a62`, and the offboarding panel changed since.

## Open decisions (Gokul; any Azure or permission change: dry run or what-if, then "approved")
- **Option 2, agent TAP mail:** `tools/deploy_free.sh tap $SUB on --sender <shared mailbox>` plus the Exchange RBAC for Applications scope (F81). Until then the ID team issues passes.
- **Azure role removal by the agent** (F5a, P3 §3 D deferred). Two ways:
  - a custom delete-only role (read + delete role assignments) with the Learn-documented users-only delete condition;
  - a condition on RBAC Administrator, proven live first (Learn documents no way to switch off its create right).
- **Go-live** (line 15): real addresses on the Contact inventory page, then `tools/deploy_free.sh environment $SUB prod` (`docs/GO_LIVE.md`).
- **External upload API:** stays off; it needs hosting option B or C (`docs/HOSTING.md`).
- **REVIEW.md open:** Medium 7, 8, 9 (partly), 11, 12; Low 5 (partly), 15, 18, 19.
- **Older items:**
  - a custom ACS mail domain if more than about one request an hour is expected (F70);
  - remove the unused `AuditLog.Read.All`;
  - remove the worker's old Cosmos roles on `handoffs`, `joinerPasses`, `roleMappings`;
  - Phase 2 SAMPLE leftovers in the tenant;
  - ADO, ARM role read and Azure OpenAI stay off;
  - a production import with real scopes, tenant ids and contacts.
- **Dates:** the SWA API moves from .NET 9 to 10 before **10 Nov** (F21); P2 trial ends ~15 Nov (F48).

## Tests at hand-off (`d4070bf`)
- .NET 499/500 (1 opt-in Cosmos emulator skip), incl. 24 F5 offboarding tests and 16 infrastructure tests.
- SPA 24/24.
- Python tool self-tests pass (verify_features, import, register, seed).
- Mutation checks: every fix of 30 Oct evening was caught.
- Playwright 11/11 last on `35e7a62` (not re-run since).

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
