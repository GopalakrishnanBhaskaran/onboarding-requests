# Test results, errors and fixes, screenshot index (PEST dev)

Evidence only: a test counts as PASS only with its masked evidence file.
Status on 2026-11-06 00:30Z. **Target changed on 2026-11-06 to `pesstddevatacadaolzweu01`** (FB40): the local Fabric workspace already had Approved blob + dfs managed private endpoints to it. The 2026-11-03 rows for `pesstddevdaiweu01` stay below as history.

## 1. Test matrix — `pesstddevatacadaolzweu01` (public access Disabled), workspace `pes-fab-data-dev-local-weu-1` (F16)

| Test | Method | Status | Evidence |
|---|---|---|---|
| Notebook: **write** CSV to ADLS (`abfss://`) | Fabric managed private endpoint (dfs, Approved) | **PASS** 2026-11-06 00:21Z | `E-T3-notebook-owner.txt` |
| Notebook: **read** the CSV back (3 rows) | same | **PASS** | same |
| Notebook: **write + read parquet** (3 rows back) | same | **PASS** | same |
| Other public IP (Cloud Shell), anonymous | Network block | **PASS (blocked)** blob 403 `AuthorizationFailure` | `E-T3-results-owner.txt` 00:24:43Z |
| Other public IP (Cloud Shell), **signed in** with blob read/write data actions | Network block | **PASS (blocked)** blob + dfs 403 `AuthorizationFailure` = network, not role (FB38) | same, 00:24:44Z |
| Firewall unchanged: public Disabled, default Deny, no new rules | Read-only check | **VERIFIED** | FB40 |
| Test lakehouse `poc_lh` | Fabric API | **PASS** | `E-T3-results-owner.txt` 00:12:11Z |
| OneLake shortcut | Trusted workspace access | **NOT POSSIBLE without the resource instance rule** (FB39). Needs the rule for workspace `856a****` plus a data role for the shortcut identity | `e_tests.sh shortcut-check` |
| Pipeline copy | VNet data gateway (org account) | **NOT RUN** (optional): create a VNet connection in the UI, then `e_tests.sh gw-roundtrip --connection <id>` | none |
| Pipeline copy | Trusted workspace access | **NOT POSSIBLE without the resource instance rule** (FB6, FB32) | none |

## 1b. History — `pesstddevdaiweu01` (2026-11-03)

| Test | Status | Evidence |
|---|---|---|
| Other public IP, anonymous | **PASS (blocked)** blob 403 `AuthorizationFailure` | `PROBE-T2-01-other-ip.txt`, `PROBE-T2-02-other-ip-control.txt` |
| Notebook 2026-11-06 run 1 | **INVALID** (wrong target, see error 16) | `E-T3-notebook-owner.txt` |
| Probe 2026-11-06 00:17Z | **INVALID** (no response, see error 18) | `E-T3-results-owner.txt` |

## 2. Errors and fixes (so far)

