# Next session — handoff (written 17 Oct 2026)

Start with this file, `CLAUDE.md` (hard rules), `SCOPE.md`, `workflows/09-onboarding-flow-v2.md` and the last rows of `docs/DEPLOYMENT_LOG.md`.
Branch `claude/charming-goldberg-2f0ejk` (v2). The previous branch `claude/optimistic-rubin-zipq5x` / PR https://github.com/GopalakrishnanBhaskaran/onboarding-requests/pull/1 holds Phase 1 + 2/2b/2c (what is live). No PR opened for v2.

## What changed (v2, built 17 Oct, NOT deployed)
- New flow: requester → AI review (mapping sheet + response document) → **named Owner** → **Security** → **ID team** (user/group + TAP) → **role team** → AI verification, notifications, audit. Statuses: Submitted, AI Review, Returned, Awaiting Owner, Awaiting Security, Rejected, With ID Team, With Role Team, Verifying, Completed, Failed-Needs-Dev.
- The agent **never approves, creates or assigns**. All Graph write code (access packages, `adminAdd`, create user/licence/TAP/group/package) and the `handoffs` / `joinerPasses` flows were removed. Worker Graph = read only (users, groups, `checkMemberGroups`, auth methods, sign-in logs, manager).
- TAP: recorded as metadata only (issued, expiry, one-time, issued-by); the API rejects any pass value; "after all approvals" compared at minute precision.
- Sources of truth in Cosmos: `mappingSheet` (PMO edits in the app), `responseDocument`; a mapping change re-runs the review of in-flight requests.
- App roles now: Requester, Owner, Security, IdTeam, RoleTeam, PMO, Operator (Reviewer/Maintainer grant nothing → re-register users).
- Operator **Resume after fix** for Failed-Needs-Dev (back to the failed stage; a failed review starts a new review).
- Optional, off: Azure OpenAI reviewer/assistant, Graph `sendMail`, ADO work items, ARM role read.
- Evidence (local): 234 .NET tests, `docs/PILOT_REPORT.md` 27/27 acceptance (12/12 gates), 6/6 Playwright E2E through the SWA emulator (full flow to Completed, Returned, Owner reject, isolation, mapping sheet, signed-out page), 6 SPA guardrail tests.

## The 17 Oct handoff tasks
1. Show-TAP button — **superseded**: the app never holds a pass in v2; the ID team gives it to the user directly.
2. Group access right after the second Accept — **superseded**: the ID team adds membership after Security approval; the agent verifies it.
3. Sign-out — **done** in code: `/logout` → `/.auth/logout?post_logout_redirect_uri=/signed-out.html` (public page with "sign in again" and Microsoft sign-out links). Confirm live after the `app` redeploy (F54).

## Deploy v2 (each approval is Gokul's)
Full steps: `workflows/09-onboarding-flow-v2.md`.
1. `tools/deploy_free.sh params <sub>`; `enableHandoff=true`, `deployWorker=true`.
2. `tools/deploy_free.sh whatif <sub> .local/params.json` → show `.local/whatif.txt` → **wait for "approved"** (expected: 4 new containers, worker Cosmos roles re-scoped, worker settings; no new resource types; cost unchanged).
3. `apply … --approved-by "Gokul <date>"` (deploys app + worker, seeds SAMPLE).
4. `grant-graph` (4 read roles) — **approval**. Then `revoke-graph-writes` dry run → **approval** → `--approved-by`.
5. Seed `--domain-suffix @<tenant>.onmicrosoft.com`; register testers with v2 roles (separate people for Owner and Security).
6. `bash tools/workflow_diag.sh <sub>`; live E2E per workflow 09; log results.
Before step 4, re-check F44–F54 in `docs/VERIFIED_FACTS.md` on Microsoft Learn (all UNVERIFIED; learn.microsoft.com was blocked in the build environment).
**Bicep was not compiled in the build session** (no Bicep CLI; aka.ms and NuGet blocked). The v2 Bicep diff was reviewed by hand and `InfraConsistencyTests` pass; run `az bicep build --file infra/main.bicep` in Cloud Shell first (the what-if compiles it too) and fix any error before showing the what-if.
Incremental deployment does **not** delete the worker's old Cosmos role assignments on `handoffs`, `joinerPasses`, `roleMappings`: list them after apply (`workflow_diag.sh` prints them) and remove only with approval.

## Decisions for Gokul
- Phase 2 leftovers in the tenant (access package, auto-assign policy, `SAMPLE-GRP-ALPHA-DEV-ENG`, `sample.joiner*` accounts with P2 licences, `handoffs`/`joinerPasses` containers): keep or remove (removal is a deletion — only on his written decision).
- Turn on later (each needs approval): mail (`Mail.Send` + application access policy), ADO (worker identity added to the org), ARM role read (Reader on scopes), Azure OpenAI (new resource, rule 2).
- P2 trial ends ~15 Nov: sign-in log checks (F48) need P1/P2.

## Checks still due
- Cost: `tools/cost_check.sh rg-pmo-review-pilot` (first worker cost).
- 29 Oct / 5 Nov / 6 Nov: Phase 2 assignment expiries (only relevant while Phase 2 is live).
- Before 10 Nov: SWA API .NET 9 → 10 retest (F21).

## Environment facts (no secrets)
- Subscription `5b9f9810-0512-49e5-b7f7-0159bc488570`; RG `rg-pmo-review-pilot`; SWA `swa-pmo-pilot-pkt2ofpqmlf3e` (https://icy-smoke-04dcd4300.6.azurestaticapps.net); Cosmos `cosmos-pmo-pilot-pkt2ofpqmlf3e` (db `pmoreview`); worker `func-pmo-worker-pkt2ofpqmlf3e` (FC1) + storage `stpmoworkpkt2ofpqmlf3e`.
- Worker today (Phase 2c, live): 7 Graph roles incl. write roles (see F43) — to be revoked after approval.

## Cloud Shell after a reset
```bash
az account set --subscription 5b9f9810-0512-49e5-b7f7-0159bc488570
cd ~ && { [ -d onboarding-requests ] && cd onboarding-requests && git fetch -q origin claude/charming-goldberg-2f0ejk && git checkout -q claude/charming-goldberg-2f0ejk && git pull -q; } \
  || { git clone -q -b claude/charming-goldberg-2f0ejk https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests; }
pip install --user -q -r tools/requirements.txt
tools/deploy_free.sh params 5b9f9810-0512-49e5-b7f7-0159bc488570
bash tools/workflow_diag.sh 5b9f9810-0512-49e5-b7f7-0159bc488570
```

## Working rules to keep
- Any Azure resource or permission change → `whatif`/dry run, show it, wait for "approved". Code-only redeploys (`app`, `worker`) need no approval — but deploying v2 code to the live worker before the what-if/apply would run v2 against containers that do not exist yet: do the approved apply first.
- Never paste or log a TAP, password or connection string; never record personal user ids in docs.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`; push to `claude/charming-goldberg-2f0ejk`; no PR unless asked.
