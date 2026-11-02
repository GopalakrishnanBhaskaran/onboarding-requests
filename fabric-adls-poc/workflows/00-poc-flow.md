# SOP 00 — Fabric (public) → ADLS Gen2 (private) POC

**Run mode:** Remote Control on the owner's Mac. `az` and the browser are signed in by the owner; Claude never types credentials and stops at any sign-in or MFA prompt.
**Rules:** read-only until a wave is approved (`tools/state.py approve <wave> --by "<name> <date>"`). The device IP is allowed **before** any restriction. Least privilege. Anything not proven is marked UNVERIFIED. Facts: `report/VERIFIED_FACTS.md`.

## Methods per use case (from the docs, FB1–FB14)
| Use case | Method | Path | Identity |
|---|---|---|---|
| OneLake shortcut (read, query via SQL endpoint / Spark) | Trusted workspace access (TWA) | Fabric → storage public endpoint, admitted by the **resource instance rule** | Workspace identity (connection type WorkspaceIdentity) |
| Spark notebook (read + write parquet, `abfss://`) | **Managed private endpoint** (`dfs`) | Fabric managed VNet → private endpoint → storage | Notebook user's token (or workspace identity) |
| Pipeline copy ADLS → Lakehouse | ① TWA (documented, no gateway) ② VNet data gateway (private path through the `pvdbx-` VNet and the storage PE) | ① public endpoint + resource rule ② delegated subnet → PE | ① workspace identity ② org account (UI) or service principal; workspace identity isn't supported on gateways (FB8) |

## Waves and sub-agents
Each sub-agent works in its own Playwright browser profile (`.local/fabric-poc/pw-<agent>`). Locks are kept in `state/state.json`.

| Wave | Agent | Does | Locks | Approval |
|---|---|---|---|---|
| 0 | — | `tools/discover.sh` (read-only) → trade-off note → owner OK | none | OK on method and storage network mode |
| 1 | A storage network | device IP rule **first**; blob + dfs PEs in the `pvdbx-` PE subnet (or reuse); private DNS zone groups and VNet links; public access = selected networks, default Deny; trusted-services bypass unchanged | storage-network-rules, vnet | wave 1 |
| 1 | B Fabric identity | F2 capacity (if none), workspace (if none), workspace identity, identity = workspace Contributor; RBAC: Storage Blob Delegator (account) + Storage Blob Data Contributor (container); resource instance rule (ARM, built from the current rule set, what-if first) | fabric-capacity, fabric-workspace, storage-network-rules (after A) | wave 1 |
| 2 | C managed PE | `POST /workspaces/{id}/managedPrivateEndpoints` (dfs, + blob if needed) → approve on storage → status Approved | fabric-workspace, storage-account | wave 2 |
| 2 | D VNet gateway | register `Microsoft.PowerPlatform`; dedicated `/27` subnet delegated to `Microsoft.PowerPlatform/vnetaccesslinks`; `POST /gateways` (1 member, sleep 30 min); connection (org account in UI, or SP) | vnet, fabric-capacity | wave 2 |
| 3 | E tests + report | shortcut query; notebook read+write; pipeline copy (TWA and gateway); blocked from a non-allowed public IP; portal from the device; evidence + report | none (read) | wave 3 (test objects only) |

## Order inside wave 1 (never lock out)
1. Add the device IP rule (bare IP, not /32, FB15). Verify that a data-plane list from the device works.
2. Create or verify the PEs and DNS, and resolve `<acct>.dfs.core.windows.net` from inside the VNet (PE IP) and outside (public).
3. Only then: default action Deny / selected networks (if not already).
4. Resource instance rule for the workspace (after B has the workspace id).

## Rollback per wave
- Wave 1: remove the resource instance rule; delete the role assignments; delete the workspace identity (the app registration is removed with it); restore the network rule set from `.local/fabric-poc/01-storage-network.json` (raw); delete the PEs created by the POC (not the Purview ones); pause or delete the F2 capacity.
- Wave 2: delete the managed PE (wait 15 minutes before re-creating it); delete the gateway connection and gateway; remove the subnet delegation and the subnet.
- Wave 3: delete the test lakehouse, notebook and pipeline; delete the `poc/` test files in the container.
