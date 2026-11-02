# A Databricks network (waves 1 and 2)

**Objective:** workspace VNet-injected, back-end Private Link on, one `databricks_ui_api` PE (clusters + SHIR through peering), `browser_authentication` PE, workspace storage reachable only through PEs.

**Inputs:** `discovery.workspace.*` (arm_id, region, vnet_injected, load_balancer_configured), `discovery.databricks.{running_clusters, warehouses, storage_credentials}`, `design.cidr.dbx_*`, approvals for wave 1 / 2.

**Locks:** `workspace`, `vnet:pvdbx-dbx-vnet`, `subnet:pvdbx-dbx-pe`.

## Wave 1
1. Stop if `load_balancer_configured` (docs: contact the account team).
2. Record running clusters, job schedules and classic/pro warehouses in state; terminate clusters, pause schedules, stop warehouses. **Users of this workspace lose running compute.**
3. Bicep in the workspace RG (what-if in the approval): `pvdbx-dbx-nsg`; `pvdbx-dbx-pe-nsg` (rules in `WAVE_PLAN.md`); `pvdbx-dbx-vnet` 10.110.0.0/22 with `pvdbx-dbx-host` 10.110.0.0/24 and `pvdbx-dbx-container` 10.110.1.0/24 (delegated, `pvdbx-dbx-nsg`, NAT), `pvdbx-dbx-pe` 10.110.2.0/26 (PE network policies on, `pvdbx-dbx-pe-nsg`); `pvdbx-dbx-nat` + `pvdbx-dbx-nat-pip`.
4. If not VNet-injected: workspace template `apiVersion 2026-01-01`, all current properties kept; add `customVirtualNetworkId`, `customPublicSubnetName=pvdbx-dbx-host`, `customPrivateSubnetName=pvdbx-dbx-container`; remove `vnetAddressPrefix`, `natGatewayName`, `publicIpName`; `enableNoPublicIp=true`, `publicNetworkAccess=Enabled`. What-if must show only these. Wait for `Succeeded` (~15 min).
5. Check: start a small cluster, run `spark.range(1).count()`, terminate. Screenshot `evidence/A-05-networking.png`.

## Wave 2
6. All compute stopped again. Set `requiredNsgRules=NoAzureDatabricksRules` (> 15 min).
7. `pvdbx-dbx-uiapi-pe` (`databricks_ui_api`) and `pvdbx-dbx-auth-pe` (`browser_authentication`) in `pvdbx-dbx-pe`, no DNS integration (C adds zone groups). Approve if pending.
8. Storage firewall (only if approved and discovery shows no storage credential on the managed-RG connector): access connector `pvdbx-dbx-ac` (system-assigned) in the workspace RG; PEs `pvdbx-dbx-dfs-pe` (`dfs`) and `pvdbx-dbx-blob-pe` (`blob`) to the `dbstorage…` account in `pvdbx-dbx-pe`; approve them on the storage account; then `az databricks workspace update --default-storage-firewall Enabled --access-connector '{"id":"<pvdbx-dbx-ac id>","identity-type":"SystemAssigned"}'`.
9. After C: start a cluster and run a command (classic path through the PE). "Control Plane Request Failure" → check DNS link on the workspace VNet and the PE NSG. Restart what step 2 stopped.

**Expected output:** `network.dbx.*` (vnet, subnets, nsgs, nat), `network.dbx.injected`, `network.dbx.nsg_rules_setting`, `network.pe.{dbx_uiapi,dbx_auth,dbx_dfs,dbx_blob}.{id,ip}`, `network.dbx.storage_firewall`.

**Rollback**
- Step 3 only: delete the new NSGs, VNet, NAT, public IP.
- Step 4 done: no documented way back to a managed VNet; never delete `pvdbx-dbx-vnet` while the workspace uses it.
- Step 6: set `requiredNsgRules=AllRules` (compute stopped) after removing the back-end PE.
- Step 7: delete the PEs.
- Step 8: `--default-storage-firewall Disabled`; the managed-RG connector stays deleted (docs: by design); keep `pvdbx-dbx-ac`.
