# Design and wave plan (draft, waiting for your OK)

Region: same as the workspace (from discovery). Names start with `pvdbx-`. CIDRs are checked against every VNet discovery finds before wave 1.

## Target architecture

```
My device (IP /32) ──public──► Workspace front end ──► IP access list: ALLOW my-device only
                                                       (every other public IP → 403)

Purview (sub-A) ◄─ account PE + ingestion PEs (blob, queue) ─┐
                                                             │  transit VNet  pvdbx-transit-vnet 10.120.0.0/23
Kubernetes SHIR VM (no public IP, NAT egress) ─ pvdbx-shir ──┤
  10.120.0.0/27                                              │
                                                             └─ pvdbx-pe 10.120.1.0/26
                                                                └─ databricks_ui_api PE ──► workspace (sub-B)
Workspace VNet (VNet injection)  pvdbx-dbx-vnet 10.110.0.0/22
  pvdbx-dbx-host 10.110.0.0/24, pvdbx-dbx-container 10.110.1.0/24 (delegated, NSG, NAT)
```

## Resources (proposed)

| Agent | Resource | Where | Notes |
|---|---|---|---|
| A | `pvdbx-dbx-nsg`, `pvdbx-dbx-vnet` (host + container subnets delegated to `Microsoft.Databricks/workspaces`), `pvdbx-dbx-nat` + public IP | workspace RG, sub-B (docs: same RG as the workspace) | only if discovery shows a managed VNet |
| A | Workspace update → VNet injection | the workspace | **all clusters and jobs terminated first**; ~15 min; public access stays Enabled; `AllRules` |
| A (wave 2) | `pvdbx-dbx-uiapi-pe` (`databricks_ui_api`) | transit `pvdbx-pe` subnet | needs VNet injection done |
| B | `rg-pvdbx-transit`, `pvdbx-transit-vnet`, subnets `pvdbx-shir`, `pvdbx-pe`, NSGs, `pvdbx-transit-nat` + public IP | sub-A | private subnets (D10) |
| B | Purview **account** PE, **ingestion** PEs (blob, queue; Event Hubs only if on) | transit `pvdbx-pe` | no portal / platform PE (D5) |
| C | Private DNS zones in `rg-pvdbx-dns`: `privatelink.azuredatabricks.net`, `privatelink.purview.azure.com`, `privatelink.blob.core.windows.net`, `privatelink.queue.core.windows.net` (+ `privatelink.servicebus.windows.net` if Event Hubs) | sub-A | linked to the **transit VNet only** (D8); blob/queue links with fallback to internet |
| C | VNet peering | — | **dropped**: not needed when the PE sits in the transit VNet (D8). Say if you still want it. |
| D | `pvdbx-shir-vm` Ubuntu 24.04, Standard_D8as_v5 (8 vCPU, 32 GB), 128 GB Standard SSD, no public IP, SSH key only, k3s single node, IRCTL registers `pvdbx-k8s-shir` | transit `pvdbx-shir` | managed through `az vm run-command` (no inbound port, no Bastion). k3s is a Linux Kubernetes; IRCTL's environment check on k3s is **UNVERIFIED** → fallback kubeadm or a 1-node AKS. |
| E | Purview MI as service principal (Entra managed) in the account, assigned to the workspace (USER); entitlements workspace + Databricks SQL access; warehouse CAN USE; UC: USE CATALOG, USE SCHEMA, SELECT on the table | Databricks | no Azure RBAC role needed for the MI (P1) |
| F | IP access list `my-device` (your IP /32) created **before** `enableIpAccessLists=true`; Purview firewall *Disabled for ingestion only* after the scans pass | workspace, Purview | no Purview IP rule exists (D5); no storage firewall (D6) |

### NSG rules
- `pvdbx-pe-nsg` (PE subnet, network policies on): Allow TCP 443 from `10.120.0.0/27` (SHIR) → `10.120.1.0/26`, priority 100; Deny all inbound, priority 4000.
- `pvdbx-shir-nsg`: Deny inbound from Internet, priority 4000 (no inbound allow at all). Outbound default (needs MCR, `*.compute.governance.azure.com`, Ubuntu/k3s packages through NAT).
- `pvdbx-dbx-nsg`: Databricks adds its own rules (AllRules); we add nothing that blocks them. Internet inbound is denied by the default rules.

## Waves

| Wave | Parallel agents | Depends on | Approval batch |
|---|---|---|---|
| 0 | Discovery (read-only), your IP | sign-in | your OK on this design |
| 1 | **A** workspace VNet + NAT + migrate to VNet injection · **B** transit VNet, NSGs, NAT, Purview PEs · **D** SHIR VM create · **E** SP, assignment, CAN USE, UC grants | wave 0 OK | **one approval**: what-if output + resource list + cost |
| 2 | **A2** Databricks PE → **C** DNS zones, links, zone groups → **D** k3s + IRCTL register (Running) → **F** IP list (my IP first, then enable) | A + B done | **one approval**: what-if + the lockdown step |
| 3 | **V** validation (nslookup / nc / curl from the SHIR VM; your device allowed; cloud runner blocked) → **S** register source, test connection, scan 1, scan 2, Data Map check → Purview firewall → **R** report | all | Purview firewall change is in the wave 2 batch |

Locks: an agent writes `locks.<resource>` in `state/state.json` before a change and removes it after. Shared resources: `subnet:pvdbx-pe` (A2, B), `workspace` (A, E, F), `purview` (B, S, F).

## Cost (Central India list prices, USD, see DOC_CHECK P13)

| Item | Qty | Per month if kept | Note |
|---|---|---|---|
| SHIR VM D8as_v5 Linux | 1 | 162 (24×7) | deallocate between scans: ~0.22 per hour used |
| SHIR OS disk E10 128 GB | 1 | 11 | charged while it exists |
| Private endpoints | 4 (5 with Event Hubs) | 29–37 | + 0.01 per GB |
| NAT gateways + public IPs | 2 + 2 | ~66 + 7 | NAT price **UNVERIFIED** (~0.045/h) |
| Private DNS zones | 4–5 | 2–3 | |
| Bastion | 0 | 0 | not used (run-command) |
| **Total** | | **~125 with the VM off, ~290 with it on 24×7** | ≈ ₹10,500 / ₹24,500 at ₹84 per USD (rate **UNVERIFIED**) |

Plus: SQL warehouse minutes during tests and scans (auto-stop 10 min), Purview scan billing (existing account). The PMO pilot budget is ₹2,000/month, so this needs its own approval and budget; everything goes into `pvdbx-` resources so cleanup is a short, listed delete.

## Open decisions (asked once, before wave 1)
1. Runtime: Kubernetes SHIR on one VM (recommended, documented) vs Managed VNet IR v2 (no VM, UNVERIFIED for MI + private path).
2. Optional extras that the docs say are not needed: back-end Private Link, workspace storage firewall (irreversible part), `browser_authentication` web-auth workspace, VNet peering.
3. Where commands run: your computer through Remote Control (browser steps, your real IP) or this cloud session with a device-code sign-in (API only, no browser).
