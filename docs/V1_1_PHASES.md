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
