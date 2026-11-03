# Test results, errors and fixes, screenshot index (PEST dev, `pesstddevdaiweu01`)

Evidence only: a test counts as PASS only with a PASS line in `evidence/E-results.txt` and its masked evidence file.
Status on 2026-11-03 04:20Z.

## 1. Test matrix

| Test | Method | Status | Blocked on | Evidence |
|---|---|---|---|---|
| OneLake shortcut: read `input/input.csv` | Trusted workspace access | **NOT RUN** | The storage admins must add the resource instance rule (requested). Then you create the shortcut in the UI (org account) and run `e_tests.sh shortcut-verify`. | none |
| Notebook: read and write parquet over `abfss://` | Fabric managed private endpoint | **NOT RUN** | The wave 2 approval and `c_managed_pe.sh --apply`, then the storage admins approve the private endpoint | none |
| Pipeline: Lakehouse → ADLS write, then ADLS → Lakehouse read | VNet data gateway (org account) | **NOT RUN** | You create connection `poc-adls-gw` in the UI, then the wave 3 approval and `e_tests.sh gw-roundtrip` | none |
| Pipeline over trusted workspace access | Trusted workspace access | **NOT RUN** | The resource instance rule, plus a data role at account scope (FB6) | none |
| Other public IP, anonymous | Network block | **PASS (blocked)** | — | `PROBE-T2-01-other-ip.txt` (01:24Z), `PROBE-T2-02-other-ip-control.txt` (04:16Z): blob 403 `AuthorizationFailure` from Claude's container; an open-network control account answered 200 / 401 from the same IP (FB38) |
| Other public IP, signed in | Network block | **NOT RUN** | You run `e_tests.sh probe` in Cloud Shell | none |
| Your device: portal access to the account | Control plane | **UNVERIFIED** | A check from your device | none |

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

## 3. Screenshot index

None yet. The POC runs as a Cloud Shell hand-off: you run the tools and paste masked text output.
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
