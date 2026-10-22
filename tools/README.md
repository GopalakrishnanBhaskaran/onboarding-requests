# tools/

Helper scripts Claude Code and operators run (v2 onboarding flow).

| Script | Purpose |
|---|---|
| `deploy_free.sh` | `whatif` · `apply --approved-by` · `app` · `worker` · `status` · `params` · `grant-graph` (worker Graph **read** roles) · `revoke-graph-writes` (removes the retired Phase 2b/2c write roles; dry run unless `--approved-by`) · `provisioning on|off` (v2.1; another role list replaces the constrained assignment) · `tap on|off [--lifetime <min>]` (agent TAP: shows the tenant TAP policy; dry run unless `--approved-by`) · `environment <pilot|dev|test|prod> [--redirect <mail>]` (settings only) · `email [--redirect <mail>]` (Azure Communication Services Email; dry run unless `--approved-by`). `app` seeds SAMPLE data only with `SEED_SAMPLE=1`. |
| `workflow_diag.sh` | Read-only diagnosis: worker state and settings, Graph roles held (flags any write role), Cosmos roles, latest requests by stage, open issues, outbox. |
| `verify_features.sh` / `verify_features.py` | Read-only feature checklist: PASS / WAIT / FAIL per feature (code deployed, environment settings, mail on and delivered, agent rights and agent TAP, the PEST and Vestacy tenants with no SAMPLE data left, the PEST test row, approvals, agent provisioning, sign-in details mailed, role assignment, end to end, open High issues) with the next step. `--self-test` checks the verdict logic without Azure. |
| `cosmos_python.sh` | Prints a Python with the Cosmos SDK (`requirements.txt`), installing it on demand (user site, else `.local/venv`). Used by the seed step of `deploy_free.sh` and by `workflow_diag.sh`. |
| `seed_sample_data.py` | Seed SAMPLE projects, mapping sheet, response document and users into Cosmos (`--domain-suffix @<tenant>` for domain-specific IDs); `--register-user <id> --project <p> --roles <v2 roles>`; `--access-scope <rg scope>` fills the SAMPLE Azure role scopes; `--test-contact <mail>` makes every project contact one tester mailbox; `--tenant-id PROJECT=<guid>|none`. Refuses non-SAMPLE data. `--remove-sample [--yes]` lists / deletes every SAMPLE document in the live database (test: `test_seed_sample_data.py`). |
| `import_workbook.py` | One tenant's Entra group classification + Data Response documents → mapping rows (environment, object id, red/amber flag, notes, every Azure role × resource). Output in `.local/` (real data never in git); `--resolve` maps resource names to scopes with read-only `az`; `--test-scope <rg>` puts every Azure role in one test resource group (pilot test in the agent's tenant); `--apply [--update]` writes Cosmos with `--tenant-id`, `--domain-suffix`, `--test-contact <mail>`, `--register-user <userId>=Role,Role`. Tests: `test_import_workbook.py` (SAMPLE workbook; `--fixture` rewrites `tests/fixtures/import-sample.json`). |
| `run_local.sh` | Local end-to-end: dev-only `local-api` (in-memory SAMPLE data) behind the SWA CLI emulator on :4280. |
| `local-api/` | Loopback-only host for the same `ApiHost`; refuses to start without `PILOT_LOCAL_DEV=true`. |
| `validate_result_schema.py` | Validate a review result JSON against `src/core/schema/review-result.v2.json`. |
| `run_acceptance.sh` / `export_pilot_report.py` | Run the acceptance suite (AC01–AC27) and write `docs/PILOT_REPORT.md`. |
| `check_free_tier.sh` | Subscription has no other Cosmos free-tier account; SWA Free quota available. |
| `cost_check.sh` | Resources in the RG, non-allowed types flagged, current-month cost. |
