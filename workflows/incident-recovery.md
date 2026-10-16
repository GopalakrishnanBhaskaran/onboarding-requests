# Incident recovery

**Purpose:** Respond to cost, security or processing incidents in the pilot.

**Inputs:** Alert (budget email, failed test, user report).

| Incident | Immediate action | Follow-up |
|---|---|---|
| Budget alert / non-zero cost | `tools/cost_check.sh`; identify resource; stop or delete it (with Gokul's OK if not created by the pilot) | Root cause in `docs/COSTS.md`; tighten policy |
| Non-allowed resource in RG | Delete it; check policy assignment is active | Add missing deny case to policy |
| Cosmos RU throttling (429) | Confirm usage is pilot traffic; reduce polling; do **not** raise RU/s above 1000 | Note in report |
| Cross-project disclosure suspected | Redeploy `staticwebapp.config.json` with every route limited to a role nobody holds (site goes dark); preserve logs | Fix + regression test before re-enable |
| Connection string exposed | Rotate Cosmos keys; update SWA app setting; purge from git history if committed | Add secret scan check |
| Runs stuck in Running | Operator recover endpoint | Check processing budget vs 45 s limit |
| Bad deploy | Redeploy previous commit with SWA CLI | Record in RUNBOOK |

**Validation:** incident closed only with evidence (command output, test pass).

**Failure handling:** if recovery needs a paid resource or a change outside the RG, stop and ask.
