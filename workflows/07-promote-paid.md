# 07 — Promotion pack

**Purpose:** Document and prove every paid swap so the same code moves to a paid environment.

**Inputs:** Disabled adapters; locked pack; `VERIFIED_FACTS`.

**Steps**
1. `docs/PROMOTION.md`, one section per swap: RuleOnly → AzureOpenAI; managed → BYO Function App (currently recommended plan, verify) + managed identity to Cosmos; Inline → Queue (or Power Automate); logs → App Insights with sampling; SWA Free → Standard; optional Cosmos → Dataverse (1:1 container → table); Azure Pipelines YAML with workload identity federation.
2. Each section: what changes, config keys, code switch, prerequisites, approval, rollback, cost inputs (no invented prices).
3. `config/pilot.settings.json` and `config/paid.settings.template.json` (placeholders only).
4. Local test: flip provider flags with mocks; build and contract tests pass for each paid path.

**Outputs:** `docs/PROMOTION.md`, config templates, test evidence.

**Validation (exit gate):** every paid path compiles and passes contract tests locally with mocks.

**Failure handling:** a paid path that cannot be tested with mocks is documented as an open risk, not claimed as ready.
