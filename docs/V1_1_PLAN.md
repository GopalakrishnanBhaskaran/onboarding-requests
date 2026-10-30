# v1.1 feature pack — Phase 0 plan (30 Oct 2026)

Binding instructions: `PMO_Access_Portal_v1.1_Feature_Prompt.md` (Section B). This file is the P0 output: what exists, what conflicts, the plan per phase, the data model changes, the questions that block P1, and the manual prerequisites.
Status: **P0 approved 30 Oct 2026 (Gokul: "approved, use your proposals"; Q8: "add the fields to existing testing samples").** P1 and P2 done; **P3 approved 30 Oct 2026 (Gokul: "approved, use your proposals", decisions 16–19)**; P4 in progress (`docs/V1_1_PHASES.md`). Nothing deployed.

## 0. Decisions (30 Oct 2026, Gokul)
Every proposal in sections 2 and 5 is the decision. In short:
1. Offboarding update/delete/revoke calls are approved **in code**: worker only, `GraphOffboardingWriter` / `ArmRoleAssignmentRemover`, switch `AGENT_OFFBOARDING=false`. Granting their permissions is a separate approval (dry run → `--approved-by`), as for every switch.
2. F2/F5 Entra and ARM calls run in the existing worker (no new Azure resource). Offboarding runs add the statuses `Planning` and `Executing` while the worker works; the four contract statuses are unchanged.
3. TAP mail: Graph `sendMail` from one shared mailbox (`TAP_MAIL_SENDER`, set at deploy), `Mail.Send` scoped only through Exchange RBAC for Applications; other mail stays on ACS.
4. The TAP mail is never redirected. In a test environment it goes out only to a test person of the people inventory whose mail matches Entra; otherwise an escalation for the ID team.
5. Today's page becomes **Team contacts**; the I2 upload is the **People inventory**.
6. Upload role: new **DataMaintainer** (legacy `Maintainer` keeps granting nothing).
7. **ProdApprover** is a third, Prod-only approval after Owner and Security for anything above Read; never the requester, the Owner approver or the Security approver.
8. The I1 columns are added to the existing SAMPLE test data (`tools/sample_data/sample-data.json`, test fixtures); an upload leaves Azure roles, red/amber and privileged flags of existing rows unchanged.
9. Levels: I1 enum on upload; job-role rows count as Read only when every Azure role is read-only. Environments: Dev, Test, UAT, PreProd, Prod.
10. One request per resolved group, linked by a `bundleId`.
11. The worker resolves group names (`groupResolutions`) and raises the unresolved-group escalations.
12. `IsProductionTenant` on the project (default false; set per tenant by PMO/import). Tightened in P6 (12a below): unset counts as production.
13. Offboarding removes memberships of every group the worker can change and Azure role assignments in every subscription it can read and delete in; anything else is a leftover (PartiallyCompleted + escalation).
14. PH / ACQ are hidden when the tenant has no such rows.
15. Root cause of the TAP mail failure: from the code, written in `docs/ROOT_CAUSE.md` (P4).

P3 checkpoint (Gokul, 30 Oct 2026: "approved, use your proposals"; `docs/PERMISSIONS.md`, `docs/HOSTING.md`):
16. Hosting **option A**: F2 and F5 run in the existing worker, asynchronously (no new Azure resource, ≈ ₹0). B and C stay options for later, each with its own written approval.
17. Offboarding **app role assignments stay under "Not covered"** (no `Directory.Read.All`); disabling the account stops sign-in to those apps.
18. Offboarding **RBAC reach**: the subscriptions in `OFFBOARD_SUBSCRIPTIONS` (comma-separated ids; empty = every subscription the worker identity can list, F84). At deploy time Reader + the users-only constrained RBAC Administrator (delete) are proposed for the pilot subscription only; each further subscription is a separate approval. An assignment the worker cannot read or delete is a leftover (decision 13).
19. **TAP mail via Graph `sendMail`** from the one mailbox in `TAP_MAIL_SENDER`, `Application Mail.Send` scoped to that mailbox through Exchange RBAC for Applications (never also in Entra, F81). Mailbox name and Exchange admin are deploy-time prerequisites; until set, the agent TAP stays with the ID team (no mail, no pass).

