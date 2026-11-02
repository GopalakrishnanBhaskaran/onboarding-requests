# A Databricks network (wave 1; A2 in wave 2)

**Objective:** workspace VNet-injected (needed for front-end Private Link, DOC_CHECK D2), then a `databricks_ui_api` PE in the transit VNet.

**Inputs:** `discovery.workspace.*` (arm_id, region, vnet_injected, load_balancer_configured, running_clusters), `design.cidr.dbx_*`, wave 1 approval in `approvals`.

**Locks:** `workspace`, `vnet:pvdbx-dbx-vnet`; A2 also `subnet:pvdbx-pe`.

**Steps (skip 1–5 if already VNet-injected with SCC)**
1. Stop if `load_balancer_configured` is true (docs: contact the account team).
2. Terminate running clusters and pause job schedules (list them in state first so they can be restarted). Warn: users of this workspace lose running compute.
3. Deploy in the workspace RG (Bicep, what-if shown in the approval): `pvdbx-dbx-nsg`; `pvdbx-dbx-vnet` 10.110.0.0/22 with `pvdbx-dbx-host` 10.110.0.0/24 and `pvdbx-dbx-container` 10.110.1.0/24, both delegated to `Microsoft.Databricks/workspaces`, NSG attached, `pvdbx-dbx-nat` + `pvdbx-dbx-nat-pip`.
4. Update the workspace (template `apiVersion 2026-01-01`): keep every current property; add `customVirtualNetworkId`, `customPublicSubnetName=pvdbx-dbx-host`, `customPrivateSubnetName=pvdbx-dbx-container`; remove `vnetAddressPrefix`, `natGatewayName`, `publicIpName`; `enableNoPublicIp=true`; `publicNetworkAccess=Enabled`; `requiredNsgRules=AllRules`. What-if must show only these changes.
5. Wait for `provisioningState=Succeeded` (~15 min). Validate: create a small cluster or run a job; restart what step 2 stopped. Screenshot the Networking blade (`evidence/A-05-networking.png`).
6. **A2 (wave 2, after B):** create `pvdbx-dbx-uiapi-pe`, sub-resource `databricks_ui_api`, in `pvdbx-transit-vnet/pvdbx-pe`, no DNS integration (C adds the zone group). Approve the connection if pending.

**Expected output:** `network.dbx.{vnet_id,host_subnet,container_subnet,nsg_id,nat_id}`, `network.dbx.injected=true`, `network.pe.dbx_uiapi.{id,ip}`.

**Rollback**
- Before step 4: delete the new NSG, VNet, NAT, public IP.
- After step 4: the docs offer no move back to a managed VNet. Fallback = keep the injected network (it is what the workspace now uses); never delete `pvdbx-dbx-vnet` while the workspace uses it.
- A2: delete the PE (`az network private-endpoint delete`).
