# Next session — handoff (updated 22 Oct 2026, evening: go-live release)

> **30 Oct 2026 — v1.1 feature pack (branch `claude/elegant-darwin-nfot4p`).** P0 approved; P1 (data model) and P2 (F1 Resource* selector, F3 Prod read-only) built and tested; P3 approved (option A, decisions 16–19); P4 done (F2 TAP recipient fix, `docs/ROOT_CAUSE.md`; F4 uploads + external API off, `docs/EXTERNAL_UPLOAD_API.md`); P5 done (F5 offboarding, switched off); P6 done (edge-case suite, independent review `REVIEW.md`: Critical 0, High 0 after two re-checks) — **Goal closed on mocked evidence (Gokul, 30 Oct 2026). P6 approved; P7 option 1 chosen, not yet deployed: follow `docs/V1_1_DEPLOY.md` (what-if → approved → apply → `app` + `worker` → tenant flags → ProdApprover / DataMaintainer → people inventory → `verify_features` line 16)**. After the next `worker` redeploy the agent TAP mails nothing until `tap … --sender` + the Exchange scope are approved (the ID team issues passes meanwhile). Instructions `PMO_Access_Portal_v1.1_Feature_Prompt.md`, decisions `docs/V1_1_PLAN.md`, phase results `docs/V1_1_PHASES.md`. Nothing deployed; the go-live steps below are unchanged.

Read first: `docs/GO_LIVE.md` (steps for 23 Oct), `CLAUDE.md`, `SCOPE.md` (22 Oct go-live additions), `workflows/09-onboarding-flow-v2.md` (section *Notifications and the contact inventory*), last rows of `docs/DEPLOYMENT_LOG.md`.
Branch **`claude/eager-ride-lx6he8`** (on top of `13ca4b9` from `claude/great-goodall-j4ozev`), pushed. No PR.

## State
- **Live end-to-end test: Completed** (Gokul, 22 Oct, in chat; no command output seen). Two findings: the completion mail went to the Owner, and step 7 waited for the joiner's sign-in.
- **This release (code only, not deployed yet):**
  - completion mail to the **requester** (with Security, PMO, ID team, role assignment team), never the Owner; provisioning summaries to the requester and Security;
  - step 7 checks account, membership and roles once and completes (no sign-in, no TAP removal wait; requests already waiting complete on the next run);
  - **Contact inventory** page per tenant (semicolon-separated team lists + escalation managers; PMO and Operators save; versioned; changes mailed to Security and PMO);
  - mail within the ACS limit (Azure-managed domain 10 an hour, F70), most urgent first, throttled mail waits instead of failing, backlog issue; 50-recipient batches; agent creates an account only when its sign-in mail can go out in the same run;
  - bad addresses skipped per mail, guest sign-in names mailed at the mailbox, High issue when an approval or team task reaches nobody;
  - `import_workbook.py --test-contact` keeps contacts saved in the app (`--replace-contacts` to force); `verify_features` lines 6c (contacts), 14 (mail queue), 15 (go-live mail).
- Tests on this branch: .NET 381 (380 pass, 1 opt-in Cosmos-emulator skip), acceptance 27/27, Playwright 8/8, SPA 10/10, Python tool tests pass.

## Next steps
Follow `docs/GO_LIVE.md`: deploy `worker` + `app` (no approval), fill the Contact inventory for PEST and Vestacy, smoke test in test mode, `tools/deploy_free.sh environment $SUB prod`, `verify_features.sh` lines 6c / 14 / 15 PASS, send the go-live mail. Log each step in `DEPLOYMENT_LOG.md`.

## Decisions / checks still open (each Azure change needs approval)
- Mail volume: a verified custom mail domain for ACS (Azure + DNS change, approval) if more than about one request an hour is expected (F70).
- `AuditLog.Read.All` on the worker is unused now; removing it is optional (permission change, approval).
- Re-import (`import_workbook.py … --update`) only if needed. It overwrites row edits made in the app, e.g. the corrected object id of `PH-DATAENGINEERS-DEV` (`cf10a737-…`, the pilot tenant's group).
- Production:
  - import with the real scopes (`--resolve` / `--scope-map`, not `--test-scope`), the real tenant ids and real contacts, no `--test-contact`;
  - `APP_ENVIRONMENT=prod` (the redirect is refused there);
  - provisioning in the PEST or Vestacy tenant itself would need an approved identity there (not built);
  - review which privileged rows the agent should do there.
- Remove the worker's old Cosmos roles on `handoffs`, `joinerPasses`, `roleMappings`.
- Leftovers in the tenant: Phase 2 + SAMPLE tests (access package, auto-assign policy, `SAMPLE-GRP-*`, `sample.joiner*` with P2 licences). Keep or remove, on Gokul's written decision.
- Off until approved: ADO, ARM role read, Azure OpenAI.
- Dates:
  - Phase 2 expiries 29 Oct, 5 Nov, 6 Nov;
  - before **10 Nov**: SWA API .NET 9 → 10 retest (F21);
  - ~15 Nov: P2 trial ends (F48). No longer blocks completion: the sign-in check is gone (22 Oct).

## Reference: import and registration (done on 22 Oct; repeat only after changes)
Upload the two workbooks (Cloud Shell → *Manage files* → *Upload*), then `mv ~/Entra-Group-Classification-*.xlsx ~/Data_Response_document*.xlsx ~/onboarding-requests/.local/`.
```bash
ME=<Gmail tester sign-in name>; ADMIN=<pilot admin sign-in name>
PY=$(tools/cosmos_python.sh); TID=$(az account show --query tenantId -o tsv)
CS=$(az cosmosdb keys list -g rg-pmo-review-pilot -n cosmos-pmo-pilot-pkt2ofpqmlf3e --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
COMMON="--test-scope /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access --domain-suffix @gopalakrishnanbaskarangmail.onmicrosoft.com --test-contact gopalakrishnan.baskaran@gmail.com --register-user $ME=Requester,Owner,IdTeam,RoleTeam,PMO,Operator --register-user $ADMIN=Security --apply"
COSMOS_CONNECTION_STRING="$CS" $PY tools/import_workbook.py --tenant PEST --project PEST --project-name "PEST data platform" \
  --classification .local/Entra-Group-Classification-Vestacy-PEST-2026-09-19.xlsx \
  --response ".local/Data_Response_document_-_PEST_Support_Reader-Writer_Groups_2026-10-15.xlsx" --tenant-id $TID $COMMON
COSMOS_CONNECTION_STRING="$CS" $PY tools/import_workbook.py --tenant Vestacy --project VESTACY --project-name "Vestacy data platform" \
  --classification .local/Entra-Group-Classification-Vestacy-PEST-2026-09-19.xlsx $COMMON
unset CS
```
Register later without re-importing: `COSMOS_CONNECTION_STRING="$CS" $PY tools/register_user.py --project PEST --user <sign-in name> --roles Owner` (`--list`, `--remove`).

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
- Cloud Shell's home folder is reset after a short idle time: re-clone (first block above). Workbooks must be uploaded again only for a re-import.

## Working rules
- Any Azure resource or permission change: show the what-if or dry run and wait for "approved".
  - `provisioning … on` (incl. `--from-sheet`), `tap … on` and `privileged … on` are such changes.
  - No approval needed: `app`, `worker`, `environment`, the imports and the SAMPLE removal.
- The agent never extends its own rights or approves. Teams hand tasks back; the agent re-checks.
- Never paste or log a TAP, password or connection string. Never record personal user ids in docs. Real workbooks stay out of git (`*.xlsx` ignored).
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`. Push to `claude/eager-ride-lx6he8`. No PR unless asked.
