# v1.1 — phase results

Each phase ends with the output contract of `PMO_Access_Portal_v1.1_Feature_Prompt.md` section 9. Test counts are from runs in the build container (no Azure).

## Phase 0 result
Status: Done (approved by Gokul on 30 Oct 2026: "approved, use your proposals"; Q8: "add the fields to existing testing samples")
Changed files: `docs/V1_1_PLAN.md` — plan, conflicts, questions; `docs/VERIFIED_FACTS.md` — F71–F92; `SCOPE.md` — v1.1 listed
Tests: 380/381 (1 opt-in Cosmos emulator skip)
Verified facts added: 22 (see VERIFIED_FACTS.md)
Open questions: None (answered: decisions in `docs/V1_1_PLAN.md` section 0)
Risks: see `docs/V1_1_PLAN.md`
Next step: P1 data model

## Phase 1 result
Status: Done
Changed files:
- `src/core/PmoReview.Core/Domain/Governance.cs` — new records: DataVersion (upload versions + before-image parts), Person, Escalation, MappingQualityIssue, GroupResolution, OffboardingRun, AuditEvent
- `src/core/PmoReview.Core/Domain/Documents.cs` — MappingRow I1 columns (TenantId, Domain, ResourceGroupType, Owner, EffectiveFrom/To, Retired, MappingVersion, SheetRow); Project.IsProductionTenant
- `src/core/PmoReview.Core/Domain/Enums.cs` — roles ProdApprover, DataMaintainer; ResourceGroupTypes; AccessLevels (Read < Write < Contributor < Owner < Admin, read-only role ids)
- `src/core/PmoReview.Core/Domain/Tenants.cs` — UAT environment
- `src/core/PmoReview.Core/Security/Access.cs` — permissions for escalations, uploads, people inventory, offboarding; legacy Maintainer still grants nothing
- `src/core/PmoReview.Core/Storage/DataStores.cs`, `src/adapters/.../Storage/*` — eight stores (in memory and Cosmos), container schema
- `src/core/PmoReview.Core/Workflow/Notifier.cs`, `src/adapters/PmoReview.Adapters/PilotApp.cs` — settings, all off: AGENT_OFFBOARDING, OFFBOARD_DELETE_USER, EXTERNAL_UPLOAD_API_ENABLED; TAP_MAIL_SENDER, MAX_UPLOAD_BYTES (≤ 25 MB), EXTERNAL_UPLOAD_AUDIENCE
- `infra/modules/pilot.bicep`, `infra/modules/worker.bicep` — eight containers; worker Cosmos roles on the six it writes (code only; deploying needs what-if + approval)
- `tools/sample_data/sample-data.json`, `tools/seed_sample_data.py` — I1 columns on every SAMPLE mapping row (Q8), SAMPLE people inventory, ProdApprover and DataMaintainer testers
- `tests/PmoReview.Tests/Unit/DataModelV11Tests.cs` — 9 tests; `InfraConsistencyTests` — worker containers
Tests: 389/390 (1 opt-in Cosmos emulator skip); SPA 10/10
Verified facts added: 0
Open questions: None
Risks:
1. `bicep build` was not run here (the Bicep binary could not be downloaded); the container lists are checked by `InfraConsistencyTests` only.
Next step: P2 F1 + F3

