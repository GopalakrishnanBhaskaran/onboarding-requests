# Next session — handoff (written 16 Oct 2026, end of day)

Start by reading this file, `CLAUDE.md` (hard rules), `docs/DEPLOYMENT_LOG.md` (last ~15 rows) and `workflows/08-entra-handoff.md`.
Branch `claude/optimistic-rubin-zipq5x`, PR https://github.com/GopalakrishnanBhaskaran/onboarding-requests/pull/1.
Team summary (Claude Docs): https://claude.ai/code/artifact/9ff2a599-a62d-4bac-b7f4-1d4f947a57e8

## Where things stand (all live, all evidenced in DEPLOYMENT_LOG)
- Phase 1 review app on SWA Free + Cosmos free tier: https://icy-smoke-04dcd4300.6.azurestaticapps.net
- Phase 2 / 2b / 2c on: a **second Reviewer's Accept** → worker (Flex, every 5 min) creates the joiner account if missing (`sample.` UPNs only), assigns Entra ID P2, issues a Temporary Access Pass (TAP), files `adminAdd` → Entra adds the group; published mappings get their group/package/policy automatically.
- Live proof 16 Oct: sample.joiner + sample.joiner2 Delivered; sample.joiner3 + sample.joiner1 accounts/licence/TAP created by the app; joiner3 assignment *scheduled* for its start date 17 Oct.
- 432 .NET + 3 web tests pass. Cost ₹0 so far (budget ₹2,000/month).

## Tasks requested by Gokul for 17 Oct (not started — do these, in this order)
1. **TAP not visible in the UI → add an explicit "Show Temporary Access Pass" button.**
   - Today `PassBox` (src/web/src/pages/RequestDetailPage.tsx) renders only when `GET …/handoffs` returns a `passes[]` entry for the latest hand-off.
   - First diagnose (read-only): open `/api/projects/SAMPLE-ALPHA/requests/<id>/handoffs` in the browser — is `passes` present? Was the app redeployed after Phase 2c (apply at 19:14 UTC ran `swa deploy`; check the build has `Show pass (once)`)? Does `joinerPasses` hold a doc for `f717b9b2….r1` (extend `tools/handoff_diag.sh` to list pass docs **without the secret**)?
   - Change: show a button **"Show Temporary Access Pass"** whenever the hand-off has `joinerAccountCreatedByApp && tapIssuedAt`, independent of `passes[]`; it calls `POST …/handoffs/{id}/pass/reveal`; show the server's message when already shown/expired. Keep show-once + ETag server rules. Guardrail test: avoid the words approve/grant access.
2. **Add the joiner to the group right after the second Accept** (do not wait for the reviewed start date; Gokul: "no need to wait").
   - Change `AssignmentOrder.For` (src/core/PmoReview.Core/Handoff/HandoffSync.cs): start = now always; keep end = reviewed end date. TAP start = now too (`JoinerProvisioning`, Provisioning.cs).
   - Update tests `Order_uses_the_reviewed_period_and_never_starts_in_the_past`, `New_joiner_gets_account_licence_and_one_pass_then_the_assignment` (ValidFrom), docs (SCOPE Phase 2c row, workflow 08, team doc section "The flow" step 7).
   - Record the decision in DEPLOYMENT_LOG (scope change: access starts at acceptance, not the start date).
3. **Sign out does not work (Gmail and pilot.admin).**
   - Cause (likely): `staticwebapp.config.json` `/logout` → `/.auth/logout?post_logout_redirect_uri=/`; `/` needs `authenticated`, so the 401 override sends the browser straight to `/.auth/login/aad`, and the Microsoft session signs it back in silently.
   - Fix: add a public `signed-out.html` (route `allowedRoles: ["anonymous"]`, exclude from navigationFallback) with a "Sign in again / use another account" link; `post_logout_redirect_uri=/signed-out.html`; optionally also end the Microsoft session (`https://login.microsoftonline.com/common/oauth2/v2.0/logout`). For switching accounts, try `/.auth/login/aad?prompt=select_account` (UNVERIFIED that SWA passes it through — check docs, record in VERIFIED_FACTS).
   - `InfraConsistencyTests`/guardrail tests may assert the config: update them.
