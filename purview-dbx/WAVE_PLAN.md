# Design and wave plan v2 — complete end-to-end private, test window only

Your decisions (2 Nov 2026): commands run on **your computer through Remote Control**; runtime **Kubernetes SHIR on one Linux VM**; **complete end-to-end private-endpoint connectivity**; **test only, then decommission** (middle ground on cost, free credits).

Region: the workspace's (from discovery). Names start with `pvdbx-`. CIDRs are checked against every VNet discovery finds.

## Target architecture

```
My device (IP /32) ──public──► workspace front end ── IP access list: ALLOW my-device only (others → 403)
My device ──public──► Purview portal (Entra sign-in; Purview has no IP allow list)

        transit VNet (sub-A) 10.120.0.0/23                     workspace VNet (sub-B) 10.110.0.0/22
 ┌──────────────────────────────────────────┐   peering   ┌────────────────────────────────────────────────┐
 │ pvdbx-shir 10.120.0.0/27                 │◄──────────►│ pvdbx-dbx-host 10.110.0.0/24  (delegated, NAT)  │
 │   K8s SHIR VM (no public IP, NAT egress) │  both ways  │ pvdbx-dbx-container 10.110.1.0/24 (delegated)  │
 │ pvdbx-pe 10.120.1.0/26                   │             │ pvdbx-dbx-pe 10.110.2.0/26                     │
 │   Purview account PE                     │             │   databricks_ui_api PE (front + back end)      │
 │   Purview ingestion PEs (blob, queue)    │             │   browser_authentication PE                    │
 └──────────────────────────────────────────┘             │   workspace storage PEs (dfs, blob)            │
  SHIR → 443 → Databricks PE subnet (NSG allow)           └────────────────────────────────────────────────┘
  Private DNS zones linked to BOTH VNets
```
Scan path, all private: SHIR → Databricks PE (API + SQL warehouse) → workspace storage PEs (Cloud Fetch) → Purview account + ingestion PEs. Only public hops left (no PE exists, docs P3): image pulls from `mcr.microsoft.com` and `*.compute.governance.azure.com`, through the NAT gateway.

## What "complete" adds compared to v1, and the docs basis
| Item | Docs basis | Note |
|---|---|---|
| Back-end Private Link: `databricks_ui_api` PE in the workspace VNet PE subnet, `Required NSG rules = NoAzureDatabricksRules`, public access **Enabled** | P15 (back-end page) | existing workspace: stop **all compute incl. SQL warehouses** first; > 15 min |
| SHIR uses the **same** PE through peering | P6: `databricks_ui_api` "handles REST API calls for both inbound and classic compute plane Private Link" | how-to pages show a second front-end PE in the transit VNet; if test connection fails because of this, fallback = add that PE (+1 PE, separate zone) |
| `browser_authentication` PE hosted on this workspace | P6: allowed "for non-production environments" | `pl-auth.<region>` record; only used by a browser inside the VNets |
| Workspace storage firewall + `dfs`/`blob` PEs + access connector outside the managed RG | P8 | **irreversible part**: deletes the access connector in the managed RG; gated on discovery showing no storage credential uses it |
| Peering transit ↔ workspace VNet, both ways | your plan | cross-subscription; no gateway transit; forwarded traffic off |
| Purview firewall **Disabled for ingestion only** | P11 | *Disabled from all networks* would lock your device out of the Purview portal (no IP allow list, your device is not in the VNet) |
| Not added: platform / portal PE, serverless NCC PEs | P9, P10, P6 | platform PE is tenant-wide; portal PE is classic-portal only; NCC is for serverless → your resources, not used by the scan |

