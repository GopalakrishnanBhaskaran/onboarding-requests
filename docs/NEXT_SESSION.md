# Next session — handoff (updated 21 Oct 2026, evening)

Read first: `CLAUDE.md` (hard rules), `SCOPE.md` (21 Oct additions), `workflows/09-onboarding-flow-v2.md` (section *Tenants, environments, mail*), last rows of `docs/DEPLOYMENT_LOG.md`.
Branch **`claude/great-goodall-j4ozev`** (built on `claude/charming-goldberg-2f0ejk` `e606253` + the 21 Oct work, pushed). No PR.

## Goal still open
Live end-to-end test to **Completed**, logged in `DEPLOYMENT_LOG.md`:
- A: request `05616a80` (SAMPLE-ALPHA, `A-FIN-VIEW`, v2.1 agent provisioning);
- B: a new **SAMPLE PEST / Dev / Support Reader** request (tenant project, environment, Azure roles from the response document).

## What changed on 21 Oct (code only, nothing deployed)
- **Owner decision not showing:** the request page now says why (not the named Owner / no Owner role yet / gave the Owner approval). A guest Owner is matched under the mail, the `#EXT#` UPN or `live.com#…`. The decision panel shows user, tenant, environment, group, every Azure role, risk flags and the AI summary. Approving needs an "I have read…" tick. The panel reloads if the review changed.
- **Tenant separation:** one project per tenant. The agent touches Entra/Azure only for projects in `AGENT_TENANT_ID`. SAMPLE data adds `SAMPLE-PEST` (agent's tenant) and `SAMPLE-VESTACY` (SAMPLE tenant id, shows the hand-off).
- **Environments:** Dev / PreProd / Prod on the request and the mapping row. Prod has no groups yet, so a Prod request is returned.
- **Workbooks:** `tools/import_workbook.py` (tested on your two files here; real data is not in git). Red rows are blocked, amber rows show their caveat. A group gets many Azure roles × resources. The agent assigns them all or none, within the allow-list.
- **Mail:** `AcsEmailSender` (Azure Communication Services Email, managed identity). The joiner is mailed at the Enterprise ID (mail address). Security gets approval requests, alerts, what the agent created/assigned, and completion. A test environment sends every mail to one tester address.
- Tests: 311 .NET (310 pass, 1 opt-in emulator skip), acceptance 27/27, Playwright 7/7, SPA 6/6.

## Tomorrow in Cloud Shell (this container cannot reach Azure)
```bash
az account set --subscription 5b9f9810-0512-49e5-b7f7-0159bc488570
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570
cd ~ && { [ -d onboarding-requests ] && cd onboarding-requests && git fetch -q origin claude/great-goodall-j4ozev && git checkout -q claude/great-goodall-j4ozev && git pull -q origin claude/great-goodall-j4ozev; } \
  || { git clone -q -b claude/great-goodall-j4ozev https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests; }
git log --oneline -1
tools/deploy_free.sh params $SUB
```
1. **Code redeploy (no approval):** `tools/deploy_free.sh worker $SUB` then `tools/deploy_free.sh app $SUB`. The worker step also sets `AGENT_TENANT_ID` to this subscription's tenant.
2. **Test environment (settings only, no resource change):**
   `tools/deploy_free.sh environment $SUB test --redirect gopalakrishnan.baskaran@gmail.com`
3. **Seed the tenant projects** (Security / PMO / team contacts = your mailbox; Azure roles in the approved RG; tenant domain):
   ```bash
   PY=$(tools/cosmos_python.sh)
   CS=$(az cosmosdb keys list -g rg-pmo-review-pilot -n cosmos-pmo-pilot-pkt2ofpqmlf3e --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
   COSMOS_CONNECTION_STRING="$CS" $PY tools/seed_sample_data.py --domain-suffix @gopalakrishnanbaskarangmail.onmicrosoft.com \
     --access-scope /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access --test-contact gopalakrishnan.baskaran@gmail.com
   ```
   Register the testers on `SAMPLE-PEST`. Each tester reads `userId` from `/.auth/me`. Use the Gmail tester for Requester, Owner, IdTeam, RoleTeam, PMO and Operator, and the pilot admin for Security:
   `COSMOS_CONNECTION_STRING="$CS" $PY tools/seed_sample_data.py --register-user <userId> --project SAMPLE-PEST --roles Requester,Owner,IdTeam,RoleTeam,PMO,Operator`
   (the same with `--roles Security` for the pilot admin). Then `unset CS`.
4. **Check:** `bash tools/workflow_diag.sh $SUB`. Look for:
   - worker settings `AGENT_PROVISIONING` / `AGENT_ROLE_ASSIGNMENT` true, `APP_ENVIRONMENT=test`, `NOTIFY_REDIRECT_TO`, `AGENT_TENANT_ID`;
   - Graph roles include `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All`;
   - projects `SAMPLE-PEST` (agent tenant, Dev/PreProd/Prod) and `SAMPLE-VESTACY`.

   If the provisioning rights are missing, run the already-approved:
   `tools/deploy_free.sh provisioning $SUB on /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access --approved-by "Gokul 17 Oct 2026"`
5. **Mail (new resources: needs your "approved"):**
   `tools/deploy_free.sh email $SUB --redirect gopalakrishnan.baskaran@gmail.com`
   - The dry run prints the plan and what-if: policy allows 3 ACS types; ACS + Email service with an Azure-managed domain; one role for the worker on that resource; pay per mail (~₹0 at pilot volume).
   - After you approve: `… --approved-by "Gokul <date>"`, then `tools/deploy_free.sh app $SUB`.
   - Until then, notifications stay in the app (status Recorded).
6. **E2E A (`05616a80`):**
   - Mapping sheet → *Azure roles the AI agent assigns* → row `A-FIN-VIEW`. Enter one line: `Reader | rg-pmo-sample-access | /subscriptions/<SUB>/resourceGroups/rg-pmo-sample-access`, then **Save Azure roles**.
   - The review re-runs. Approve as Owner (Gmail tester: tick "I have read", then *Approve business need*), then as Security (pilot admin).
   - Wait one or two worker runs. Expect `agent-provisioned`, or `agent-waiting-permission` for up to 24 h (F61).
   - The ID team issues a one-time TAP (≤60 min) in Entra for `sample.joiner5` and records metadata only. The agent assigns Reader → Verifying.
   - Sign in at mysignins with the TAP and register Authenticator → **Completed**.
7. **E2E B (PEST):**
   - Switch project to *SAMPLE PEST data platform* → New request. Enterprise ID `sample.joiner6@company.example`, Environment **Dev**, Resource *SAMPLE PEST Platform (ph)*, Access level *Support Reader*, Owner = Gmail tester sign-in name.
   - Expect: review Pass → Owner → Security → the agent creates `sample.joiner6@<tenant>`, group `sample-azr-pes-rg-data-dev-ph-weu-support-reader` and the membership (mail "AI agent provisioned…").
   - Then TAP by the ID team → Reader assigned on `rg-pmo-sample-access` (mail "AI agent assigned 1 Azure role") → *access ready* mail → sign-in → Completed.
   - Optional: a Prod request is returned (red row).
8. Log each step in `DEPLOYMENT_LOG.md` (no TAP, no user ids), then commit and push.

## Decisions / checks still open (each Azure change needs approval)
- Mail resources (`deploy_free.sh email`).
- **The role the sending identity needs** (F63): Learn does not name it. The script uses "Communication and Email Service Owner" if the tenant lists it, otherwise Contributor on the ACS resource only. Confirm with the first mail.
- **Real Vestacy / PEST tenants:**
  - The agent works only in its own tenant (the pilot). Provisioning there would need an approved identity in each tenant; not built, your decision.
  - Until then, import their workbooks into their own projects with `tools/import_workbook.py --apply`. The ID team and role team work there; the app reviews, routes, records and verifies against their reports.
- Remove the worker's old Cosmos roles on `handoffs`, `joinerPasses`, `roleMappings`.
- Phase 2 tenant leftovers (access package, auto-assign policy, `SAMPLE-GRP-ALPHA-DEV-ENG`, `sample.joiner*` with P2 licences): keep or remove, on your written decision.
- Off until approved: ADO, ARM role read, Azure OpenAI.
- Dates:
  - Phase 2 expiries 29 Oct, 5 Nov, 6 Nov.
  - Before 10 Nov: SWA API .NET 9 → 10 retest (F21).
  - ~15 Nov: P2 trial ends (F48).

## Environment (no secrets)
- Subscription `5b9f9810-0512-49e5-b7f7-0159bc488570`.
- RG `rg-pmo-review-pilot`.
- SWA `swa-pmo-pilot-pkt2ofpqmlf3e` (https://icy-smoke-04dcd4300.6.azurestaticapps.net).
- Cosmos `cosmos-pmo-pilot-pkt2ofpqmlf3e` (db `pmoreview`).
- Worker `func-pmo-worker-pkt2ofpqmlf3e` (FC1) + storage `stpmoworkpkt2ofpqmlf3e`.
- Access RG `rg-pmo-sample-access`. Project domain suffix `@gopalakrishnanbaskarangmail.onmicrosoft.com`.
- Test mailbox (all mail in the test environment): `gopalakrishnan.baskaran@gmail.com` (your choice, 21 Oct).

## Working rules
- Any Azure resource or permission change: show the what-if or dry run and wait for "approved". The `app`, `worker` and `environment` commands need no approval.
- Never paste or log a TAP, password or connection string. Never record personal user ids in docs. Real workbooks stay out of git (`*.xlsx` ignored).
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`. Push to `claude/great-goodall-j4ozev`. No PR unless asked.
