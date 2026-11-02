# C DNS (+ peering, dropped) (wave 2, after A2 and B)

**Objective:** private names resolve to the PE IPs inside the transit VNet only.

**Inputs:** `network.pe.*`, `network.transit.vnet_id`, wave 2 approval.

**Locks:** `rg:pvdbx-dns`, each PE while adding its zone group.

**Steps**
1. `rg-pvdbx-dns` (sub-A). Zones: `privatelink.azuredatabricks.net`, `privatelink.purview.azure.com`, `privatelink.blob.core.windows.net`, `privatelink.queue.core.windows.net` (+ `privatelink.servicebus.windows.net` if Event Hubs). Reuse a zone if discovery found one already linked to the transit VNet (one zone per name per VNet).
2. VNet links to `pvdbx-transit-vnet` only, auto-registration off; blob/queue links with resolution policy *fallback to internet* (other storage accounts keep resolving).
3. Private DNS zone groups on each PE (records are created and removed with the PE).
4. Check the records: `adb-7405607339118600.0` → databricks PE IP; `<purview>` → account PE IP; ingestion storage names → their PE IPs.
5. Peering: **not created** (DOC_CHECK D8). If you ask for it: `pvdbx-transit-to-dbx` and `pvdbx-dbx-to-transit` (cross-subscription, both directions), and still no Databricks zone link to the workspace VNet.

**Expected output:** `network.dns.zones[]`, `network.dns.records{}`.

**Rollback:** delete zone groups, links, then zones (`az group delete -n rg-pvdbx-dns`). Resolution falls back to public names.
