# B Purview network (wave 1)

**Objective:** transit VNet for the SHIR and every PE; Purview account + ingestion PEs.

**Inputs:** `discovery.purview.*` (arm_id, region, managed_event_hub_state, ingestion_storage, private_endpoint_connections), `design.cidr.transit_*`, wave 1 approval.

**Locks:** `vnet:pvdbx-transit-vnet`, `subnet:pvdbx-pe`, `purview`.

**Steps**
1. `rg-pvdbx-transit` in sub-A, workspace region.
2. `pvdbx-transit-vnet` 10.120.0.0/23: `pvdbx-shir` 10.120.0.0/27 (NSG `pvdbx-shir-nsg`, NAT `pvdbx-transit-nat` + `pvdbx-transit-nat-pip`), `pvdbx-pe` 10.120.1.0/26 (NSG `pvdbx-pe-nsg`, private endpoint network policies **Enabled** so the NSG applies).
3. NSG rules as in `WAVE_PLAN.md` (PE subnet: allow 443 from the SHIR subnet only).
4. Purview **account** PE `pvdbx-pv-account-pe` (group `account`), no DNS integration yet.
5. Purview **ingestion** PEs from the Purview Networking blade (or ARM): blob, queue (+ namespace only if managed Event Hubs is on). Screenshot `evidence/B-05-ingestion-pes.png`.
6. Do **not** change the Purview firewall here (F does it after scans pass).

**Expected output:** `network.transit.{vnet_id,shir_subnet_id,pe_subnet_id,nat_id}`, `network.pe.purview_account`, `network.pe.purview_ingestion_*` with private IPs.

**Rollback:** delete the PEs (Purview ingestion PEs from its Networking blade), then `az group delete -n rg-pvdbx-transit` (only `pvdbx-` resources live there).