## Phase 2 result
Status: Done
Changed files:
- `src/core/PmoReview.Core/Resources/ResourceResolver.cs` — F1 options, All, normalisation (incl. full-width fold: the apps run with invariant globalization), de-duplication, highest level, near-duplicates, Unresolved, Prod status
- `src/core/PmoReview.Core/Resources/ProdGuard.cs` — F3: above Read in Prod from level, every Azure role and privilege; escalation id and scope fingerprint; MayFulfil
- `src/core/PmoReview.Core/Services/ResourceService.cs` — options/resolve per tenant, worker group lookups, mapping version
- `src/core/PmoReview.Core/Workflow/RequestBundleService.cs` — one request per ticked group (BundleId), Unresolved never requested
- `src/core/PmoReview.Core/Workflow/GroupResolutionJob.cs` — worker: Entra read of group names, UnresolvedGroup escalations, near-duplicate quality issues
- `src/core/PmoReview.Core/Workflow/WorkflowAgent.Prod.cs`, `WorkflowAgent.cs` — AwaitingProdApproval step, guard before every grant, shared hand-over to the ID team
- `src/core/PmoReview.Core/Workflow/WorkflowService.Prod.cs`, `WorkflowService.cs` — Security approval gate, ProdApprover decision (four eyes, revision + scope), team reports refused without the approval
- `src/core/PmoReview.Core/Workflow/WorkflowState.cs`, `Records.cs`, `Messages.cs`, `Services/ContactInventoryService.cs`, `Domain/Documents.cs` — status AwaitingProdApproval, stage Prod, waiting on ProdApprover, issue ProdWriteBlocked, ProdApprovers mail list, BundleId
- `src/core/PmoReview.Core/Http/ApiHost.cs`, `Endpoints.cs`, `src/api/.../ApiFunction.cs`, `tools/local-api/Program.cs` — query strings; routes resources/options, resources/resolve, requests/bundle, escalations, escalations/{id}/decision
- `src/worker/PmoReview.Worker/Program.cs` — group resolution job in the worker run
- `src/web/src/lib/resources.ts`, `pages/RequestFormPage.tsx`, `pages/EscalationsPage.tsx`, `components/ApprovalPanel.tsx`, `pages/ContactsPage.tsx`, `App.tsx`, `lib/*` — Resource* selector (dropdown/checkboxes, All, "Choose…", resolved list, submit blocked), Escalations page with the ProdApprover decision, Team contacts (ProdApprovers list)
- `tests/PmoReview.Tests/Unit/F1ResourceSelectorTests.cs` (22), `F3ProdGuardTests.cs` (14), `src/web/src/resources.test.ts` (6), `src/web/e2e/two-projects.spec.ts` (new form + 1 new browser test)
Tests: .NET 425/426 (1 opt-in Cosmos emulator skip); SPA 16/16; Playwright 9/9 (local emulator); Python tool tests pass. Mutation checks: removing the agent's Prod guard fails 2 F3 tests; removing the Security-stage gate fails 1 (the agent's guard still blocks the rest: two layers).
Verified facts added: 0
Open questions: None
Risks:
1. F3 tests "via upload" and "via external API" come with F4 (P4); P2 covers the UI, the API with a forged body, a Read-labelled row that carries a writing role, forged state at both granting stages, approver = requester and stale revisions.
2. A mapping group still "to be created" is Unresolved in the selector (F1: never granted): the ID team creates it and the worker resolves it before it can be requested through the selector. The older single-request API stays for compatibility (resubmit, tests).
3. Near-duplicates use edit distance ≤ 2 as specified; short domain codes can collide (e.g. "ph"/"hr"). When both names resolve to different Entra groups, both stay visible with a note instead of hiding one.
Next step: P3 checkpoint (PERMISSIONS.md, HOSTING.md) — STOP