P6 independent review fixes (30 Oct 2026, `REVIEW.md`). They tighten decisions already approved. **Confirmed by Gokul at the P6 checkpoint (30 Oct 2026): 12a kept (unset = production), 13a kept (the other project releases a shared person first); P7 option 1 chosen (`docs/V1_1_DEPLOY.md`).**
- **12a.** `IsProductionTenant` unset counts as production (fails closed): a second person confirms offboarding unless the tenant is marked `false` (`tools/import_workbook.py --production-tenant no`). The SAMPLE tenants state it explicitly (PEST true, the others false).
- **13a.** Offboarding only for the project's own people: the project names the agent's own tenant (`TenantId` = `AGENT_TENANT_ID`; a project with no tenant id cannot offboard), the UPN ends with the project's Domain ID suffix, and it is a non-retired person of the project's people inventory, or the Domain ID of a request of the project that its Owner **and** Security approved (a request alone never counts: any Requester can write one), or an account an earlier run of the project already changed (404 otherwise). Offboarding is tenant-wide, so when another project of the same tenant still holds the person the plan is refused (`409 person_in_other_project`) until that project releases them: its people inventory decides when it lists the person (Active holds, Leaver releases); otherwise an approved request that is open or Completed holds, and its DataMaintainer releases the person by listing them as Leaver (never blocked for good, re-check finding 22). A confirmed run whose switch is turned off half-way keeps what is done, leaves the rest to people and goes through verify (escalation, no mail; finding 23). Checked at plan and at confirm; the worker checks again only before planning (a confirmed run always goes through verify). An account holding an Entra directory role (directly or through a role-assignable group) is left to people: no Entra or Azure write is planned, and if the role appears between plan and confirm the remaining writes are skipped at execute; verify lists it as a leftover.
- **13b.** A plan not confirmed within 24 h is closed (`Failed`, "plan expired", audited) when a new plan is asked for; there is no `Cancelled` status.
- **13c.** A person marked Leaver — or offboarded in the project and not in its people inventory as Active — gets no new or corrected request (`409 person_is_leaver`) until the DataMaintainer adds or marks them Active (a rehire); verify lists the person's requests opened after the plan as leftovers.
- **F3a.** The ProdApprover is never the creator, anyone who submitted or resubmitted a revision, or the person who gets the access. A mapping row with no environment is guarded as Prod whenever the request names an environment. Any environment whose name starts with prod/prd is Prod.
- **F5a (30 Oct 2026, after option 1 was live).** Option 3 **Entra only for now**: the agent revokes sessions, removes the TAP and group memberships and disables the account (Graph `User.RevokeSessions.All`, `User.EnableDisableAccount.All`; the rest is held). No Azure right yet: Learn shows no documented way to switch off the *create* right of Role Based Access Control Administrator (P3's §3 D), so Azure role assignments stay with the role assignment team and appear as a leftover in every run (Partially completed, escalation, no PMO completion mail). **Trigger unchanged:** a PMO or Operator clicks *Offboard user* and confirms; the agent does the rest (R5, CLAUDE.md rule 5). A run that can see no Azure subscription is never Completed.
- **F4a.** Retired rows and rows outside their I1 effective dates fail the review (`MAP_NOT_DEPRECATED` with the reason; a usable row wins over a retired one, so a rename with "Retire missing rows" is one match) and are re-checked at every grant point (approvals, Prod decision, agent).

## 1. What exists today (read on 30 Oct 2026)

| Area | Today (code at `2e512fe`) |
|---|---|
| Hosting | SWA Free + managed Functions (.NET 9, no managed identity, 45 s per request, F2/F3/F85). One Flex Consumption worker (`src/worker`, managed identity, timer every 5 min) makes every Entra/Azure call. |
| Request | One request = one resource + one access level + one environment → one mapping row → one Entra group (`ReviewedScope.MappingRowId`). Form: dropdowns from the tenant's mapping sheet. |
| Mapping sheet | Container `mappingSheet`, one doc per row: `Resource`, `AccessLevel` (job roles such as *Data Engineer*, *Support Reader*), `EntraGroup`, `GroupObjectId`, `Role`, `AzureRoles[]`, `Environment`, flags (red/amber, privileged, sensitive, deprecated), `Version`. Imported from the group classification + response document workbooks (`tools/import_workbook.py`, columns `MI-ResourceName`, `RoleName`, `ResourceName`, group names `azr-<tenant>-rg-data-<env>-<domain>-weu-<role>`). Edited row by row in the app (PMO). |
| "Contact inventory" | Team mail lists per project (Security, ID team, role team, PMO, Operators; semicolon-separated) on the `projects` doc, versioned. **Not** a people directory. |
| Approvals | Named Owner, then Security (not the Owner approver). No third approver. |
| Agent writes | Create only: user, security group, membership, Azure role assignment, one TAP for a user it just created. **No update, no delete** (CLAUDE.md rule 5, SCOPE *Excluded*). Each behind a switch and approval. |
| TAP mail | `WorkflowAgent.SendSignInDetailsAsync` → `IDirectMail` (ACS) → `Recipients.User(r)` = the request's `EnterpriseId`. Masked copy stored in `notifications`. |
| Roles | Requester, Owner, Security, IdTeam, RoleTeam, PMO, Operator; legacy `Reviewer` / `Maintainer` load but grant nothing. |
| Tests | .NET 381 (380 pass, 1 opt-in Cosmos-emulator skip), acceptance AC01–AC27, Playwright 8, SPA 10. |

## 2. Conflicts between Section B and the existing rules or code

Section B says: the rule wins and the conflict is reported. Several of these conflict with **CLAUDE.md hard rules that need Gokul's written approval** (cost, never-create list, the agent's write limits), so I list them and stop rather than overriding them silently.

