# Next session — handoff (updated 22 Oct 2026)

Read first: `CLAUDE.md` (hard rules 4–6 changed for the agent TAP), `SCOPE.md` (22 Oct additions), `workflows/09-onboarding-flow-v2.md` (section *Agent TAP, request form, real tenants*), last rows of `docs/DEPLOYMENT_LOG.md`.
Branch **`claude/great-goodall-j4ozev`**, pushed. No PR.

## Goal still open
Live end-to-end test to **Completed**, logged in `DEPLOYMENT_LOG.md`, on the **real PEST tenant data**:
PEST / Dev / *the platform resource whose name ends in (ph)* / *Support Reader* → AI review → Owner → Security → the agent creates the group, the account and the membership, **mails the one-time TAP, the temporary password and the MFA steps**, assigns Storage Blob Data Reader + Reader → the joiner signs in and sets up Authenticator → Completed.

## What changed on 22 Oct (code only, nothing deployed)
- **Agent TAP** (your request). It is off until you approve it with `deploy_free.sh tap`. For an account it has just created, the agent:
  - issues one one-time TAP (8 h by default);
  - sends one mail to the Enterprise ID mail address with: sign-in name, temporary password, TAP and expiry, and the MFA set-up steps.
  - The app keeps only a masked copy (`******`). Nothing else stores either value.
  - If anything fails, the ID team issues the TAP, as before.
- **Request form**:
  - Tenant, Environment, Resource and Access level are dropdowns only.
  - The Enterprise ID must be a mail address.
  - A justification template has *Copy* / *Insert* buttons; a template with unfilled `[ … ]` placeholders is refused.
- **Real tenants**:
  - `import_workbook.py` imports PEST and Vestacy from your two workbooks, with tenant id, domain, test contact, user registration and `--test-scope`.
  - `seed_sample_data.py --remove-sample` deletes all SAMPLE data.
  - `deploy_free.sh app` no longer seeds SAMPLE data.
- `verify_features.sh` now checks:
  - the agent TAP;
  - the PEST and Vestacy tenants;
  - that no SAMPLE data is left;
  - the PEST test row;
  - that the sign-in details were mailed.
- Tests: .NET 332 (331 pass, 1 opt-in skip), acceptance 27/27, Playwright 7/7, SPA 8/8.

## Steps in Cloud Shell (this container cannot reach Azure)
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; az account set --subscription $SUB
cd ~/onboarding-requests && git fetch -q origin claude/great-goodall-j4ozev && git checkout -q claude/great-goodall-j4ozev && git pull -q origin claude/great-goodall-j4ozev
git log --oneline -1
tools/deploy_free.sh params $SUB
```
1. **Code redeploy** (no approval): `tools/deploy_free.sh worker $SUB` then `tools/deploy_free.sh app $SUB`.
2. **Upload the two workbooks.**
   - Cloud Shell → *Manage files* → *Upload*. Then `mv ~/Entra-Group-Classification-*.xlsx ~/Data_Response_document*.xlsx ~/onboarding-requests/.local/`
   - `.local/` and `*.xlsx` are git-ignored.
3. **Your two user ids.** Sign in to the app as the Gmail tester, open `https://icy-smoke-04dcd4300.6.azurestaticapps.net/.auth/me` and copy `userId`. Do the same as the pilot admin.
   ```bash
   ME=<Gmail tester userId>; ADMIN=<pilot admin userId>
   ```
4. **Import PEST and Vestacy** (Cosmos data only, no Azure change). Pilot test mode:
   - `--test-scope`: every Azure role from the response document lands in `rg-pmo-sample-access`; the real resource names stay on each line.
   - `--tenant-id`: your tenant.
   - `--test-contact`: every contact is your Gmail.
   ```bash
   PY=$(tools/cosmos_python.sh); TID=$(az account show --query tenantId -o tsv)
   CS=$(az cosmosdb keys list -g rg-pmo-review-pilot -n cosmos-pmo-pilot-pkt2ofpqmlf3e --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
   COMMON="--test-scope /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access --domain-suffix @gopalakrishnanbaskarangmail.onmicrosoft.com --test-contact gopalakrishnan.baskaran@gmail.com --register-user $ME=Requester,Owner,IdTeam,RoleTeam,PMO,Operator --register-user $ADMIN=Security --apply"
   COSMOS_CONNECTION_STRING="$CS" $PY tools/import_workbook.py --tenant PEST --project PEST --project-name "PEST data platform" \
     --classification .local/Entra-Group-Classification-Vestacy-PEST-2026-09-19.xlsx \
     --response ".local/Data_Response_document_-_PEST_Support_Reader-Writer_Groups_2026-10-15.xlsx" --tenant-id $TID $COMMON
   COSMOS_CONNECTION_STRING="$CS" $PY tools/import_workbook.py --tenant Vestacy --project VESTACY --project-name "Vestacy data platform" \
     --classification .local/Entra-Group-Classification-Vestacy-PEST-2026-09-19.xlsx $COMMON
   ```
   - Expect PEST 43 rows (8 red, 23 amber, 6 to be created) and Vestacy 169 rows.
   - **Vestacy without `--tenant-id`** is treated as your tenant. Its groups do not exist there, so a Vestacy request is returned with "group not in Entra".
   - If you add Vestacy's real tenant id, the agent leaves Vestacy alone and its requests go to the teams. Either way, use PEST for the agent test.
5. **Remove the SAMPLE data** (lists first, then deletes):
   ```bash
   COSMOS_CONNECTION_STRING="$CS" $PY tools/seed_sample_data.py --remove-sample
   COSMOS_CONNECTION_STRING="$CS" $PY tools/seed_sample_data.py --remove-sample --yes
   unset CS
   ```
   - This also deletes the old SAMPLE test requests (`05616a80`, `bdc376a5`) and their issues. Their evidence stays in `DEPLOYMENT_LOG.md`.
   - The Entra leftovers (`sample.joiner*` accounts, the `SAMPLE-GRP-*` groups) are not touched; decide on them separately.