## Phase 3 result
Status: Done (approved by Gokul on 30 Oct 2026: "approved, use your proposals" — answers to 1–4 are decisions 16–19 in `docs/V1_1_PLAN.md`)
Changed files:
- `docs/PERMISSIONS.md` — every Graph / ARM / Exchange / Entra app-role permission for F1–F5, why, narrowest option, held or new, what to approve at deploy time
- `docs/HOSTING.md` — no new Function App needed: F2/F5 run in the existing worker (managed identity), asynchronously; options A (recommended, ≈ ₹0), B (SWA Standard + worker linked as API backend), C (actual-tenant App Service); cost and rollback per option
- `docs/VERIFIED_FACTS.md` — F93 (Flex Consumption billing), F94 (direct group members, advanced query)
Tests: .NET 425/426 (1 opt-in Cosmos emulator skip); SPA 16/16; Playwright 9/9 — unchanged since P2 (documents only)
Verified facts added: 2 (see VERIFIED_FACTS.md)
Open questions:
1. Hosting: option A (existing worker, asynchronous, ≈ ₹0) — approve? (B and C need written approval of a plan change / new resources.)
2. Offboarding app role assignments: leave them under "Not covered" (no `Directory.Read.All`), or grant `Directory.Read.All` so the plan lists them?
3. Offboarding RBAC reach: which subscriptions get Reader + the users-only constrained RBAC Administrator (delete)?
4. TAP mail: confirm Graph `sendMail` with the Exchange-scoped `Application Mail.Send` (needs the sender mailbox name and an Exchange admin), or keep ACS for the TAP mail.
Risks:
1. `User.RevokeSessions.All`, `User.EnableDisableAccount.All` and `GroupMember.ReadWrite.All` are tenant-wide; only the code limits them to the confirmed offboarding user (and the switch keeps them unused until approved).
2. TAP delete: Learn lists `UserAuthenticationMethod.ReadWrite.All` as least privileged; the plan reuses the narrower-in-effect `UserAuthMethod-TAP.ReadWrite.All` (listed as sufficient) — prove with the first dry run.
3. Which subscriptions the worker can list (F84) and the member filter (F94) are to be proven live before F5 relies on them; the code falls back safely (leftover → PartiallyCompleted + escalation).
4. SWA Standard price is not on Learn (F87): check the pricing calculator before choosing B.
Next step: Gokul approves P3 (answers 1–4) → P4 (F2 + F4).

