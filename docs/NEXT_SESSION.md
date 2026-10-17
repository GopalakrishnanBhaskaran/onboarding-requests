# Next session — handoff (written 17 Oct 2026)

Start with this file, `CLAUDE.md` (hard rules), `SCOPE.md`, `workflows/09-onboarding-flow-v2.md` and the last rows of `docs/DEPLOYMENT_LOG.md`.
Branch `claude/charming-goldberg-2f0ejk` (v2). The previous branch `claude/optimistic-rubin-zipq5x` / PR https://github.com/GopalakrishnanBhaskaran/onboarding-requests/pull/1 holds Phase 1 + 2/2b/2c (what is live). No PR opened for v2.

## What changed (v2, built and deployed 17 Oct; live E2E pending)
- New flow: requester → AI review (mapping sheet + response document) → **named Owner** → **Security** → **ID team** (user/group + TAP) → **role team** → AI verification, notifications, audit. Statuses: Submitted, AI Review, Returned, Awaiting Owner, Awaiting Security, Rejected, With ID Team, With Role Team, Verifying, Completed, Failed-Needs-Dev.
- The agent **never approves, creates or assigns**. All Graph write code (access packages, `adminAdd`, create user/licence/TAP/group/package) and the `handoffs` / `joinerPasses` flows were removed. Worker Graph = read only (users, groups, `checkMemberGroups`, auth methods, sign-in logs, manager).
- TAP: recorded as metadata only (issued, expiry, one-time, issued-by); the API rejects any pass value; "after all approvals" compared at minute precision.
- Sources of truth in Cosmos: `mappingSheet` (PMO edits in the app), `responseDocument`; a mapping change re-runs the review of in-flight requests.
- App roles now: Requester, Owner, Security, IdTeam, RoleTeam, PMO, Operator (Reviewer/Maintainer grant nothing → re-register users).
- Operator **Resume after fix** for Failed-Needs-Dev (back to the failed stage; a failed review starts a new review).
- Optional, off: Azure OpenAI reviewer/assistant, Graph `sendMail`, ADO work items, ARM role read.
- Evidence (local): 243 .NET tests (+ opt-in `CosmosEmulatorTests`: full flow on the Cosmos emulator next to v1 documents), `docs/PILOT_REPORT.md` 27/27 acceptance (12/12 gates), 6/6 Playwright E2E through the SWA emulator (full flow to Completed, Returned, Owner reject, isolation, mapping sheet, signed-out page), 6 SPA guardrail tests.

## The 17 Oct handoff tasks
1. Show-TAP button — **superseded**: the app never holds a pass in v2; the ID team gives it to the user directly.
2. Group access right after the second Accept — **superseded**: the ID team adds membership after Security approval; the agent verifies it.
3. Sign-out — **done** in code: `/logout` → `/.auth/logout?post_logout_redirect_uri=/signed-out.html` (public page with "sign in again" and Microsoft sign-out links). Confirm live after the `app` redeploy (F54).

## Deploy v2 — DONE 17 Oct 07:39 UTC (approved by Gokul; evidence in DEPLOYMENT_LOG)
Applied; worker on v2 with only the 4 read Graph roles; write roles revoked. Live check A passed; live check B (no New request button) was the missing v2 Requester role — fixed by `--register-user`. Live check C: submit returned 500 because v1 review runs in `reviewRuns` could not be read — **fixed in code** (v1 documents are skipped); needs `tools/deploy_free.sh app` **and** `worker` (code only), then the stuck Submitted request advances on the next sweep. Live check D: `app` redeploy had reset the project domain to `@sample.invalid` and worker errors were invisible — both fixed in code (seed keeps the live domain; agent errors become Needs-developer issues); after redeploy re-seed with `--domain-suffix` once and submit a fresh request. Live check E: request `05616a80` passed AI review (20/20) and the Owner approval → Awaiting Security (stages 1–3 PASS). Live check F: Security approved → With ID Team. Live check G: `app` uploaded f33d36d, then the seed failed (`No module named 'azure'` after the Cloud Shell reset) and the provisioning dry run stopped at the missing `rg-pmo-sample-access` — both fixed in the scripts (SDK installed on demand; the dry run now lists the resource group creation and the approved run creates it); re-run `app`, then the dry run. Live check H: Gokul approved the v2.1 plan (17 Oct); the agent now retries a refused write for 24 h because new permissions reach the worker's managed identity late (F61). Apply: `worker` + `app`, then `provisioning <sub> on …/rg-pmo-sample-access --approved-by "Gokul 17 Oct 2026"`, then `params`; watch `05616a80` with `workflow_diag.sh`. **v2.1 (Gokul's choice): the agent creates user/group/membership and assigns allow-listed Azure roles after both approvals; TAP stays with the ID team — built and tested, not live until the permissions are approved** (`deploy_free.sh provisioning <sub> on <rgScope>` dry run → approval → `--approved-by`; then `app` + `worker` redeploy; PMO sets the Azure role on row A-FIN-VIEW). **Still to do:** (a) open the app → New request shows Enterprise ID + 7-step flow; (b) re-seed with `--domain-suffix @gopalakrishnanbaskarangmail.onmicrosoft.com` so domain IDs match tenant UPNs; (c) register testers with v2 roles (Gmail: Requester, Owner, IdTeam, RoleTeam, PMO, Operator; pilot.admin: Security); (d) live E2E with `SAMPLE Finance Reports` / `Viewer` (group to be created by the ID team) per workflow 09.