| # | Section B | Existing rule / code | Proposal |
|---|---|---|---|
| C1 | R8: settings via app settings / **Key Vault references** | CLAUDE.md rule 2: never create Key Vault. SWA managed Functions do not support Key Vault references (F85). | App settings only (as today). No Key Vault in the pilot. |
| C2 | F5 executes revoke sessions, delete TAP, remove memberships, delete role assignments, disable (and optionally delete) the account | CLAUDE.md rule 5 + SCOPE *Excluded*: no update, delete or revoke calls; writes only in `GraphDirectoryWriter` / `ArmRoleAssignmentWriter` | New `GraphOffboardingWriter` + `ArmRoleAssignmentRemover`, worker only, switch `AGENT_OFFBOARDING=false` until approved, like the other switches. **Needs Gokul's written approval** (Q1). |
| C3 | Section 7: maybe a bring-your-own Function App with managed identity | CLAUDE.md rule 2: no further Function Apps. BYO backend needs SWA **Standard** (F86/F87). | Run F2 and F5 Entra/ARM calls in the **existing worker** (it already has a managed identity). The API only records the request; the worker does the reads and writes. Consequence: offboarding *plan* and *execute* are asynchronous (≤ 5 min, or an on-demand worker HTTP trigger). Decision at P3 (HOSTING.md), Q2 below. |
| C4 | F2: mail via **Graph `sendMail` from one mailbox**, permission restricted to that mailbox | Pilot mails through ACS (`AcsEmailSender`); `GraphMailSender` is coded and off; `Mail.Send` needs approval. | Graph `sendMail` for the TAP mail only, `Mail.Send` granted **only through Exchange RBAC for Applications** scoped to the sender mailbox, never in Entra (F81: an Entra grant would be unscoped and union with it). Normal mail stays on ACS. Needs an Exchange admin and approval (Q3). |
| C5 | F2: TAP never stored in Cosmos; audit = userId, sentTo (masked), timestamp, expiry, correlationId | Today a copy of the sign-in mail is stored with TAP and password masked, recipient in clear. | Stricter rule wins: store only the audit event; no mail body copy for the TAP mail; recipient masked. |
| C6 | F2: recipient = EnterpriseEmail from the contact inventory, cross-checked with Entra; never an address from a request body | Recipient is the request's `EnterpriseId` (typed by the requester = request body). In test environments `NOTIFY_REDIRECT_TO` sends the TAP mail to the tester mailbox. | Recipient only from the people inventory + Entra cross-check; the TAP mail is **never redirected**: in a test environment without a matching test person it goes to the ID team as an escalation (Q4). |
| C7 | I2 "Contact inventory" = a people directory (EnterpriseId, EnterpriseEmail, Status …) | "Contact inventory" already names the team mail lists page and API (`projects/{id}/contacts`). | Keep the existing page as **Team contacts**; the new upload is the **People inventory** (container `peopleInventory`). Q5. |
| C8 | F4 role **Maintainer** | `Maintainer` is a legacy v1 role still on old `userAccess` rows; today it grants nothing. Reviving it would silently give upload rights to everyone who held it in v1. | New role **DataMaintainer**, or revive `Maintainer` only after `register_user.py --list` shows who holds it (Q6). |
| C9 | F3: role **ProdApprover** approves Prod escalations | Only Owner and Security approve (CLAUDE.md rule 5, SCOPE "Nobody else approves"). | ProdApprover becomes a third, Prod-only approval **after** Owner and Security, never instead of them; must not be the requester, the Owner approver or the Security approver of the same request (Q7). |
| C10 | I1 mapping sheet columns: TenantId, Domain, ResourceGroupType, Environment, AccessLevel, EntraGroupName, EntraGroupObjectId, Owner, EffectiveFrom, EffectiveTo | The live sheet comes from two workbooks with other columns (no TenantId, Domain, ResourceGroupType, Owner, Effective dates) and carries what I1 lacks (Azure roles, red/amber flags, privileged, to-be-created). Per I1 the import path stops for a file without the columns. | I1 is a new upload format. Upload rows merge into the existing rows by the I1 key; fields I1 does not carry (Azure roles, flags) stay as they are. Need a sample I1 file (prerequisite C1) and Q8. |
| C11 | AccessLevel enum Read / Write / Contributor / Owner / Admin; R4 "above Read" | Live access levels are job roles: Admin, Data Engineer, DevOps, Developer, Power BI Developer, Analyst, Support Reader, Support Writer … | Enforce the I1 enum on upload; rank Read < Write < Contributor < Owner < Admin. For rows that are still job roles, "Read" = the row's Azure roles are all read-only (Reader, Storage Blob Data Reader, Key Vault Reader, Key Vault Secrets User); anything else counts as above Read in Prod (Q9). |
| C12 | Environment enum Dev / Test / UAT / Prod | Projects use Dev / PreProd / Prod; the normaliser does not know UAT. | Accept the I1 enum on upload, add UAT to the normaliser; PreProd stays valid for existing rows (Q9). |
| C13 | F1 multi-select: one submit can resolve to many groups | The whole flow (review, approvals, response document, provisioning, verification) is one request = one group. | The form submits **one request per resolved group**, linked by a `bundleId`; each runs the full audited flow. No change to the approval model (Q10). |
| C14 | F1 canonical group = "the one whose objectId resolves in Entra"; unresolved names → Escalation | The API has no managed identity and cannot read Entra; GET with side effects (escalations) is unsafe. | The worker resolves group names each run and stores the result (`groupResolutions`, with time read); the resolve API reads it. Escalations for unresolved groups are created by the worker (idempotent per tenant + group + mapping version), not by the GET (Q11). |
| C15 | F4 external API: Entra client credentials, app role `Uploads.Write` | SWA puts `/api/*` behind its own cookie sign-in (`allowedRoles: authenticated`); a bearer-only caller does not pass it. Whether SWA forwards an `Authorization` header to managed Functions is **not stated on Learn** (unverified). | Build it now behind `EXTERNAL_UPLOAD_API_ENABLED=false` with JWT validation (issuer, `tid`, `aud`, `roles` contains `Uploads.Write`) and tests with mocked keys. Turning it on needs a reachable host (likely the BYO backend from C3). Decision at P3. |
| C16 | F5 "Prod-tenant offboarding needs a second person" | A project is a tenant; environments are per request; there is no "Prod tenant" flag. `APP_ENVIRONMENT=prod` is the deployment, not the tenant. | New project flag `IsProductionTenant` (Q12). |
| C17 | F5 "remove all group memberships" and "RBAC across subscriptions the app can see" | Tenant separation (rule 10): the agent works only in `AGENT_TENANT_ID`. Its RBAC rights are one resource group, conditioned to allow-listed roles. Dynamic and on-premises-synced groups cannot be changed in the cloud; role-assignable groups need a directory role. | Offboarding only for projects in the agent's tenant; others are "Not covered" with the reason. Anything the worker cannot remove is a leftover → PartiallyCompleted + Escalation (never silent). Scope of RBAC removal = subscriptions where the worker has read + delete rights (Q13). |
| C18 | F5 "close the user's open requests" | No closed/cancelled status exists. | New terminal status **Closed – offboarded**, logged in the trail. |
| C19 | F1 options "PH", "ACQ" | In the live workbooks *ph* is also a domain code (Central hub / platform). | Use I1's `ResourceGroupType` column, not the domain code. Hide PH / ACQ when the tenant has no such rows (edge case "tenant with no PH/ACQ") (Q14). |

