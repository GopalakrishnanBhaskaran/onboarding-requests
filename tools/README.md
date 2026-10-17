# tools/

Helper scripts Claude Code and operators run (v2 onboarding flow).

| Script | Purpose |
|---|---|
| `deploy_free.sh` | `whatif` · `apply --approved-by` · `app` · `worker` · `status` · `params` · `grant-graph` (worker Graph **read** roles) · `revoke-graph-writes` (removes the retired Phase 2b/2c write roles; dry run unless `--approved-by`). |
| `workflow_diag.sh` | Read-only diagnosis: worker state and settings, Graph roles held (flags any write role), Cosmos roles, latest requests by stage, open issues, outbox. |
| `seed_sample_data.py` | Seed SAMPLE projects, mapping sheet, response document and users into Cosmos (`--domain-suffix @<tenant>` for domain-specific IDs); `--register-user <id> --project <p> --roles <v2 roles>`. Refuses non-SAMPLE data. |
| `run_local.sh` | Local end-to-end: dev-only `local-api` (in-memory SAMPLE data) behind the SWA CLI emulator on :4280. |
| `local-api/` | Loopback-only host for the same `ApiHost`; refuses to start without `PILOT_LOCAL_DEV=true`. |
| `validate_result_schema.py` | Validate a review result JSON against `src/core/schema/review-result.v2.json`. |
| `run_acceptance.sh` / `export_pilot_report.py` | Run the acceptance suite (AC01–AC27) and write `docs/PILOT_REPORT.md`. |
| `check_free_tier.sh` | Subscription has no other Cosmos free-tier account; SWA Free quota available. |
| `cost_check.sh` | Resources in the RG, non-allowed types flagged, current-month cost. |