## Phase 4 result
Status: Done
Changed files:
- `docs/ROOT_CAUSE.md` — why the TAP mail did not go only to the enterprise ID (typed Enterprise ID, other domain, test redirect, guest forms; code at `2e512fe`)
- `src/core/PmoReview.Core/Workflow/TapRecipient.cs` — F2: people inventory EnterpriseEmail = Entra mail or UPN; refuses empty, #EXT#/guest, personal domain, manager, AlternateEmail, leaver, mismatch
- `src/core/PmoReview.Core/Workflow/WorkflowAgent.Provisioning.cs`, `WorkflowAgent.cs`, `WorkflowState.cs`, `Outbound.cs` — check before the pass exists, TapRecipientMismatch escalation, one recipient, never redirected, no stored copy, audit event (user id, masked recipient, time, expiry, correlation id); ACS budget no longer gates the TAP mail
- `src/core/PmoReview.Core/Directory/DirectoryContracts.cs`, `src/adapters/.../Entra/GraphDirectoryReader.cs`, `GraphDirectoryWriter.cs`, `Outbound/GraphMailSender.cs`, `src/worker/.../Program.cs` — Entra `mail`; TAP mail through Graph from `TAP_MAIL_SENDER` only
- `src/core/PmoReview.Core/Uploads/*` — F4: `TabularFile` (CSV/XLSX, macro and zip-bomb checks), `UploadSchema` (I1/I2), `UploadService` (diff, upsert, versions, rollback, head claim, undo, idempotency), `UploadToken` (Entra v2.0 app token), `Multipart`
- `src/core/PmoReview.Core/Http/ApiHost.cs`, `UploadEndpoints.cs`, `Security/Access.cs`, `Domain/Governance.cs`, `src/api/.../ApiFunction.cs`, `tools/local-api/Program.cs`, `src/adapters/.../PilotApp.cs`, `Uploads/OpenIdSigningKeys.cs` — upload routes, per-route body limit, raw bodies, app-token route (404 while off), signing keys from Entra's public metadata
- `src/web/src/pages/DataUploadsPage.tsx`, `lib/uploads.ts`, `lib/api.ts`, `lib/types.ts`, `lib/router.ts`, `App.tsx`, `uploads.test.ts`, `e2e/two-projects.spec.ts` — Data uploads page (two buttons, validation errors, diff, apply, versions, rollback), nav for DataMaintainer and PMO
- `docs/EXTERNAL_UPLOAD_API.md` — contract, auth, curl and PowerShell examples (placeholders), how to switch on
- `tools/deploy_free.sh` (`tap … --sender`, Exchange scope commands), `tools/verify_features.py`, `tools/run_local.sh` (SAMPLE agent tenant id), `CLAUDE.md`, `SCOPE.md`, `workflows/09-onboarding-flow-v2.md`, `docs/ARCHITECTURE.md`, `docs/PERMISSIONS.md`, `docs/HOSTING.md`, `docs/V1_1_PLAN.md` (decisions 16–19)
- Tests: `F2TapRecipientTests.cs` (12), `F4UploadTests.cs` (19), `F3UploadPathTests.cs` (2), `AgentTapTests.cs` (updated), `InfraConsistencyTests.cs`, `Support/UploadFiles.cs`, `Support/CapturingLogger.cs`
Tests: .NET 458/459 (1 opt-in Cosmos emulator skip); SPA 20/20; Playwright 10/10 (local SWA emulator); Python tool tests pass; seed dry-run OK. Mutation checks: the old recipient (typed Enterprise ID) → 3 F2 tests fail; the tenant check removed → `F4_Wrong_tenant_rows_block_the_whole_file` fails; the idempotency replay removed → `F4_Replayed_idempotency_key…` fails.
Verified facts added: 7 (F95–F101, see VERIFIED_FACTS.md)
Open questions: None
Risks:
1. F2: a new cloud-only account has no Entra `mail`, so the check passes only when the inventory's EnterpriseEmail is the account's own UPN. A person whose working mailbox is elsewhere gets the *TAP recipient mismatch* escalation and the ID team hands over the pass (safe default).
2. Live change after the next `worker` redeploy: the agent TAP mail moves from ACS to Graph. Until `TAP_MAIL_SENDER`, the Exchange scope (F81) and people inventory entries exist, the agent creates no pass and the ID team issues every one.
3. Rollback retires the rows a version added (the store has no delete); a later upload with them makes them active again (Updated).
4. The near-duplicate check of an upload runs inside the file, per environment; near-duplicates against rows already on the sheet are caught by F1 at read time and by the worker's mapping quality issues.
5. The external API stays off: whether a bearer token reaches SWA managed Functions is not on Learn (open item); it needs option B or C to be reachable.
6. `bicep build` still not run here (no Bicep change in P4).
Next step: P5 F5 offboarding (plan → confirm → execute → verify → audit, mocked Graph/ARM).