| # | When | Error | Cause | Fix |
|---|---|---|---|---|
| 1 | Agent B, first tenant | Capacity create: `Invalid principal name` | The capacity admin was not an enabled Member user of the tenant | `b_fabric_identity.sh --list-admins` and a precheck: the capacity admin must be an enabled Member user |
| 2 | Agent B | `capacity admin must be an enabled Member user (found: None None)` | `az ad user show` returns no `userType` / `accountEnabled` unless Graph is asked for them | Graph `$select=userType,accountEnabled`, lower-case compare |
| 3 | Pasted commands | `!contains` errors | Bash history expansion of `!` in an interactive shell | Removed `!` from pasted commands (grep / Graph instead) |
| 4 | Pasted commands | `unknown argument UPN>` | The `<native UPN>` placeholder was pasted as is | The tools refuse placeholder values |
| 5 | Every Cloud Shell restart | The clone is missing | Cloud Shell deletes files when an ephemeral session ends (FB37) | Session start: `tools/cloud_init.sh` + `CLOUD_SHELL_START.md` |
| 6 | `ws_check.sh` | A Trial (FT1) capacity reported as an F SKU | `F*` matched before `FT*` | The pattern order is fixed |
| 7 | `ws_check.sh` | "Workspace visible" taken as "same tenant" | Wrong inference | The tool checks that the storage account is in the signed-in tenant (TWA is not cross-tenant, FB32) |
| 8 | Agent A plan, tenant 2 | The plan would have set public access to selected networks and created new DNS zones | Build-mode logic | Verify-only mode: an account with Disabled public access and Approved blob+dfs private endpoints is never weakened |
| 9 | Agents B and C | C's admin request overwrote B's | Both wrote to the same file | One `ADMIN-REQUEST-<agent>.txt` per agent; `admin_request.sh` merges them into one mail |
| 10 | `discover.sh` | Stalled on calls per workspace | Too many workspaces | `--workspace-id` and a cap |
| 11 | `role_request.sh` | A custom role's AssignableScopes cannot be one storage account | Doc: management group, subscription or resource group only (FB36) | Defined on the RG, assigned on the account (assignment at resource scope: UNVERIFIED) |
| 12 | Found before block B ran | `state.py approve` wrote to a different file than the tools read; `--apply` would have said "not approved" | The default path was not updated when raw output moved to `fabric-adls-poc/.local/` | Fixed (`186a585`), with a tenant guard and one state per tenant profile |
| 13 | Probe | dfs answers 401 to anonymous requests, so it says nothing about the network | dfs checks authentication first | Only blob decides; signed-in probe added; 403 codes per FB38 |
| 14 | Agent B / C plans | The owner has no `storageAccounts/write`, no `roleAssignments/write` and no private endpoint approval on the account | Least-privilege access | Admin requests (`admin_request.sh`) or the scoped role grant (`role_request.sh`) |
| 15 | 2026-11-06, `c_managed_pe.sh` plan | `CHANGE create managed PE mpe-pesstddevdaiweu01-dfs` although the workspace already had Approved MPEs (to another account) | The tool matched MPEs only by its own name and the state still named the old account | MPEs now match on target account + sub-resource, whatever the name. `--apply` then created a Pending, unneeded MPE to `pesstddevdaiweu01` (delete it, FB40) |
| 16 | 2026-11-06, notebook run 1 | `System cancelled the Spark session due to statement execution failures` | State pointed at `pesstddevdaiweu01` (MPE Pending) and the container value carried a note, so the `abfss://` path was invalid | `e_tests.sh` keeps the container name only; state switched to `pesstddevatacadaolzweu01` / `fabric-poc` |
| 17 | 2026-11-06, `e_tests.sh notebook` | `ERROR line 138: save E-cloudshell-adls-listing.txt`, no NOTEBOOK result line | The Cloud Shell listing is expected to fail (private account); with `set -e` + `pipefail` it stopped the script | The listing is wrapped so it never stops the script |
| 18 | 2026-11-06, `e_tests.sh probe` | `REACHABLE … (blob HTTP  )` with an empty code | curl got no response (Cloud Shell sign-out); empty code fell through to REACHABLE | Empty code = `NO ANSWER (not decisive)` |
| 19 | 2026-11-06, request "shortcut without the resource instance rule" | Not possible by design | Shortcuts reach a firewalled ADLS account only through TWA; no MPE support; OPDG shortcuts only for S3/S3-compatible/GCS (FB39) | `e_tests.sh shortcut-check` (read-only) says READY or BLOCKED; `shortcut` refuses to run without the rule |

## 3. Screenshot index

S2 exists as the owner's portal screenshot (2026-11-06, `pesstddevatacadaolzweu01` > Networking > Private endpoints: two Fabric MPEs Approved; not stored in git). The POC runs as a Cloud Shell hand-off: you run the tools and paste masked text output.
Browser screenshots (`tools/pw_capture.mjs`, Playwright) need a browser signed in by you. They are planned for the UI steps:

| # | Planned screenshot | Step |
|---|---|---|
| S1 | Storage > Networking: public access Disabled, resource instance rule listed | after the storage admins add the rule |
| S2 | Storage > Networking > Private endpoint connections: the Fabric MPE Approved | after approval |
| S3 | Fabric workspace > Managed private endpoints: Approved | after approval |
| S4 | Fabric > Manage connections and gateways: `poc-adls-gw` on the VNet gateway | after you create it |
| S5 | Lakehouse `poc_lh`: shortcut `adls_poc` showing `input.csv` | wave 3 |
| S6 | Notebook run output (read + write) | wave 3 |
| S7 | Pipeline run: Succeeded | wave 3 |
