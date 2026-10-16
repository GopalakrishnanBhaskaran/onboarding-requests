# 00 — Setup and plan

**Purpose:** Create the repo skeleton, rules and plan; get Gokul's plan approval before any code.

**Inputs:** Pilot prompt v1.0 (16 Oct 2026); Locked Power Apps prompt pack v1.0 (9 Oct 2026).

**Steps**
1. Create `CLAUDE.md`, `SCOPE.md`, `workflows/`, `tools/`, `docs/`.
2. Verify platform facts on Microsoft Learn; record in `docs/VERIFIED_FACTS.md`.
3. Reconcile `SCOPE.md` with the locked pack (included/deferred table).
4. Write `docs/ARCHITECTURE.md` (Mermaid + A1 mapping).
5. Write `docs/PHASE0_PLAN.md`: findings, one question list, schema, plan, manual prerequisites.
6. Commit and push. **Stop.**

**Outputs:** files above, one commit.

**Validation:** every SOP has purpose, inputs, steps, outputs, validation, failure handling; every fact row has URL + date + status.

**Failure handling:** docs unreachable → record `SEARCH`/`UNVERIFIED` status and list as an open item; locked pack missing → mark `SCOPE.md` draft and ask. Do not start Phase 1 without "approved".