## Phase 5 result
Status: Done
Changed files:
- `src/core/PmoReview.Core/Offboarding/OffboardingService.cs` — F5 API side: plan request (202 `Planning`), confirm (four eyes in a production tenant, `AGENT_OFFBOARDING`, 24 h plan age), idempotent run ids (double click), the exact output contract
- `src/core/PmoReview.Core/Offboarding/OffboardingJob.cs` — worker: plan from reads, execute only the confirmed plan (`PlanHash`) in order, read before every write, progress saved per step, 3 retries for transient failures, verify, PMO-only mail after a clean verify, leftovers → PartiallyCompleted + escalation, audit events
- `src/core/PmoReview.Core/Directory/OffboardingContracts.cs` — `IOffboardingReader`, `IOffboardingWriter`, `IRoleAssignmentLister`, `IRoleAssignmentRemover`
- `src/adapters/.../Entra/GraphOffboardingWriter.cs`, `Rbac/ArmRoleAssignmentRemover.cs` (writes, worker only, switch on); `Entra/GraphDirectoryReader.cs`, `Rbac/ArmRoleAssignmentReader.cs` (reads)
- `src/core/PmoReview.Core/Http/OffboardingEndpoints.cs`, `ApiHost.cs` (`{name:upn}` route segment), `Workflow/WorkflowState.cs` (status `Closed`), `Records.cs` (`OffboardingCompleted`), `Notifier.cs` (`OFFBOARD_SUBSCRIPTIONS`, plan validity), `Outbound.cs`, `Domain/Governance.cs`, `src/adapters/.../PilotApp.cs`, `src/worker/.../Program.cs`, `infra/main.bicep`, `infra/modules/worker.bicep` (switches default off)
- `src/web/src/components/OffboardingPanel.tsx`, `lib/offboarding.ts`, `offboarding.test.ts`, `pages/RequestDetailPage.tsx`, `lib/api.ts`, `lib/types.ts`, `lib/format.ts`, `e2e/two-projects.spec.ts` — "Offboard user" on the request page
- Tests: `F5OffboardingTests.cs` (13, every F5 edge case by name), `Contract/OffboardingAdapterTests.cs` (5, mocked HTTP), `InfraConsistencyTests`, `AdapterContractTests`, `AcceptanceTests` AC23 (guards updated to the approved F5 writers), `Support/Fakes.cs`, `Support/TestApp.cs`
- `docs/VERIFIED_FACTS.md` (F102), `CLAUDE.md`, `SCOPE.md`, `docs/ARCHITECTURE.md`, `docs/PERMISSIONS.md`, `workflows/09-onboarding-flow-v2.md`
Tests: .NET 476/477 (1 opt-in Cosmos emulator skip); SPA 23/23; Playwright 11/11 (local SWA emulator); Python tool tests pass. Mutation checks: a completion mail despite leftovers → 2 F5 tests fail; the four-eyes check removed → 1 fails; the read before a membership removal removed → 1 fails (test added after this check first survived).
Verified facts added: 1 (F102, see VERIFIED_FACTS.md)
Open questions: None
Risks:
1. Graph and ARM calls are mocked only (R9). Which subscriptions the worker can list (F84), the direct-member advanced query (F94) and the TAP delete with `UserAuthMethod-TAP.ReadWrite.All` must be proven with the first approved dry run.
2. Any membership in a dynamic group makes the run PartiallyCompleted (members of rule-based groups cannot be removed, F102): correct, but expect escalations for tenant-wide dynamic groups.
3. `revokeSignInSessions` takes minutes to apply (F71); verify cannot read sessions, so it trusts the 2xx.
4. Plan and execute are asynchronous (decision 2): the API answers `Planning` / `Executing`; the page refreshes until the worker's next run (≤ 5 minutes).
5. In a test environment the PMO completion mail is redirected like every other mail (`NOTIFY_REDIRECT_TO`); only the TAP mail is never redirected.
6. `bicep build` not run here (Bicep parameters added; checked by `InfraConsistencyTests` only).
Next step: P6 — edge-case suite complete, independent review agent → REVIEW.md (STOP).

