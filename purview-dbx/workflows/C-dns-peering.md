# C DNS + peering (wave 2, after A step 7 and B)

**Objective:** every private name resolves to its PE IP in both VNets; the SHIR reaches the Databricks PE subnet through peering.

**Inputs:** `network.pe.*`, `network.transit.vnet_id`, `network.dbx.vnet_id`, wave 2 approval.

**Locks:** `rg:pvdbx-dns`, `vnet:pvdbx-transit-vnet`, `vnet:pvdbx-dbx-vnet`, each PE while adding its zone group.

**Steps**
1. Peering `pvdbx-transit-to-dbx` and `pvdbx-dbx-to-transit` (cross-subscription if the subscriptions differ): allow virtual network access on, forwarded traffic off, no gateway transit. Both show `Connected`.
2. `rg-pvdbx-dns` (sub-A). Zones: `privatelink.azuredatabricks.net`, `privatelink.purview.azure.com`, `privatelink.blob.core.windows.net`, `privatelink.queue.core.windows.net`, `privatelink.dfs.core.windows.net` (+ `privatelink.servicebus.windows.net` if Event Hubs). If discovery found a zone with the same name already linked to either VNet, reuse it (one zone per name per VNet).
3. Link every zone to **both** VNets, auto-registration off; blob/queue/dfs links with resolution policy *fallback to internet*, so other storage accounts with PEs elsewhere still resolve.
4. Zone groups on each PE (records follow the PE's life): Databricks `adb-7405607339118600.0` and `pl-auth.<region>`; Purview account; ingestion blob/queue; workspace storage dfs/blob.
5. Check record sets list the PE IPs (`evidence/C-05-records.png`).

**Expected output:** `network.peering.*`, `network.dns.zones[]`, `network.dns.records{}`.

**Rollback:** delete zone groups, links, zones (`az group delete -n rg-pvdbx-dns`), then both peerings. Names fall back to public resolution (the workspace keeps working over its public front end; with back-end PL still on, remove the back-end PE first: A rollback).
