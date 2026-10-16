# 05 — Validate

**Purpose:** Prove the pilot meets the critical gates before any deployment.

**Inputs:** All code; seeded SAMPLE data.

**Steps**
1. Acceptance cases in `tests/acceptance`: complete request, missing mandatory field, invalid dates, unsupported project, unknown/ambiguous role, missing/expired requirement, conflicting mappings, prompt injection, forged approval, unauthorized caller, cross-project access, result tampering, duplicate trigger, concurrent analysis, edit during processing, timeout, malformed result, unsupported citation, retry, browser closure.
2. Run `tools/run_acceptance.sh`.
3. `tools/export_pilot_report.py` → `docs/PILOT_REPORT.md` (case, expected, actual, evidence, pass/fail).

**Outputs:** `docs/PILOT_REPORT.md`.

**Validation (critical gates):** zero cross-project disclosure; zero fulfilment actions; all seeded missing fields detected; every citation resolves to a pinned version; injection and forged approval non-executable.

**Failure handling:** any critical gate fails → stop, fix, re-run the full suite. Report notes the 90% agreement and 25% time-saving targets are out of pilot scope.
