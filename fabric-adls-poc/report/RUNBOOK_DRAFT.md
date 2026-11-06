# Production setup runbook — DRAFT (superseded 2026-11-06 by FINAL_SETUP_RUNBOOK.md for the managed private endpoint path)

**Status: DRAFT.** After wave 3 this becomes `FINAL_SETUP_RUNBOOK.md`, with only the steps whose tests passed. Steps that fail are removed and listed in `TEST_RESULTS.md` (errors + fixes).

## 0. Prerequisites
- An F SKU capacity in a region with Data Engineering and VNet data gateway support (FB11, FB12). Trial capacities don't support TWA (FB1).
- One workspace (not My workspace). The creator is a workspace admin. Free Fabric licenses are enough for lakehouses, notebooks and pipelines (FB17).
- Azure: Owner, or Contributor + User Access Administrator, on the storage account and VNet resource group. Subscription owner to register resource providers.
- Resource providers `Microsoft.Network`, `Microsoft.Fabric`, `Microsoft.PowerPlatform` registered.
- Conditional Access: exclude the workspace identity from workload-identity policies (FB9).

## 1. Storage network (Agent A)
1. Add the admin IP rule (bare IP, not /32) **before** any restriction (FB15).
2. Private endpoints `blob` and `dfs` in the PE subnet, with DNS zone groups on `privatelink.blob/dfs.core.windows.net` linked to the VNet.
3. Public network access = **Enabled from selected networks**, default action **Deny**. Leave the trusted-services bypass as is unless nothing depends on it (FB5).

## 2. Identity and RBAC (Agent B)
1. Create the workspace identity (workspace settings or `POST /v1/workspaces/{id}/provisionIdentity`).
2. Make the workspace identity a **Contributor** in the workspace.
3. Assign **Storage Blob Delegator** at storage-account scope and **Storage Blob Data Contributor** at container scope (FB3). The fallback is a Storage Blob Data role at account scope.

## 3. Resource instance rule (Agent B)
- `az storage account network-rule add --resource-id /subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/Fabric/providers/Microsoft.Fabric/workspaces/<wsId> --tenant-id <tenantId>`. This is the same Storage RP update as the documented PowerShell cmdlet; an ARM template is the documented alternative (FB4).

## 4. Managed private endpoint (Agent C)
1. `POST /v1/workspaces/{id}/managedPrivateEndpoints` (`targetSubresourceType: dfs`, plus `blob` if blob URLs are used).
2. Approve the Pending connection on the storage account (Networking → Private endpoint connections).
3. Confirm **Approved** in Fabric. Expect 3–5 minute Spark starts afterwards (FB11).

## 5. VNet data gateway (Agent D)
1. Register `Microsoft.PowerPlatform`. Create a dedicated subnet (/27 or larger) delegated to `Microsoft.PowerPlatform/vnetaccesslinks`.
2. `POST /v1/gateways` (`type: VirtualNetwork`, 1 member, auto-sleep after 30 minutes).

## 6. Connections
- TWA: cloud connection to ADLS Gen2 (`https://<acct>.dfs.core.windows.net`), credential **Workspace identity**, test skipped (FB7, FB8).
- Gateway: a Virtual network connection on the gateway, credential **organizational account** (UI) or service principal.

## 7. Tests (Agent E)
- Shortcut read; notebook read + write parquet (abfss); pipeline copy (TWA and/or gateway); unauthenticated probe from a non-allowed IP returns `403 AuthorizationFailure`; portal access from the admin device works.

## 8. Operate
- Pause the capacity when idle (FB18). Review resource instance rules (max 200), role assignments and the PE connections each quarter.
