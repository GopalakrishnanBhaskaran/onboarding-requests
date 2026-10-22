# 09 — Onboarding flow v2 (AI review, Owner → Security, ID team, role team, AI verification)

**Purpose:** Run the v2 flow: the AI agent reviews, routes, tracks, verifies and reports; the named Owner and Security approve; the ID team and the role assignment team make the changes. The agent never approves, creates or assigns.

**Supersedes:** `08-entra-handoff.md` (Phase 2/2b/2c: Entra access packages, auto-assign, joiner provisioning, show-once TAP). Built 17 Oct 2026 on branch `claude/charming-goldberg-2f0ejk`; deployed 17 Oct 07:39 UTC (live results in `docs/DEPLOYMENT_LOG.md`).

## Flow (stated to every requester upfront)
1. Requester submits the request
2. AI review (mapping sheet + response document)
3. Owner approval
4. Security approval
5. ID team creates user/group (+ TAP)
6. Role assignment team assigns roles
7. AI verification, notifications, audit

## People and app roles (`userAccess`, per project)
| App role | Person | Can |
|---|---|---|
| Requester | anyone requesting access for a joiner | submit, correct and resubmit a returned request |
| Owner | business owners (only the one **named** on a request decides it) | approve / reject the business need |
| Security | security approvers | approve / reject risk and access level; issues list; reports |
| IdTeam | identity team | pick up and record Stage 4 tasks; mark a mapping-sheet group as created |
| RoleTeam | role assignment team | pick up and record Stage 5 tasks |
| PMO | PMO | edit the mapping sheet; reports (informed only, never approves) |
| Operator | pilot operator | issues, reports, run the agent step now, recover stuck reviews |

v1 roles (Reviewer, Maintainer) grant nothing in v2: re-register users with v2 roles.

Register a person by **sign-in name** (22 Oct 2026; the address they sign in with, matched like the named Owner, so the guest `#EXT#` and `live.com#` forms count) or by the app user id from `/.auth/me`: `tools/register_user.py --project PEST --user jane@contoso.com --roles Requester,Owner` (`--list`, `--remove`), or `--register-user jane@contoso.com=Requester,Owner` on `import_workbook.py`. Roles from both add up. If a person's sign-in name changes, register the new one. An unregistered person sees their sign-in name and user id on the start page.

## Deploy (each step needing approval is marked)
1. `tools/deploy_free.sh params <sub>` (after a Cloud Shell reset), set `enableHandoff=true`, `deployWorker=true` in `.local/params.json` (the worker is the AI agent).
2. `tools/deploy_free.sh whatif <sub> .local/params.json` → review `.local/whatif.txt`. Expected: + containers `mappingSheet`, `responseDocument`, `notifications`, `issues`; worker Cosmos data roles re-scoped to the containers it uses; worker app settings (`WORKFLOW_SCHEDULE`, `NOTIFY_SENDER=Outbox`, `ROLE_READER=None`, SLAs, `TAP_MAX_LIFETIME_MINUTES`); retired `ENTRA_*` settings off. No new resource types; cost unchanged (₹50–300/month from credits). **Wait for "approved".**
3. `tools/deploy_free.sh apply <sub> .local/params.json --approved-by "Gokul <date>"` (deploys app + worker, seeds SAMPLE data).
4. `tools/deploy_free.sh grant-graph <sub>` — Graph **read** roles: User.Read.All, GroupMember.Read.All, UserAuthenticationMethod.Read.All, AuditLog.Read.All (Global Administrator or Privileged Role Administrator). **Security change: approval.**
5. `tools/deploy_free.sh revoke-graph-writes <sub>` (dry run lists the 7 Phase 2b/2c roles) → after approval `… --approved-by "Gokul <date>"`. **Security change: approval.**
6. Seed with the tenant domain so domain-specific IDs match real UPNs: `COSMOS_CONNECTION_STRING=… python3 tools/seed_sample_data.py --domain-suffix @<tenant>.onmicrosoft.com`. Later seeds without the flag (e.g. `deploy_free.sh app`) keep it; `workflow_diag.sh` prints it. A request keeps the domain ID it was submitted with.
7. Each tester signs in once, reads `userId` from `/.auth/me`, then `python3 tools/seed_sample_data.py --register-user <userId> --project SAMPLE-ALPHA --roles <roles>`. Use separate people (or InPrivate windows) for Requester, Owner, Security, IdTeam, RoleTeam: the same person cannot be Owner approver and Security approver of one request.
8. `bash tools/workflow_diag.sh <sub>` — worker running, settings, Graph roles (read-only), containers.

