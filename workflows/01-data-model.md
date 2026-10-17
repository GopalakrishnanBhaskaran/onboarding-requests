# 01 — Data model and access

> **v2 (17 Oct 2026):** the request flow, roles and checks changed — see `workflows/09-onboarding-flow-v2.md` and `docs/ARCHITECTURE.md`. This SOP records how the phase was built; the build and test commands in `CLAUDE.md` are current.

**Purpose:** Build the Cosmos model, the server-side access model and snapshot logic, and prove cross-project isolation.

**Inputs:** Approved schema in `docs/PHASE0_PLAN.md` §3; `VERIFIED_FACTS` F12–F17.

**Steps**
1. `src/core`: domain records, `IRepository`, `AccessPolicy` (role → permitted actions), `SnapshotBuilder` (canonical JSON + SHA-256).
2. `src/adapters/CosmosRepository`: one database, shared throughput ≤ 1000 RU/s, 7 containers with partition keys from the plan; unique key `/idempotencyKey` on `reviewRuns`; ETag `IfMatch` on every replace.
3. `src/api`: `ClientPrincipalReader` (header only, reject missing/malformed), `AuthorizationGuard` (deny by default, project filter on every query).
4. `staticwebapp.config.json`: all routes require `authenticated`; block non-Entra providers (`/.auth/login/github` etc. → 404).
5. `tools/seed_sample_data.py`: SAMPLE data only.
6. Tests: two users in two projects; read, list, update, analyze, decide, forged body `projectId`/`userId`, missing header, unknown user.

**Outputs:** code, tests, seed script, updated `VERIFIED_FACTS` (SWA auth Free vs Standard).

**Validation (exit gate):** cross-project read/review/tamper tests all fail closed (403/404, no data in body).

**Failure handling:** emulator not available → run Cosmos-dependent tests against an in-memory repository implementing the same contract, and mark emulator run as pending. Any test showing disclosure blocks the phase.