Steps as run:
Full steps: `workflows/09-onboarding-flow-v2.md`.
1. `tools/deploy_free.sh params <sub>`; `enableHandoff=true`, `deployWorker=true`.
2. `tools/deploy_free.sh whatif <sub> .local/params.json` → show `.local/whatif.txt` → **wait for "approved"** (expected: 4 new containers, worker Cosmos roles re-scoped, worker settings; no new resource types; cost unchanged).
3. `apply … --approved-by "Gokul <date>"` (deploys app + worker, seeds SAMPLE).
4. `grant-graph` (4 read roles) — **approval**. Then `revoke-graph-writes` dry run → **approval** → `--approved-by`.
5. Seed `--domain-suffix @<tenant>.onmicrosoft.com`; register testers with v2 roles (separate people for Owner and Security).
6. `bash tools/workflow_diag.sh <sub>`; live E2E per workflow 09; log results.
Facts F44–F55 were checked on Microsoft Learn on 17 Oct (`docs/VERIFIED_FACTS.md`). Two differ from the plan: **F49** — Graph lists application permissions for `GET /users/{id}/manager` as *Not supported*, so Owner escalations will go to the project contact (`ownerManagerFallback`; keep it filled in; tested); **F53** — Learn now shows Azure OpenAI on the GA `/openai/v1/` endpoint with audience `https://ai.azure.com/.default`; the code was aligned (default `AZURE_OPENAI_API_VERSION=v1`, tested). A live SAMPLE call remains for when Azure OpenAI is approved (it is off). Also: sign-in log checks need Entra ID **P1/P2** (F48); SAMPLE groups must not use hidden membership (F46).
**Bicep compiled** with Bicep CLI 0.48.1 (exit 0; 2 pre-existing `defaultTtl` warnings; F55). Parameter files match the template. Incremental deployment does **not** delete the worker's old Cosmos role assignments on `handoffs`, `joinerPasses`, `roleMappings`: list them after apply (`workflow_diag.sh` prints them) and remove only with approval.

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
- Worker (v2, live since 17 Oct 07:39 UTC): Graph User.Read.All, GroupMember.Read.All, UserAuthenticationMethod.Read.All, AuditLog.Read.All only; old Cosmos roles on handoffs/joinerPasses/roleMappings still to clean up (approval).

## Cloud Shell after a reset
```bash
az account set --subscription 5b9f9810-0512-49e5-b7f7-0159bc488570
cd ~ && { [ -d onboarding-requests ] && cd onboarding-requests && git fetch -q origin claude/charming-goldberg-2f0ejk && git checkout -q claude/charming-goldberg-2f0ejk && git pull -q; } \
  || { git clone -q -b claude/charming-goldberg-2f0ejk https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests; }
# The Cosmos SDK for the seed and diagnosis is installed on demand (tools/cosmos_python.sh).
tools/deploy_free.sh params 5b9f9810-0512-49e5-b7f7-0159bc488570
bash tools/workflow_diag.sh 5b9f9810-0512-49e5-b7f7-0159bc488570
```

## Working rules to keep
- Any Azure resource or permission change → `whatif`/dry run, show it, wait for "approved". Code-only redeploys (`app`, `worker`) need no approval — but deploying v2 code to the live worker before the what-if/apply would run v2 against containers that do not exist yet: do the approved apply first.
- Never paste or log a TAP, password or connection string; never record personal user ids in docs.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`; push to `claude/charming-goldberg-2f0ejk`; no PR unless asked.