## End-to-end test (live)
1. Requester: New onboarding request → `sample.e2e1`, SAMPLE Databricks Engineering / User, Owner = the Owner tester's sign-in name. Expect **AI Review**, then within ~5 min **Awaiting Owner** (worker finishes the Entra cross-check; group not in Entra → FAIL unless the row is "to be created").
2. Owner: My actions → open → *Approve business need* → **Awaiting Security**. Check a second Owner sees no decision buttons.
3. Security: *Approve risk and access level* → **With ID Team**; ID team notified.
4. ID team (in Entra, not in the app): create the user (if new) and group per the task, add membership, **then** issue a one-time TAP with the shortest lifetime and give it to the user directly. In the app: Pick up task → record user ID, group, checkboxes, TAP issued/expiry/one-time/issued-by → **With Role Team** within ~5 min (Entra verified). A wrong group name → task Returned with the exact difference.
5. Role team (in Azure/Databricks): assign the role from the task. In the app: record exactly what was assigned → **Verifying**.
6. User: first sign-in with the TAP, register a permanent method (Authenticator); ID team deletes the TAP if it is not one-time-consumed. → **Completed** within ~5 min; notifications listed on the request; ADO work item if enabled.
7. Record each result in `docs/DEPLOYMENT_LOG.md` (no user ids, no TAP values).

## v2.1 agent provisioning (optional; security change, approval needed)
Chosen by Gokul on 17 Oct 2026: after both approvals the AI agent (worker, deterministic code) creates the user, group and membership and assigns allow-listed Azure roles; the **TAP stays with the ID team** unless the agent TAP (22 Oct 2026, below) is switched on.
1. Scope: the resource group the agent may assign roles in (recommended: a new, empty `rg-pmo-sample-access`). If it does not exist, the dry run lists its creation (free, empty, same region as the pilot) and the approved run creates it.
2. `tools/deploy_free.sh provisioning <sub> on /subscriptions/<sub>/resourceGroups/<rg> [roleIds]` → dry run lists: Graph `User.Create`, `Group.Create`, `GroupMember.ReadWrite.All` (F56–F58); Role Based Access Control Administrator at the scope with a condition: only the given roles (default Reader `acdd72a7-…`) and only user/group principals (F59–F60); worker + SWA settings. **Wait for "approved".**
3. `… --approved-by "Gokul <date>"`. Then `tools/deploy_free.sh params <sub>` (keeps the settings for later applies). New Graph permissions can take up to ~24 h to reach the worker (managed identity token cache, F61): until then the agent's writes are refused and it retries every run (request shows "waiting for its newly granted permission"; a Warning issue); still refused after 24 h → the team does it.
4. PMO: Mapping sheet → *Azure role the AI agent assigns* → row, role definition ID, scope (inside the allowed scope). Rows without it stay with the role team.
5. Live test: submit → Owner → Security → within 5 min the agent creates user/group/membership (request page shows "Done by the AI agent"); the ID team issues the one-time TAP in Entra and records its metadata only; the agent verifies, assigns the role, verifies → Verifying → user signs in, registers Authenticator → Completed.
   A request already With ID Team when provisioning is switched on (task not yet reported) is provisioned by the agent on the next worker run; its ID team task becomes TAP-only.
Switch off at any time: `tools/deploy_free.sh provisioning <sub> off` (settings off at once; removing the rights needs `--approved-by`).

| Agent leaves it to the team when | Issue |
|---|---|
| Guest user, privileged row, row without an Azure role, domain ID outside the project's current domain (e.g. submitted before the tenant domain was set) | none (normal) |
| Two groups with the name, not a security group, sheet says "exists" but missing | High, Needs developer |
| Write refused (permission): retried every run for 24 h (new permissions reach the worker late, F61) | Warning while waiting; resolved when it works |
| Write still refused after 24 h, 3 failed attempts, role/scope not on the allow-list | High, Needs developer |