## Resources
| Agent | Resource | Where |
|---|---|---|
| A | `pvdbx-dbx-nsg`, `pvdbx-dbx-pe-nsg`, `pvdbx-dbx-vnet` (host + container delegated to `Microsoft.Databricks/workspaces`, PE subnet), `pvdbx-dbx-nat` + public IP | workspace RG, sub-B (docs: same RG as the workspace) |
| A | Workspace update: VNet injection (if not yet) → then `NoAzureDatabricksRules` | the workspace |
| A | PEs `pvdbx-dbx-uiapi-pe` (`databricks_ui_api`), `pvdbx-dbx-auth-pe` (`browser_authentication`), `pvdbx-dbx-dfs-pe`, `pvdbx-dbx-blob-pe` (workspace storage); access connector `pvdbx-dbx-ac`; storage firewall on | workspace VNet PE subnet |
| B | `rg-pvdbx-transit`: `pvdbx-transit-vnet` (`pvdbx-shir`, `pvdbx-pe`), `pvdbx-shir-nsg`, `pvdbx-pe-nsg`, `pvdbx-transit-nat` + public IP; Purview account PE + ingestion PEs (blob, queue; + namespace only if managed Event Hubs is on) | sub-A |
| C | `rg-pvdbx-dns`: `privatelink.azuredatabricks.net`, `privatelink.purview.azure.com`, `privatelink.blob.core.windows.net`, `privatelink.queue.core.windows.net`, `privatelink.dfs.core.windows.net` (+ `privatelink.servicebus.windows.net` if Event Hubs), each linked to **both** VNets (blob/dfs/queue links: fallback to internet); peering both ways | sub-A |
| D | `pvdbx-shir-vm` Ubuntu 24.04, D8as_v5, 128 GB Standard SSD, no public IP, k3s, IRCTL → `pvdbx-k8s-shir` Running; auto-shutdown | transit `pvdbx-shir` |
| E | Purview MI service principal, workspace USER, entitlements, warehouse CAN USE, USE CATALOG / USE SCHEMA / SELECT | Databricks |
| F | IP list `my-device` (your IP /32) → `enableIpAccessLists=true`; Purview *Disabled for ingestion only* after scans pass | workspace, Purview |

### NSG rules
- `pvdbx-dbx-pe-nsg` (Databricks PE subnet, PE network policies on): allow TCP **443 from `10.120.0.0/27`** (SHIR, via peering); allow TCP 443, 6666, 3306, 8443–8451 from `10.110.0.0/23` (cluster subnets, back-end ports P6); deny all other inbound.
- `pvdbx-pe-nsg` (transit PE subnet): allow TCP 443 from `10.120.0.0/27`; deny all other inbound.
- `pvdbx-shir-nsg`: deny inbound from Internet; no inbound allow (access through `az vm run-command`).
- `pvdbx-dbx-nsg` (cluster subnets): with `NoAzureDatabricksRules` Databricks adds no inbound/outbound rules; keep the default rules (deny Internet inbound) and allow outbound to the PE subnet.

## Waves (one approval per wave)
| Wave | Parallel | Notes |
|---|---|---|
| 0 | discovery, your IP | read-only; your OK on this plan |
| 1 | **A** VNet + NSGs + NAT, then VNet injection · **B** transit VNet, NSGs, NAT, Purview PEs · **D** VM · **E** identity + grants | approval = what-if + list + cost; **workspace downtime**: all clusters, jobs and classic warehouses stopped during the network update |
| 2 | **A** NoAzureDatabricksRules + Databricks PEs (+ storage firewall if approved) · **C** zones, links, zone groups, peering · **D** k3s + IRCTL (Running) · **F** IP list (your IP first, then enable) | approval = what-if + the lockdown and storage-firewall steps |
| 3 | **V** validation → **S** source, test connection, scan 1, scan 2, Data Map → Purview firewall → **R** report | no infra change except the Purview firewall |
| 4 | **Decommission** (below) | approval: delete list |

## Cost — test window only (Central India list prices, DOC_CHECK P13)
| Item | Qty | Per day kept | Note |
|---|---|---|---|
| Private endpoints | 9 (10 with Event Hubs) | ~2.2 USD | 0.01/h each + 0.01/GB |
| NAT gateways + public IPs | 2 + 2 | ~2.4 USD | NAT price UNVERIFIED (~0.045/h) |
| SHIR VM D8as_v5 | 1 | 0.22 per hour **on** | deallocated outside test hours |
| SHIR disk 128 GB | 1 | ~0.4 USD | |
| Private DNS zones | 5–6 | < 0.1 USD | |
| Bastion | 0 | 0 | not used |

**A 1–2 day test ≈ 10–20 USD (≈ ₹850–1,700) from your credits.** Plus SQL warehouse minutes and Purview scan billing.

## Decommission (wave 4) and what stays
Deleted: SHIR VM + disk + NIC, IR registration, every `pvdbx-` PE, DNS zones and links, peering, transit VNet + NAT, Purview PEs; IP list switched off (or kept, your choice); Purview firewall back to *Enabled from all networks*; storage firewall off.

**Stays on the existing workspace (no documented way back):**
- VNet injection: the workspace keeps `pvdbx-dbx-vnet`; its NAT gateway (~33 USD/month, UNVERIFIED) stays unless you accept default outbound access (free, being retired). Required NSG rules go back to `AllRules` after the back-end PE is removed.
- If the storage firewall was enabled: the managed-RG access connector stays deleted (the replacement `pvdbx-dbx-ac` stays in use).