4. Deploy: `tools/deploy_free.sh app <sub>` (UI/API; code only, no Azure resource change) and `tools/deploy_free.sh worker <sub>` (item 2). Then E2E with a new joiner (`sample.joiner4@…`, start today) and log results.

## Checks due
- 17 Oct after 00:00 UTC: joiner3 Delivered + member of SAMPLE-GRP-ALPHA-DEV-ENG; first sign-in with the TAP (valid 17 Oct 00:00–08:00 UTC) at https://myaccess.microsoft.com.
- 17–18 Oct: `tools/cost_check.sh rg-pmo-review-pilot` (first worker cost).
- 29 Oct / 5 Nov / 6 Nov: expiry → app shows *Expired* (joiner2+3 / joiner1 / joiner).
- Before 10 Nov: SWA API .NET 9 → 10 retest (F21). Before ~15 Nov: P2 trial decision.
- Before production: reduce worker Graph privilege (F43), `User.Create` instead of User.ReadWrite.All (F40).

## Environment facts (no secrets)
- Tenant `gopalakrishnanbaskarangmail.onmicrosoft.com`; subscription `5b9f9810-0512-49e5-b7f7-0159bc488570`; RG `rg-pmo-review-pilot`.
- Resources: `swa-pmo-pilot-pkt2ofpqmlf3e` (eastasia, Free), `cosmos-pmo-pilot-pkt2ofpqmlf3e` (free tier, db `pmoreview`, 9 containers incl. `handoffs`, `joinerPasses`), `func-pmo-worker-pkt2ofpqmlf3e` + `asp-pmo-worker-…` (FC1) + `stpmoworkpkt2ofpqmlf3e`.
- Entra: catalog `PMO Pilot SAMPLE` (1f0593d1-c71e-4d17-8068-7afa6bc24ea5); Dev package `SAMPLE Data Engineer – Dev` (4fe9213d-db34-420f-a39a-f70a4bd96cbd) with group SAMPLE-GRP-ALPHA-DEV-ENG; policy "PMO Review auto-assign" (8772cc0d-…); TAP method enabled (All users, ≥ 8 h).
- Worker Graph roles (7): EntitlementManagement.Read.All/ReadWrite.All, User.ReadBasic.All, User.ReadWrite.All, LicenseAssignment.ReadWrite.All, UserAuthenticationMethod.ReadWrite.All, Group.ReadWrite.All.
- People: Gmail account = PMO + Reviewer + Maintainer in the app; pilot.admin = second Reviewer (app) and Entra admin. Use separate InPrivate windows.

## Cloud Shell after a reset (Gmail, Bash, "No storage account required")
```bash
az account set --subscription 5b9f9810-0512-49e5-b7f7-0159bc488570
cd ~ && { [ -d onboarding-requests ] && cd onboarding-requests && git pull -q origin claude/optimistic-rubin-zipq5x; } \
  || { git clone -q -b claude/optimistic-rubin-zipq5x https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests; }
pip install --user -q -r tools/requirements.txt
# entra-package.json is lost on reset; recreate before params:
mkdir -p .local
cat > .local/entra-package.json <<'EOF'
{"catalogId":"1f0593d1-c71e-4d17-8068-7afa6bc24ea5","accessPackageId":"4fe9213d-db34-420f-a39a-f70a4bd96cbd","myAccessLink":"https://myaccess.microsoft.com/@gopalakrishnanbaskarangmail.onmicrosoft.com#/access-packages/4fe9213d-db34-420f-a39a-f70a4bd96cbd"}
EOF
tools/deploy_free.sh params 5b9f9810-0512-49e5-b7f7-0159bc488570 --provision
bash tools/handoff_diag.sh 5b9f9810-0512-49e5-b7f7-0159bc488570
```

## Working rules to keep
- Approval gate: any Azure resource change → `whatif`, show it, wait for "approved". Code-only redeploys (`app`, `worker`) need no approval.
- Never paste or log a TAP, password or connection string; never record personal user ids in docs.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`; push to `claude/optimistic-rubin-zipq5x` (updates PR #1).
