# tools/

Helper scripts Claude Code and operators run. Each script is created in the phase shown and has `--help`.

| Script | Purpose | Phase |
|---|---|---|
| `seed_sample_data.py` | Seed SAMPLE projects, rules, mappings, users into Cosmos (emulator or free-tier account). Refuses non-SAMPLE data. | 1 |
| `validate_result_schema.py` | Validate a review result JSON against `src/core/schema/review-result.v1.json`. | 2 |
| `run_acceptance.sh` | Run the acceptance suite and write `docs/PILOT_REPORT.md`. | 5 |
| `export_pilot_report.py` | Turn acceptance test output into the PILOT_REPORT table. | 5 |
| `check_free_tier.sh` | Check the subscription has no existing Cosmos free-tier account and SWA Free quota is available. | 6 |
| `cost_check.sh` | List resources in the RG, flag non-allowed types, show current-month cost. | 6 |