## 3. Plan per phase

| Phase | Work | Ends with |
|---|---|---|
| **P1** Data model | Containers and records in section 4; `ProdApprover` (+ maintainer role per Q6); `MaxUploadBytes`, `EXTERNAL_UPLOAD_API_ENABLED`, `AGENT_OFFBOARDING`, `OFFBOARD_DELETE_USER` settings (all off); `InMemoryDocumentStore` + Cosmos store wiring; Bicep containers (code only; deploy needs what-if + approval). Tests: serialization, partition keys, ETag on every new container, role table. | Output contract |
| **P2** F1 + F3 | `ResourceResolver` (core, pure): options, All/untick, normalisation, de-dup by object id / normalised name, highest level wins with "included", near-duplicates (normalised edit distance ≤ 2) → `MappingQualityIssue`, Unresolved, Prod > Read → NeedsHumanApproval. `GET /api/resources/resolve` (exact contract, tenant filtered server-side). Form: `Resource*`, dropdown/checkbox toggle, "Choose…", submit blocked until one is chosen, one request per resolved group. F3: `ProdGuard` checked at every grant point (agent provisioning, agent role assignment, team report verification, upload, external API); Escalation records; ProdApprover decision endpoint (not requester, revision-bound). All F1/F3 edge-case tests by name. | Output contract |
| **P3** Checkpoint | `docs/PERMISSIONS.md` (each Graph/ARM permission, why, narrowest option; from F71–F84) and `docs/HOSTING.md` (worker vs BYO backend vs on-demand worker trigger; cost; alternative). | **STOP** for approval |
| **P4** F2 + F4 | `docs/ROOT_CAUSE.md`; `TapRecipientResolver` (inventory EnterpriseEmail × Entra mail/UPN, refuse empty / guest `#EXT#` / mismatch / AlternateEmail / personal domains / DL → Escalation "TAP recipient mismatch"); one recipient, no CC/BCC, never redirected; audit event only. F4: CSV + XLSX parser (macro-enabled formats refused, first sheet or named), validator, diff, upsert, retire-missing, versions, rollback, ETag; Data uploads page; `POST /api/v1/uploads/{kind}` (flag off) with Idempotency-Key; `docs/EXTERNAL_UPLOAD_API.md`. | Output contract |
| **P5** F5 | `OffboardingService` (plan → confirm → execute → verify → audit), writer adapters with mocked HTTP only, PMO-only notification after a clean verify, "Offboard user" button (PMO/Operator, four eyes for Prod tenants), idempotent steps, double-click safe. | Output contract |
| **P6** Review | Edge-case suite complete; independent review agent (repo + prompt + test results only) → `REVIEW.md`; fix Critical/High; re-check. | **STOP** |
| P7 | Deployment options (only after P6 and approval). | — |

