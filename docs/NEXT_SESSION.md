# Next session — handoff (updated 22 Oct 2026, end of day)

Read first: `CLAUDE.md` (hard rules 4–6, 11 changed on 22 Oct), `SCOPE.md` (22 Oct additions), `workflows/09-onboarding-flow-v2.md` (sections *Agent TAP, request form, real tenants*, *Agent privileged rows*, *Hand back to the AI agent*), last rows of `docs/DEPLOYMENT_LOG.md`.
Branch **`claude/great-goodall-j4ozev`**, latest commit **`4b12afc`**, pushed. No PR.

## Goal still open
Live end-to-end test to **Completed**, logged in `DEPLOYMENT_LOG.md`, on the **real PEST data**. The request in flight is **34456531**: PEST / Dev / Central hub / platform (ph) / **Data Engineer**, a privileged row (Storage Blob Data Contributor, Key Vault Secrets Officer, Reader on `rg-pmo-sample-access`).

## Live state (from Gokul's messages in chat; no command output seen since live check M)
- Request 34456531 passed the AI review, the Owner and Security approvals and the agent's ID step. The agent did the ID step after the privileged rows switch, so `privileged … on` was applied (inferred).
- It now sits **With Role Team**: *"2 of 3 Azure role(s) are not on the agent's allow-list (role or scope): Storage Blob Data Contributor …; Key Vault Secrets Officer …"*.
- So the agent's allow-list is still Reader + Storage Blob Data Reader. The earlier `provisioning … $ROLES --approved-by` did not take effect: not run, or the `ROLES` variable was lost in a Cloud Shell reset.
- Unknown:
  - which commit the worker and the app run (check line 1 of `verify_features`);
  - whether the agent mailed the sign-in details for 34456531 (check the Gmail test mailbox and line 10d).
- Mac route parked:
  - On Gokul's Intel Mac, Homebrew compiled python@3.15 from source and looked frozen: Intel is Tier 3, so no new prebuilt packages (F69).
  - `mac_setup.sh` now installs ready-built pieces only (`a80f895`), but has not been run again.
  - Use **Azure Cloud Shell** (Bash) for now.

## Next steps (Cloud Shell, Bash; this container cannot reach Azure)
```bash
SUB=5b9f9810-0512-49e5-b7f7-0159bc488570; az account set --subscription $SUB
cd ~ && rm -rf onboarding-requests && git clone -q -b claude/great-goodall-j4ozev https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git && cd onboarding-requests
git log --oneline -1                     # 4b12afc or later
tools/deploy_free.sh params $SUB
tools/deploy_free.sh worker $SUB         # no approval (code only)
tools/deploy_free.sh app $SUB            # no approval (code only); ends "Deployed commit … to https://icy-smoke-…"
```
1. **Allow-list from the mapping sheet** (security change):
   ```bash
   tools/deploy_free.sh provisioning $SUB on /subscriptions/$SUB/resourceGroups/rg-pmo-sample-access --from-sheet PEST
   ```
   - The dry run lists each role under *allow* and what stays with people:
     - the never-list (Owner, Contributor, …);
     - roles not in the agent's role catalogue;
     - roles outside the scope.
   - Expect *allow*: Storage Blob Data Contributor, Key Vault Secrets Officer, Reader, Storage Blob Data Reader.
   - Wait for "approved". Then re-run with `--approved-by "Gokul <date>"`. That replaces the constrained RBAC Administrator assignment on `rg-pmo-sample-access` with the longer role list and sets `ROLE_ASSIGN_ALLOWED_ROLES`.
2. **Nothing to click afterwards.** On its next run (every 5 min) the agent takes the role task back on its own (`agent-resumed`), if nobody picked it up. It then assigns the roles, verifies them and closes the High issue.
   - If someone picked it up: request page → **Hand back to the AI agent**.
   - A just-granted Azure right can take minutes; a refused write is retried every run for up to 24 h.
3. **Check:**
   - `bash tools/workflow_diag.sh $SUB`: for each open request, why the agent left it and whether it was picked up.
   - `bash tools/verify_features.sh $SUB | tee .local/verify.txt`: line 5c covers privileged rows.
4. **Finish the test**, as the joiner, from the sign-in details mail in the Gmail test mailbox (InPrivate window):
   1. aka.ms/mysecurityinfo → TAP;
   2. add Microsoft Authenticator within 10 min;
   3. delete the TAP entry;
   4. sign in at myapps.microsoft.com with the temporary password and set a new one.

   The agent then completes the request after the sign-in shows in the logs. If no sign-in mail came, the ID team issued or must issue the TAP; see the request's ID team section.
5. Re-run step 3 until every line is PASS. Log the run in `DEPLOYMENT_LOG.md` (no TAP, no password, no user ids). Commit and push.

## What changed on 22 Oct (all pushed; deploy = `worker` + `app`)
| Commit | Change |
|---|---|
| `16fc4e0` … `83301b4` | Agent TAP mail; dropdown-only request form; real PEST/Vestacy import; SAMPLE removal; registration by sign-in name; importer flags privileged rows with the review's rule; PMO can **Edit** a mapping row (e.g. group object id). |
| `c5870dc` | **Privileged rows by the agent** (`AGENT_PRIVILEGED`, `deploy_free.sh privileged`, approval). Never Owner, Contributor, User Access Administrator, RBAC Administrator, Reservations Administrator or Key Vault Data Access Administrator (`BuiltInRoles.NeverByAgent`, F68), in code and in the RBAC condition. `mac_setup.sh` + `docs/MAC_DEPLOY.md`. |
| `80cb9be`, `a80f895` | `tools/tool_path.sh`: the scripts find Homebrew's `az`, or say how to install it. `mac_setup.sh` installs ready-built pieces only: the `az` cask, Node 22 from nodejs.org, the Mac's python3. |
| `36d2319` | **Hand back to the AI agent** (ID / role team, request page; `POST …/tasks/{team}/handback`). It works whether or not the task was picked up and is not an approval: the agent re-checks everything. `workflow_diag` shows why each task was left. |
| `4b12afc` | `provisioning … --from-sheet <PROJECT>` (`tools/sheet_roles.py`) adds the sheet's roles to the allow-list. The agent takes back a role task left "not on the allow-list" once every approved role is allowed. |

Tests at `4b12afc`: .NET 352 (351 pass, 1 opt-in Cosmos-emulator skip), acceptance 27/27, Playwright 7/7, SPA 8/8, Python tool tests pass.

## Decisions / checks still open (each Azure change needs approval)
- Step 1 above.
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
  - ~15 Nov: P2 trial ends (F48; the final sign-in check needs the sign-in logs).

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
- Commit trailers: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>` + `Claude-Session: …`. Push to `claude/great-goodall-j4ozev`. No PR unless asked.
