# R Final report (orchestrator)

`report/FINAL_REPORT.md` (local, not in git: it holds tenant ids and your IP), built from `state/state.json`, `logs/` and `evidence/`:
1. Architecture summary.
2. Runbook in the order it ran (from `agents.*.steps` timestamps).
3. Permissions table (`identity.*`, Azure roles used).
4. Network table: VNets, subnets, PEs + IPs, DNS zones + records, NSG rules, NAT.
5. Access/firewall before/after (`access.before`, `access.after`).
6. Validation output (`validation.*`).
7. Test connection + scan runs (id, status, assets, duration).
8. Errors and fixes (`errors[]`).
9. WAT summary: agent, wave, start/end, time saved vs serial.
10. Screenshot index (`evidence/`).
11. Open risks, cost items, cleanup and rollback.
