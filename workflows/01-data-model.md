# 01 — Data model and access (v2)

**Purpose:** Store requests, workflow state and the two sources of truth in Cosmos with server-side, deny-by-default access per project.

**Inputs:** `SCOPE.md` (roles, sources of truth); `VERIFIED_FACTS` F12–F17.

**Steps**
1. `src/core/Domain` + `Workflow/WorkflowState.cs`: `Project` (contacts, domain-ID suffix, group naming pattern), `MappingRow`, `ResponseRecord`, `AccessRequest` with its workflow state (`flow`: status, waiting on, reviewed scope, approvals, team tasks, final checks, stages, events, attempts).
2. `src/core/Storage/DataStores.cs` + `src/adapters/Storage/CosmosDocumentStore.cs`: 8 containers — `projects`, `requests`, `reviewRuns` (unique `/idempotencyKey`), `userAccess` (`/userId`), `mappingSheet`, `responseDocument`, `notifications`, `issues`; legacy v1 containers stay declared but unused. ETag `If-Match` on every replace.
3. `src/core/Security/Access.cs`: roles Requester, Owner, Security, IdTeam, RoleTeam, PMO, Operator → permissions; identity only from `x-ms-client-principal`; 404 for a project the caller has no role in, 403 for a missing permission.
4. `staticwebapp.config.json`: every route needs `authenticated` except `/signed-out.html`; `/logout` → `/.auth/logout?post_logout_redirect_uri=/signed-out.html`; non-Entra providers blocked.
5. `tools/seed_sample_data.py`: SAMPLE data only (`--domain-suffix` for real UPN suffixes; `--register-user` with v2 roles).

**Outputs:** domain, stores, access policy, seed script, infra container list (`infra/modules/pilot.bicep`, kept in step by `InfraConsistencyTests`).

**Validation (exit gate):** `AccessTests` and AC25 — cross-project read, write and approval attempts fail closed with no data in the body; forged body fields are ignored.

**Failure handling:** any test showing disclosure blocks the phase. No Cosmos emulator → the in-memory store implements the same contract (ETag, unique keys).
