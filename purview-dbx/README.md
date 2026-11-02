# purview-dbx — Purview → Azure Databricks Unity Catalog over private endpoints

Separate from the PMO onboarding app. Goal: Purview's **system-assigned MI** scans `dbw_pmo_pkt2ofpqmlf3e.default.sample_access_test` through a self-hosted IR on a private path; the workspace answers only your device's IP and the private network.

Read first: `DOC_CHECK.md` (where the docs differ from the original plan), `WAVE_PLAN.md` (design, waves, cost).

## WAT layout
| Folder | What |
|---|---|
| `workflows/` | one SOP per workstream: 00 discovery, A Databricks network, B Purview network, C DNS, D SHIR, E identity, F lockdown, V validation, S scans, R report |
| `tools/` | one job per script; each logs to `logs/<tool>.log` with tokens masked |
| `tools/pw/browser.sh` | `playwright-cli -s=<agent>` sessions (headed, on your computer); stops at any sign-in page |
| `state/state.json` | the only hand-off between agents: inputs, discovery, resource ids, locks, approvals, errors (created from `state.template.json`; **not in git**) |
| `evidence/` | screenshots `<workstream>-<step>-<name>.png` (**not in git**) |
| `report/` | discovery and final report (**not in git**: tenant ids, your IP) |

## Rules the tools enforce
- Read-only tools run any time. Change tools are dry runs unless `--approved-by "<name> <date>"` is given (one approval per wave).
- Never type credentials: `browser.sh` exits 3 on a sign-in/MFA page; `discover.sh` stops if Azure CLI is not signed in.
- Your IP is used only after `my_ip.sh --confirm`; the allow list is created before IP lists are switched on.
- An agent locks a resource in `state.json` before changing it (`state.py lock <resource> <agent>`); another agent gets exit 3.
- Tokens, registration keys and passwords never reach argv, logs or state.

## Run order
```bash
bash purview-dbx/tools/my_ip.sh                 # on YOUR device; then confirm the IP
bash purview-dbx/tools/discover.sh --purview <purview-account-name>
python3 purview-dbx/tools/state.py show         # what every agent sees
```
Self-test (offline): `python3 purview-dbx/tools/state.py self-test`.
