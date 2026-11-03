# Cost, limits, cleanup and rollback

Status 2026-11-03: prices come from VERIFIED_FACTS (Azure Retail Prices API, INR, 2026-11-02). Rows that need the capacity, the managed PE or the gateway to exist are marked **UNVERIFIED** until they run.

## 1. Cost items

| Item | Unit price | Running now? | Per hour | 730-hour month | Fact |
|---|---|---|---|---|---|
| Fabric capacity **F2** (Central India) | ₹19.0458 per CU-hour × 2 CU | not yet (Agent B) | **₹38.09** | ₹27,806 if never paused | FB19 |
| Private endpoints blob + dfs | ₹0.9523 per PE-hour, plus ₹0.38–0.95/GB processed | **yes**, since 23:57Z on 2 Nov | ₹1.90 | ₹1,390 | FB20 |
| Private DNS zones (privatelink.blob, privatelink.dfs) | ₹47.61 per zone-month | **yes** | — | ₹95.22 | FB20 |
| ADLS Gen2 Hot LRS | ₹1.90/GB-month | yes (KB of test data) | ~₹0 | ~₹0 | FB20 |
| VNet, subnets, subnet delegation, budget | free | yes | ₹0 | ₹0 | — |
| Fabric managed private endpoint | Azure charge | not yet (Agent C) | **UNVERIFIED** | **UNVERIFIED** | FB21 |
| VNet data gateway | uses the capacity's CUs, no separate Azure meter | not yet (Agent D) | **UNVERIFIED** | **UNVERIFIED** | FB12 |
| Fabric licences | Free licence, auto-assigned at first sign-in | — | ₹0 | ₹0 | FB17 |

**POC estimate:** about 8 active F2 hours (₹305), plus 3 days of PEs and zones (₹137 + ₹10), comes to about **₹450**. That holds only if the F2 is paused between sessions.

**Budget warning:** the RG budget is ₹1,000 (alerts at 50/80/100%). An F2 left running uses it up in about **26 hours**. Budgets only alert. They don't stop anything.

**Pause when idle:** `bash fabric-adls-poc/tools/capacity.sh pause`. Resume with `bash fabric-adls-poc/tools/capacity.sh resume`. Pausing stops billing, but any smoothed or overage usage still outstanding is billed when you pause (FB18).

## 2. Limits met or expected

| Limit | Effect on the POC | Fact |
|---|---|---|
| Trusted workspace access needs an **F SKU**. Trial doesn't work. | F2 is the minimum. | FB1 |
| Capacity admin must be a **member of the tenant**. B2B and MSA guests are refused ("Invalid principal name"). | A native user is the capacity admin and adds the owner as **contributor**. | FB24, FB26, FB28 |
| Only a capacity admin can add contributors. Assigning a workspace through the Fabric API needs contributor plus workspace admin. | Until then, the owner (Fabric admin) assigns through the Power BI admin API. That API allows 200 requests/hour. | FB27, FB28 |
| Storage IP rules: public IPv4 only, no /31 or /32, and **no effect on clients in the same Azure region**. | Bare device IP. The other-IP probe must come from **outside Central India**. | FB15 |
| Anonymous **dfs** request returns 401 before the firewall check. | The block is proven on **blob** (403 AuthorizationFailure). | PROBE-01 |
| First managed PE creates a managed VNet. **Starter pools are then disabled**, and Spark starts in about 3–5 min. | Notebook tests wait longer. | FB11 |
| Spark on F2: 4 VCores (20 with burst), queue limit 4. | Run one notebook at a time. | FB16 |
| VNet data gateway: any F SKU, **F8+ recommended**. Subnet reserves 5 IPs + 1 per member. Can't use the names gatewaysubnet or AzureBastionSubnet. | F2 with 1 member: performance **UNVERIFIED**. | FB12 |
| Gateway connections can't use **workspace identity**. The REST API has no OAuth2 type. | The owner creates the org-account connection in the UI. | FB8, FB14 |
| Subscription offer **FreeTrial_2014-09-01** with the spending limit on. | Whether it can buy an F2 is **UNVERIFIED** (the Agent B apply tells). | FB25 |
| Device-code sign-in is blocked in the tenant. | Cloud Shell hand-off. No Playwright screenshots from Claude's side. | FB22 |

## 3. Cleanup (end of POC, whole set)

Everything Azure-side is in **`rg-pvdbx-fabric-poc`**. The Fabric items live in the Fabric service and are removed first.

1. Pause the capacity: `bash fabric-adls-poc/tools/capacity.sh pause`. Billing stops at once.
2. In Fabric, as the owner:
   - delete the shortcut `adls_poc`, the lakehouse, the notebook and the pipelines (or delete the whole workspace in one step);
   - delete the connections under Settings > Manage connections and gateways (REST `DELETE /v1/connections/{id}`);
   - delete the VNet data gateway `vnetgw-fabric-adls-poc` (REST `DELETE /v1/gateways/{id}`);
   - delete the managed PE (REST `DELETE /v1/workspaces/{ws}/managedPrivateEndpoints/{id}`);
   - delete the workspace `ws-fabric-adls-poc` (REST `DELETE /v1/workspaces/{ws}`).
   Non-Power BI items on a deleted capacity are soft-deleted for 7 days (capacity-settings doc).
3. Delete the resource group, which removes the capacity, storage, PEs, DNS zones, VNet and budget:
   `az group delete -n rg-pvdbx-fabric-poc --yes`
4. Optional and free: unregister providers only if nothing else uses them (`az provider unregister -n Microsoft.Fabric`, `-n Microsoft.PowerPlatform`).
5. Check: `az group exists -n rg-pvdbx-fabric-poc` should print `false`. In Cost Management, there should be no new charges after the next day.

**Before step 3:** confirm the Purview work no longer uses these PEs or DNS zones (PURVIEW_REUSE.md). Deleting the RG removes them for Purview as well.

## 4. Rollback per wave (keep the rest)

| Wave / agent | Undo | Command or place |
|---|---|---|
| 3 / E tests | Remove the test items and files | Fabric UI (items). Storage files under `poc/input` and `poc/output`. |
| 2 / D gateway | Delete the gateway, then the subnet (the delegation goes with it) | `DELETE /v1/gateways/{id}`, then `az network vnet subnet delete -g rg-pvdbx-fabric-poc --vnet-name pvdbx-vnet-fabricpoc -n <gateway subnet>` |
| 2 / C managed PE | Delete the MPE in Fabric. The storage-side connection becomes Disconnected; remove it. | `DELETE /v1/workspaces/{ws}/managedPrivateEndpoints/{id}`, then `az network private-endpoint-connection delete --id <connection id>` |
| 1 / B identity | Remove the resource instance rule, the role assignments and the workspace identity. Unassign the capacity or pause it. | `az storage account network-rule remove -g rg-pvdbx-fabric-poc --account-name stpvdbxfabb5731f --resource-id /subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/Fabric/providers/Microsoft.Fabric/workspaces/<ws id> --tenant-id <tenant>`; `az role assignment delete --assignee <identity id> --scope <scope> --role "<role>"`; `POST /v1/workspaces/{ws}/deprovisionIdentity` |
| 1 / A network | **Never lock the owner out.** Keep the device IP rule until last. Remove the PE zone groups, PEs, zone links and zones. | `az network private-endpoint delete …`, `az network private-dns zone delete …`. The firewall stays on Deny with the IP rule. |

Each wave was approved separately, so each rollback is a change too: show the plan, then act on "approved".
