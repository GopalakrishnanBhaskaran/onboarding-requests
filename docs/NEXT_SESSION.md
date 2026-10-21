# Next session — handoff (updated 21 Oct 2026)

Read first: `CLAUDE.md` (hard rules), `SCOPE.md`, `workflows/09-onboarding-flow-v2.md`, last rows of `docs/DEPLOYMENT_LOG.md`.
Branch `claude/charming-goldberg-2f0ejk` (head `b6bbdc0`, all pushed). No PR for v2. Old branch / PR https://github.com/GopalakrishnanBhaskaran/onboarding-requests/pull/1 = Phase 1 + 2/2b/2c.

## Goal still open
Live end-to-end test of v2 (workflow 09) to **Completed**, with each result logged in `DEPLOYMENT_LOG.md`.

## Where it stands
- **Live and passed:** v2 deployed 17 Oct (A–F). Request `05616a80` (SAMPLE `sample.joiner5`, `SAMPLE Finance Reports` / `Viewer`) passed AI review 20/20, Owner and Security approvals, and reached With ID Team.
- **v2.1 approved by Gokul (17 Oct, live check H):** after both approvals the worker creates the user, group and membership and assigns allow-listed Azure roles. The TAP stays manual with the ID team. Approved plan:
  - empty RG `rg-pmo-sample-access`;
  - Graph `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All`;
  - RBAC Administrator on that RG, limited to Reader and User/Group principals;
  - `AGENT_PROVISIONING` / `AGENT_ROLE_ASSIGNMENT` on;
  - cost ₹0.
- **Live check I:** the SWA runs with `AGENT_PROVISIONING=true`. Request `bdc376a5` was re-approved and is "waiting on AI agent", but its domain is `@sample.invalid`. Since `b6bbdc0` the agent does not create accounts outside the project domain; it hands such a request to the ID team (Info). Ignore it for the E2E.
- **Not confirmed (no Cloud Shell output pasted):**
  - `worker` / `app` redeploys of `7e56e94` and `b6bbdc0`;
  - whether `provisioning … on … --approved-by` ran, i.e. whether the Graph roles and RBAC assignment exist.
- **Code since the last live run:**
  - Cosmos SDK installed on demand (`tools/cosmos_python.sh`).
  - The provisioning plan creates the RG.
  - A refused write is retried for 24 h, because new managed-identity permissions arrive late (F61); after that it goes to the team with a High issue.
  - The agent creates accounts only in the project's current domain.
  - Tests: 277 .NET (276 pass, 1 opt-in emulator skip); SPA 6/6; build OK.

## Next steps (user runs in Cloud Shell; this container cannot reach Azure)
1. Run the reset block below, then `tools/deploy_free.sh worker <sub>` and `tools/deploy_free.sh app <sub>` (code only, no approval).
2. In `workflow_diag.sh`, check that the worker settings show `AGENT_PROVISIONING` / `AGENT_ROLE_ASSIGNMENT` true and that the Graph roles include the 3 write roles. If either is missing, run (already approved):
   `tools/deploy_free.sh provisioning <sub> on /subscriptions/<sub>/resourceGroups/rg-pmo-sample-access --approved-by "Gokul 17 Oct 2026"`
3. PMO: open the mapping sheet and use the **"Azure role the AI agent assigns"** form (not "Add a row") on row `A-FIN-VIEW`. Set role Reader `acdd72a7-3385-48ef-bd42-f606fba81ae7` at scope `rg-pmo-sample-access`, then **Save Azure role**.
4. The mapping change re-runs the review, so re-approve `05616a80`: first **Owner** (Gmail tester), then **Security** (pilot admin).
5. Wait one or two worker runs (5 min each), then run the diagnosis. Expect `agent-provisioned`, or `agent-waiting-permission` for up to 24 h (F61).
6. The ID team issues a one-time TAP (≤60 min) in Entra for `sample.joiner5` and records metadata only.
7. The agent verifies the account, assigns Reader to the group and checks it → Verifying.
8. The user signs in at mysignins with the TAP and registers Authenticator → **Completed**.
9. Log each step in `DEPLOYMENT_LOG.md` (no TAP, no user ids), then commit and push.

## Cloud Shell after a reset
```bash
az account set --subscription 5b9f9810-0512-49e5-b7f7-0159bc488570
cd ~ && { [ -d onboarding-requests ] && cd onboarding-requests && git pull -q origin claude/charming-goldberg-2f0ejk; } \
  || { git clone -q -b claude/charming-goldberg-2f0ejk https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests; }
git log --oneline -1
tools/deploy_free.sh params 5b9f9810-0512-49e5-b7f7-0159bc488570
bash tools/workflow_diag.sh 5b9f9810-0512-49e5-b7f7-0159bc488570
```

## Decisions / checks still open (each Azure change needs approval)
- Remove the worker's old Cosmos roles on `handoffs`, `joinerPasses`, `roleMappings`.
- Phase 2 tenant leftovers: access package, auto-assign policy, `SAMPLE-GRP-ALPHA-DEV-ENG`, `sample.joiner*` accounts with P2 licences, the `handoffs` / `joinerPasses` containers. Keep or remove; removal only on Gokul's written decision.
- Off until approved: mail, ADO, ARM role read, Azure OpenAI.
- Run `tools/cost_check.sh rg-pmo-review-pilot` (first worker cost).
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
- Project domain suffix `@gopalakrishnanbaskarangmail.onmicrosoft.com`.

## Working rules
- Any Azure resource or permission change: show the what-if or dry run and wait for "approved". `app` and `worker` redeploys need no approval.
- Never paste or log a TAP, password or connection string. Never record personal user ids in docs.
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`. Push to `claude/charming-goldberg-2f0ejk`. No PR unless asked.
