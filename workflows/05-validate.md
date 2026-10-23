# 05 — Validate (v2)

**Purpose:** Prove the flow meets the critical gates before any deployment.

**Inputs:** All code; seeded SAMPLE data.

**Steps**
1. Acceptance cases `tests/acceptance` (AC01–AC27, `cases.json`): flow overview upfront, incomplete request, domain ID, review pass/fail, mapping and response document checks, risk flags, partial review never sent, unknown approver, Owner then Security, rejection, SLA, ID team task and TAP rules, TAP never accepted, group/role mismatch, mapping change re-review, final verification, ADO trail, status line, auto-fix limit, agent never approves/creates/assigns, prompt injection, cross-project isolation, daily summary, Azure OpenAI contract (mocked).
2. `tools/run_acceptance.sh` (`E2E=1` with `tools/run_local.sh` running adds the browser tests).
3. `tools/export_pilot_report.py` → `docs/PILOT_REPORT.md`; the critical-gates table is built from the `gate` flags in `cases.json`.

**Outputs:** `docs/PILOT_REPORT.md`.

**Validation (critical gates):** AC02, AC05, AC09, AC10, AC11, AC13, AC14, AC15, AC18, AC23, AC24, AC25 — all must pass.

**Failure handling:** any critical gate fails → stop, fix, re-run the full suite. The report states it ran locally on SAMPLE data; live evidence goes to `docs/DEPLOYMENT_LOG.md`.