## Tenants, environments, mail (21 Oct 2026)
- **One project per tenant.** SAMPLE data: `SAMPLE-PEST` (environments Dev, PreProd, Prod; the agent's tenant in the pilot) and `SAMPLE-VESTACY` (a SAMPLE tenant id, so it shows the hand-off: the agent never reads or writes it). Bind a project to a tenant with `seed_sample_data.py --tenant-id PROJECT=<guid>` (or `=none` for the agent's tenant). The worker's tenant is `AGENT_TENANT_ID` (set by `deploy_free.sh worker` / `environment`).
- **Real workbooks** (Vestacy/PEST group classification, Data Response documents) go in with `tools/import_workbook.py` (one tenant per run, output in `.local/`, `--apply` writes Cosmos). Red rows are never sent for approval; amber caveats are shown to the approvers; groups only in the response document are "to be created"; resource names resolve to Azure scopes with `--scope-map` or `--resolve` (read-only `az`).
- **Azure roles per group.** PMO pastes them on the Mapping sheet page (*Azure roles the AI agent assigns*), one per line `Role name | resource name | Azure scope`. The approvers see the full list on the decision page. After both approvals the agent assigns them only when every role is resolved and allow-listed; otherwise the role team gets the full list and must record exactly those lines.
- **Mail.** `deploy_free.sh environment <sub> test --redirect <tester mail>` (settings only) and `deploy_free.sh email <sub> --redirect <tester mail>` (new resources: dry run → approval). Without mail the notifications stay in the app (status Recorded). Who is mailed: the named Owner and Security (approval requests), Security (alerts, what the agent provisioned/assigned, completion, daily summary), the requester, and the joiner at the Enterprise ID mail (request received, access ready with sign-in name, group and roles; with the agent TAP also the one sign-in details mail). No other mail ever carries a TAP.
- **Why a decision is missing.** The request page tells the signed-in person why they see no decision (not the named Owner, no Owner role yet, gave the Owner approval so Security must be someone else). A guest Owner is matched under the mail, the `#EXT#` UPN or the `live.com#` form.