## Phase 6 result
Status: Done — waiting for Gokul's approval at the P6 checkpoint (STOP). Independent review: Critical 0, High 0 (deployment gate met, `REVIEW.md` re-checks 2–4; last re-check on `35e7a62`).
Changed files:
- `docs/V1_1_EDGE_CASES.md` — every B.8 edge case and every review finding mapped to a named test; `docs/V1_1_TEST_RESULTS.md` — per-test results and 22 mutation checks
- `REVIEW.md` — independent review of `c817105` (1 Critical, 5 High, 6 Medium, 7 Low), re-check of `5db8b72` (High 1 left, findings 20–21 new), re-check 2 of `47d66fe` (Critical 0, High 0, findings 22–23 new); written by the review agent only
- `src/core/PmoReview.Core/Offboarding/OffboardingService.cs`, `OffboardingJob.cs` — offboarding only for the project's own people in a project that names the agent's tenant (inventory, or a request its Owner and Security approved); refused while another project of the tenant holds the person; directory-role holders left to people (at plan and at execute); unset production flag = production; expired plans closed and replaced; confirmed runs always verified (also when switched off half-way)
- `src/core/PmoReview.Core/Workflow/WorkflowService.Prod.cs` — the ProdApprover is never the creator, a (re)submitter or the beneficiary; `Resources/ProdGuard.cs`, `Domain/Tenants.cs` — environment-less rows guarded as Prod, every prod*/prd* spelling is Prod
- `src/core/PmoReview.Core/Sources/SourcesOfTruth.cs`, `Review/ReviewChecks.cs`, `Workflow/WorkflowAgent.cs`, `WorkflowService.cs` — retired and expired rows fail the review and are re-checked at every grant point (`MappingSheet.AsReviewed`)
- `src/core/PmoReview.Core/Services/RequestService.cs` — no new request for a leaver or for a person offboarded in the project
- `src/adapters/.../Rbac/ArmRoleAssignmentReader.cs` (ARM `nextLink`, F103), `Entra/GraphDirectoryReader.cs` + `Directory/OffboardingContracts.cs` (`DirectoryRole`), `Uploads/UploadToken.cs` + `PilotApp.cs` (tenant checked before keys), `Uploads/UploadService.cs` (idempotency per app), `Domain/Documents.cs` (`bool? IsProductionTenant`), `Domain/Governance.cs` (no `Cancelled`)
- `src/web/src/components/OffboardingPanel.tsx`, `lib/offboarding.ts`, `lib/types.ts`, `offboarding.test.ts`, `e2e/two-projects.spec.ts` — "Make a new plan" after an expired plan; the e2e offboards after the approvals
- `tools/import_workbook.py` (`--production-tenant yes|no`) + test; `tools/local-api/Program.cs`, `tools/run_local.sh` (local SAMPLE projects get the local agent tenant)
- Tests: `F3ProdGuardTests` (+3), `F5OffboardingTests` (+8), `RetiredRowTests` (new, 5), `OffboardingAdapterTests` (+1), `F4UploadTests`, `DataModelV11Tests`, `Support/Fakes.cs`, `Support/UploadFiles.cs`
- `docs/VERIFIED_FACTS.md` (F103), `docs/PERMISSIONS.md` (row C built), `docs/EXTERNAL_UPLOAD_API.md`, `docs/V1_1_PLAN.md` (P6 fixes 12a, 13a–13c, F3a, F4a; prerequisite 7), `workflows/09-onboarding-flow-v2.md`, `CLAUDE.md`
Tests: .NET 494/495 (1 opt-in Cosmos emulator skip); SPA 24/24; Playwright 11/11 (local SWA emulator); Python tool tests 5/5; `tsc`, SPA build and `az bicep build infra/main.bicep` clean. 22 mutation checks, all caught (`docs/V1_1_TEST_RESULTS.md`).
Verified facts added: 1 (F103, see VERIFIED_FACTS.md); 18 older rows (F5–F55) now cite a Learn page re-read on 30 Oct 2026, with F21, F30, F38 and F39 corrected where Learn differs
Open questions:
1. Decision 12 is tightened (12a): a tenant whose production flag is not set counts as production, so offboarding there needs a second person to confirm. Confirm, or say which tenants are not production (`import_workbook.py --production-tenant no`).
2. Offboarding a person that two projects of one tenant hold now needs the other project to list them as Leaver first (13a). Confirm this is the wanted rule.
Risks:
1. Re-check 3 (`291438a`) closed 22 and 23 and added Low 24 (F38's citation misquoted Learn), corrected afterwards in `VERIFIED_FACTS.md` (not re-checked). Re-check 4 (`35e7a62`) closed 24. The docs test checks that every fact row cites learn.microsoft.com and that every fact id the three documents cite (F6 and above; F1–F5 there name the features) exists; that every platform claim carries a citation was traced by hand by the reviewer (re-check 3).
2. Open by decision (not fixed in P6): Medium 7 (Prod "Read" rows with no Azure roles are trusted), 8 (no list of mapping quality issues for the DataMaintainer), 9 (part: another project of the tenant can still take a new request for an offboarded person; the agent does not refuse a disabled account), 11 (an upload apply cut by the 45 s SWA limit can stay half-applied), 12 (apply is not bound to the validated plan); Low 5 (near-duplicate variants through the single-request route), 15, 18, 19. Each has a proposed fix in `REVIEW.md`.
3. Graph and ARM are mocked only (R9): subscription listing (F84), the direct-member query (F94), TAP delete and `nextLink` behaviour are proven only by the first approved live dry run.
4. A project with environments but no Prod now sends environment-less rows above Read to a ProdApprover (fails closed, F3a).
Next step: STOP — Gokul approves P6 and picks a P7 deployment option (below). Nothing is deployed.

### P7 deployment options (offered because Critical = 0 and High = 0)
| | 1. Data and code, every new switch off (recommended first) | 2. Option 1 + agent TAP mail (F2) | 3. Option 2 + offboarding writes (F5), pilot subscription only |
|---|---|---|---|
| Steps | `deploy_free.sh whatif` → "approved" → `apply` (8 Cosmos containers, worker data roles, new settings all off) → `app` + `worker` redeploy → import each tenant with `--tenant-id` and `--production-tenant` → register ProdApprover and DataMaintainer users | `tap <sub> on --sender <mailbox>` dry run (shows the tenant TAP policy) → `--approved-by`; Exchange admin scopes `Mail.Send` to that one mailbox (RBAC for Applications, F81) | Admin consent for `User.RevokeSessions.All` and `User.EnableDisableAccount.All`; Reader + users-only RBAC Administrator (delete) on the pilot subscription; `OFFBOARD_SUBSCRIPTIONS`, `AGENT_OFFBOARDING=true`. P7 adds an `offboarding <sub> on\|off` command (dry run → `--approved-by`), like `provisioning` |
| Live after it | F1 selector, F3 Prod guard and escalations, F4 Data uploads page, F5 plans (dry runs, read only); TAP passes stay with the ID team | + the joiner gets the one-time TAP mail at the verified enterprise mail only | + offboarding executes, verifies, mails the PMO |
| New Azure resources | None: containers in the existing free-tier account (21 of 25 under shared throughput, F90) | None (Exchange configuration only) | None (permissions only) |
| Expected cost | ≈ ₹0 extra (budget ₹2,000/month unchanged; a few hundred more worker runs inside the Flex free grant, F93) | ≈ ₹0 | ≈ ₹0 |
| Prerequisites | what-if output and your approval; ProdApprover, DataMaintainer and PMO names; tenant id and production flag per tenant; people inventory (I2) per tenant | sender mailbox; an Exchange admin; people inventory with EnterpriseEmail | a Global or Privileged Role Administrator for admin consent; first live dry run on a SAMPLE test account |
| Rollback | redeploy the previous commit (`app`, `worker`); the new containers stay empty and harmless | `tap … off` (always allowed); remove the Exchange role assignment | `AGENT_OFFBOARDING=false` (settings only, always allowed); then remove the grants (approval) |

The external upload API (F4) stays off in all three: it needs hosting option B or C (`docs/HOSTING.md`).

Manual steps (people, not code): admin consent for the Graph application permissions (Global or Privileged Role Administrator); Exchange RBAC for Applications scope for `TAP_MAIL_SENDER`; ProdApprover and DataMaintainer users per project (`register_user.py`); PMO and team contacts (Contact inventory page); tenant id and production flag per tenant (`import_workbook.py --tenant-id --production-tenant`); people inventory upload per tenant; an `Uploads.Write` app registration only if the external API is switched on later.