6. **Let the agent assign the PEST row's roles** (security change: needs your "approved").
   - The row needs Reader + Storage Blob Data Reader; today the agent may assign Reader only.
   - Dry run:
     ```bash
     tools/deploy_free.sh provisioning $SUB on /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access acdd72a7-3385-48ef-bd42-f606fba81ae7,2a2b9908-6ea1-4ae2-8e65-a410df84e7d1
     ```
   - It shows that the constrained RBAC Administrator assignment on `rg-pmo-sample-access` is replaced with one that allows both roles.
   - After you approve: add `--approved-by "Gokul <date>"`.
7. **Agent TAP** (security change: needs your "approved"). Dry run:
   ```bash
   tools/deploy_free.sh tap $SUB on
   ```
   - The dry run shows:
     - checks: provisioning on, mail sender AcsEmail, the redirect;
     - **your tenant's TAP policy**: state, maximum lifetime, one-time setting, groups in scope;
     - the plan: Graph `UserAuthMethod-TAP.ReadWrite.All`, `AGENT_TAP=true`, lifetime (capped at the policy maximum).
   - **Risk to accept:** the permission covers every user's TAP in the tenant. The code uses it only for the account it created in the same request, after both approvals.
   - After you approve: add `--approved-by "Gokul <date>"`, then run `tools/deploy_free.sh app $SUB` so the website wording follows.
   - The new permission can take **up to ~24 h** to reach the worker (F61). Until then the agent creates nothing for a new account, and the request says it is waiting for the permission.
8. **Check:** `bash tools/verify_features.sh $SUB | tee .local/verify.txt`. Before the test, every row up to 7 should be PASS.
9. **End-to-end test** (as the Gmail tester):
   - **New request**:
     - Tenant **PEST**, Enterprise ID `joiner01@company.example`, Environment **Dev**;
     - Resource: the platform entry ending in **(ph)**; Access level **Support Reader**;
     - *Insert into justification*, then fill every `[ … ]`;
     - Owner = your Gmail sign-in name.
   - Approve as Owner (tick "I have read…"). Approve as Security (pilot admin).
   - Wait 5–10 min. The request shows `agent-provisioned … mailed the sign-in details`. Your Gmail gets "[TEST test] Your new account for PEST: sign-in details and MFA set-up".
   - Follow that mail in an InPrivate window:
     1. aka.ms/mysecurityinfo → TAP;
     2. add Microsoft Authenticator within 10 min;
     3. delete the TAP entry;
     4. sign in at myapps.microsoft.com with the temporary password and set a new one.
   - Within 10–15 min the agent verifies its ID step and assigns Storage Blob Data Reader + Reader on `rg-pmo-sample-access`. Your Gmail gets an *access ready* mail, then **Completed** after the sign-in shows in the logs.
   - Optional: a Prod request is returned (red row).
10. Re-run step 8 until everything is PASS. Log the steps in `DEPLOYMENT_LOG.md` (no TAP, no password, no user ids). Commit and push.

## Decisions / checks still open (each Azure change needs approval)
- Steps 6 and 7 above.
- Production:
  - import with the real scopes (`--resolve` / `--scope-map`, not `--test-scope`), the real tenant ids and real contacts, no `--test-contact`;
  - `APP_ENVIRONMENT=prod` (the redirect is refused there);
  - provisioning in the PEST or Vestacy tenant itself would need an approved identity there (not built).
- F63 (the role the mail sender needs) is confirmed in practice: mail was delivered on 21 Oct.
- Remove the worker's old Cosmos roles on `handoffs`, `joinerPasses`, `roleMappings`.
- Leftovers in the tenant (Phase 2 + SAMPLE tests: access package, auto-assign policy, `SAMPLE-GRP-*`, `sample.joiner*` with P2 licences): keep or remove, on your written decision.
- Off until approved: ADO, ARM role read, Azure OpenAI.
- Dates:
  - Phase 2 expiries 29 Oct, 5 Nov, 6 Nov;
  - before 10 Nov: SWA API .NET 9 → 10 retest (F21);
  - ~15 Nov: P2 trial ends (F48; sign-in logs for the final check need it).

## Environment (no secrets)
- Subscription `5b9f9810-0512-49e5-b7f7-0159bc488570`.
- RG `rg-pmo-review-pilot`.
- SWA `swa-pmo-pilot-pkt2ofpqmlf3e` (https://icy-smoke-04dcd4300.6.azurestaticapps.net).
- Cosmos `cosmos-pmo-pilot-pkt2ofpqmlf3e` (db `pmoreview`).
- Worker `func-pmo-worker-pkt2ofpqmlf3e` (FC1) + storage `stpmoworkpkt2ofpqmlf3e`.
- Mail: ACS (Azure-managed domain).
- Access RG (test scope) `rg-pmo-sample-access`.
- Tenant domain `@gopalakrishnanbaskarangmail.onmicrosoft.com`.
- Test mailbox (all mail in the test environment): `gopalakrishnan.baskaran@gmail.com`.

## Working rules
- Any Azure resource or permission change: show the what-if or dry run and wait for "approved". The `app`, `worker` and `environment` commands, the imports and the SAMPLE removal need no approval (no Azure change).
- Never paste or log a TAP, password or connection string. Never record personal user ids in docs. Real workbooks stay out of git (`*.xlsx` ignored).
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`. Push to `claude/great-goodall-j4ozev`. No PR unless asked.