## Agent TAP, request form, real tenants (22 Oct 2026)
- **Agent TAP** (Gokul's request; security change, approval needed). After `provisioning … on` and mail on:
  1. `tools/deploy_free.sh tap <sub> on` → dry run: checks (provisioning on, mail sender, redirect), the tenant's TAP policy (state, default/max lifetime, one-time only), the Graph permission `UserAuthMethod-TAP.ReadWrite.All` (F66) and the settings `AGENT_TAP=true`, `AGENT_TAP_LIFETIME_MINUTES` (default 480, capped at the policy maximum). **Wait for "approved".**
  2. `… --approved-by "Gokul <date>"`, then `tools/deploy_free.sh app <sub>` (the website's wording follows `AGENT_TAP`).
  3. What the agent does: group first, then the account (random 16-character password, change at first password sign-in), then at once one one-time TAP and **one mail** to the Enterprise ID mail (so the password, held only in memory, goes out in the same run), then the membership: sign-in name, temporary password, TAP and expiry, MFA steps (aka.ms/mysecurityinfo → TAP → add Microsoft Authenticator within 10 minutes → delete the TAP entry → sign in at myapps.microsoft.com with the password, set a new one). The ID step is recorded by the agent (TAP metadata only) and verified on the next run; the ID team has nothing to do. Security, Owner, requester and ID team get "AI agent provisioned … mailed the sign-in details" (no values).
  4. Until the new permission reaches the worker's token (up to ~24 h, F61) the agent creates nothing (so password and pass go out together) and the request shows "waiting for its newly granted permission".
  5. Left to the ID team (TAP task, as before): mail off, guest, existing account (no TAP needed), Entra refused the pass (e.g. lifetime above the policy maximum, TAP method disabled), mail failed (the unseen pass is replaced by the ID team's new one), the TAP permission still missing after 24 h (the agent then provisions without it). Entra not yet able to read the brand-new account: retried next run, and that mail has the pass and steps without the password (set it at aka.ms/sspr after MFA).
  Switch off at once: `tools/deploy_free.sh tap <sub> off`; removing the permission needs `--approved-by`.
- **Agent privileged rows** (Gokul's request; security change, approval needed). Needs provisioning on.
  1. `tools/deploy_free.sh privileged <sub> on` → dry run: provisioning on, the roles on the agent's allow-list (by name), the setting `AGENT_PRIVILEGED=true` (worker + SWA). **Wait for "approved".** Then `… --approved-by "Gokul <date>"`.
  2. For the agent to assign a row's roles too, they must be on the allow-list.
     - `tools/deploy_free.sh provisioning <sub> on <rg scope> --from-sheet PEST` (approval) reads them from the mapping sheet and adds them to the roles already allowed. The dry run lists each role, plus those left to people (never-list, not in the catalogue, outside the scope).
     - Otherwise the agent does the user, group and membership and the role assignment team the roles.
     - Once the roles are allowed, the agent takes back on its own a role task it left "not on the agent's allow-list" (if nobody picked it up), assigns the roles and closes the issue.
     - The agent never extends its own allow-list: that stays an approved command.
  3. What changes: after both approvals the agent treats a row flagged privileged like any other (same reads, re-checks, all-or-nothing roles). A row with Owner, Contributor, User Access Administrator, Role Based Access Control Administrator, Reservations Administrator or Key Vault Data Access Administrator stays with the teams, flag or not (F68). The trail and the summaries say "privileged access". An open task the agent left to the ID or role team only because the row was privileged, and nobody has picked up, goes back to the agent on its next run (`agent-resumed`).
  Switch off at once: `tools/deploy_free.sh privileged <sub> off`.
- **Hand back to the AI agent** (request page, ID or role team). Use it when a task the agent left to your team can now be done by the agent, picked up or not. Examples: privileged rows switched on, row object id corrected, permission arrived. It is not an approval. On its next run the agent checks everything again: both approvals, the row and the never-list. If a reason still holds, it leaves the task again with that reason. The trail records `id-team-handed-back` / `role-team-handed-back`.
- **Request form:** Tenant → Environment → Resource → Access level, each a dropdown from that tenant's mapping sheet (empty sheet = a message, never a text box). Enterprise ID = the joiner's mail address (the sign-in details and notifications go there). *Justification template*: Copy or Insert, then replace every `[ … ]`; the API refuses an unfilled template.
- **Real tenants.** Import each tenant from the shared workbooks (PEST: classification + response document; Vestacy: classification), register the testers, then remove SAMPLE data (commands in `docs/NEXT_SESSION.md`). For a pilot test in the agent's own tenant use `--test-scope <test resource group>` (every Azure role of the workbook lands in that group, the real resource names stay on each line) and `--tenant-id <agent tenant>`; production imports keep the real scopes (`--resolve` / `--scope-map`) and tenant ids.

## Validation
- Status table shows Request ID, requester, stage, waiting on, done, next, time in stage, ADO link.
- Daily summary (Issues and reports) shows wait time per team; issues list shows any SLA breach, mismatch or unauthorized approval.
- `tools/workflow_diag.sh` shows only read Graph roles on the worker after step 5.

## Failure handling
| Symptom | Action |
|---|---|
| Owner sees no decision | Read the blue *Waiting for a decision* box on the request: it names who the request waits for and why this sign-in does not count. Fix the Owner role (`--register-user … --roles Owner`) or ask the requester to correct the Owner. |
| No mail arrives | `workflow_diag.sh`: notifications by status and mail errors. Recorded = mail not switched on (`deploy_free.sh email`); MailForbidden = the sender role has not reached the worker yet (retried 3 times, then an issue). |
| AI Review, waiting on AI agent | Normal for up to 5 min: the website runs the rule checks, the worker runs the Entra check (every 5 min). *Run agent step now* cannot run the Entra check. |
| With ID Team, waiting on AI agent > 10 min (v2.1) | The worker provisions on its next run; Issues and reports shows any `agent-error` / provisioning issue; `workflow_diag.sh`. |
| Stuck in AI Review > 10 min | Issues and reports (or `workflow_diag.sh`): a **Needs developer** issue `agent-error:…` names the error the worker hit; Entra read failure → `DependencyFailure`. No issue at all → the worker is not running the current code: `workflow_diag.sh` shows the last worker deploy; `tools/deploy_free.sh worker <sub>`. |
| Failed-Needs-Dev | Issues and reports → developer issue report (`NeedsDeveloper` issue); fix (redeploy if code), then Operator → *Resume after fix* on the request. |
| Unauthorized approval issue | Security reviews who tried; nothing changed in the request. |
| Task past SLA | Reminder, then escalation to the team manager (project contacts). |
| Mapping sheet changed mid-flow | Agent re-runs the review automatically; new approvals needed only if group or role changed. |
| Joiner got no sign-in details mail (agent TAP) | Request trail: `agent-provisioned` says where it was mailed (test environment: the tester mailbox, check spam). "The agent did not mail the sign-in details: …" = left to the ID team with the reason (Warning issue); the ID team issues a new one-time TAP in Entra (it replaces any earlier one) and records its metadata. |
| The pass expired before the joiner used it | The ID team issues a new one-time TAP in Entra and gives it to the joiner directly (the agent does not resend). |
| Returned: "Entra group … has object id X, but the mapping sheet says Y" | The sheet's object id is from another tenant (e.g. the workbook's real PEST group) or out of date. PMO: Mapping sheet → *Edit* on the row → *Group object id* = this tenant's id (or empty to match by name) → *Save row*; then *Correct and resubmit* the request. A later `import_workbook.py --update` puts the workbook id back. |
| Resource or access level list is empty on the form | The tenant's mapping sheet has no rows for that environment: PMO imports the workbooks (`import_workbook.py … --apply`). |

## Phase 2 leftovers in the tenant (decide, do not auto-delete)
The Phase 2 access package `SAMPLE Data Engineer – Dev`, policy "PMO Review auto-assign", group `SAMPLE-GRP-ALPHA-DEV-ENG`, accounts `sample.joiner*` with P2 licences and the `handoffs` / `joinerPasses` containers are not used by v2. Removing them is a deletion: list them for Gokul and act only on his written decision.