## 4. Data model changes (P1)

All new containers are partitioned by `/projectId` (one project = one tenant), use ETag concurrency and carry `schemaVersion`. Count after P1: 13 existing (8 v2 + 5 legacy v1, still declared) + 8 new = 21 containers, within the 25 that share the free 1000 RU/s database (F90).

| Container | Record | Key fields |
|---|---|---|
| `mappingSheet` (existing) | `MappingRow` + new optional fields | `Domain`, `ResourceGroupType` (Domain/PH/ACQ), `Owner`, `EffectiveFrom`, `EffectiveTo`, `Retired`, `UploadKey` (I1 key, normalised), `MappingVersion`, `SheetRow` |
| `mappingVersions` (new) | `DataVersion` | `id` = `{projectId}:mapping:{n}`, `Version` n, `UploadedBy`, `UploadedAt`, `FileHash` (SHA-256), `FileName`, `Counts {added, updated, unchanged, retired}`, `DiffSummary`, `PreviousVersion`, `RolledBackBy/At`, `ChunkCount` |
| `mappingVersions` (chunks) | `VersionChunk` | before-images of every changed row, ≤ 1.5 MB per item (Cosmos item limit 2 MB, F90); used by rollback |
| `peopleInventory` (new) | `Person` | `id` = normalised EnterpriseId, `EnterpriseId` (UPN), `EnterpriseEmail`, `DisplayName`, `EmployeeId`, `Domain`, `ManagerEnterpriseId`, `Status` (Active/Leaver), `AlternateEmail` (never used for TAP), `Version`, `InventoryVersion` |
| `inventoryVersions` (new) | `DataVersion` + chunks | as `mappingVersions` |
| `escalations` (new) | `Escalation` | `RequestId`, `RequestRevision`, `User`, `Group`, `Level`, `Environment`, `Kind` (ProdWrite / UnresolvedGroup / TapRecipientMismatch / OffboardingLeftover), `Reason`, `RequestedBy`, `CreatedAt`, `Status` (Open/Approved/Rejected), `DecidedBy`, `DecidedAt`, `IdempotencyKey` |
| `mappingQualityIssues` (new) | `MappingQualityIssue` | `VariantName`, `CanonicalName`, `SheetRow`, `Reason`, `MappingVersion`, `Status` (Open/Resolved) |
| `groupResolutions` (new, Q11) | `GroupResolution` | normalised group name → `ObjectId` or none, `ReadAt` (worker) |
| `offboardingRuns` (new) | `OffboardingRun` | the F5 output contract + `RequestedBy`, `ConfirmedBy`, `PlanHash` (execute only the plan that was confirmed), `IdempotencyKey` |
| `auditEvents` (new) | `AuditEvent` (append-only) | `At`, `Actor`, `Action`, `Target`, `CorrelationId`, masked `Details`; TAP sends store only userId, sentTo (masked), timestamp, expiry, correlationId; upload idempotency keys |
| `userAccess` (existing) | `AppRole` | + `ProdApprover`; + maintainer role (Q6) |
| `projects` (existing) | `Project` | + `IsProductionTenant` (Q12) |

## 5. Questions (block P1 until answered)

1. **Offboarding writes (C2).** Do you approve, in writing, adding update/delete/revoke calls for offboarding (revoke sessions, delete TAP, remove memberships, delete role assignments, disable account), worker only, behind `AGENT_OFFBOARDING=false` until a separate permission approval?
2. **Hosting (C3).** OK to plan F2/F5 on the existing worker (no new resources; offboarding plan/execute answer within ~5 min, the API returns the run and the page refreshes)? The contract has no "in progress" status: may I add `Planning` / `Executing`, or must `POST …?mode=plan` block until the plan is ready (not possible on SWA managed Functions without a managed identity)?
3. **TAP mail sender (C4).** Graph `sendMail` from one shared mailbox (name?) with `Mail.Send` scoped only by Exchange RBAC for Applications — or keep ACS for the TAP mail with the new recipient checks?
4. **Test environments (C6).** In test, should the TAP mail go nowhere (escalate to the ID team) unless the recipient is a test person in the inventory whose mail matches Entra? (Today it goes to the tester mailbox.)
5. **Naming (C7).** OK to rename today's page to *Team contacts* and call the I2 upload *People inventory*?
6. **Upload role (C8).** New role `DataMaintainer`, or reuse the legacy `Maintainer` (I would first list who still holds it)?
7. **ProdApprover (C9).** Third approval after Owner and Security for Prod above Read, never replacing them — correct?
8. **Mapping sheet format (C10).** Is the I1 file a new PMO export (please send a SAMPLE), and should an upload leave Azure roles / red-amber flags / privileged flags of existing rows untouched?
9. **Levels and environments (C11, C12).** Map today's job-role levels as proposed (read-only Azure roles = Read), and accept both I1's Dev/Test/UAT/Prod and the existing PreProd?
10. **Multi-select (C13).** One request per resolved group, linked by a bundle id — OK?
11. **Group resolution (C14).** Worker-maintained resolution records read by the resolve API, escalations created by the worker — OK?
12. **Prod tenant (C16).** Add `IsProductionTenant` on the project (PEST? Vestacy?) for the four-eyes offboarding rule?
13. **Offboarding reach (C17).** Remove memberships of *all* groups the worker can change, and RBAC assignments in every subscription it can read? (Today its RBAC rights are one resource group.) Which subscriptions?
14. **PH / ACQ (C19).** Hide PH / ACQ when the tenant has no such rows, or show them disabled?
15. **F2 root cause.** The deployment log records no failed TAP mail. What did you see (no mail, wrong mailbox, guest address)? From the code the likely causes are: the recipient is the requester-typed Enterprise ID with no Entra cross-check; the test redirect sends the pass to the tester mailbox; the pass is for `local@<tenant>.onmicrosoft.com` while the mail goes to a different (possibly personal) domain; `#EXT#` guest forms are normalised for the Owner match only. I confirm in ROOT_CAUSE.md (P4).

## 6. Manual prerequisites (Gokul)

1. SAMPLE I1 mapping sheet and I2 people inventory for one test tenant (no real staff data).
2. Sender mailbox for the TAP mail (if Q3 = Graph), and an Exchange admin to scope `Mail.Send` (RBAC for Applications).
3. ProdApprover users and the PMO contact per domain.
4. Someone who can grant admin consent for the permissions in PERMISSIONS.md (P3).
5. Hosting decision at P3.
6. Written approvals for C2 (and each switch later: dry run → `--approved-by`).
7. (P6) Per tenant: its Entra tenant id on the project (`import_workbook.py --tenant-id`, needed for offboarding) and whether it is a production tenant (`--production-tenant yes|no`; not set = production, a second person confirms offboarding).
