# 00 Discovery (orchestrator, read-only)

**Objective:** facts for the design; no change anywhere.

**Inputs:** `inputs.*` in `state/state.json`; Azure CLI signed in as you (you sign in; tools never type credentials).

**Steps**
1. On your device: `bash purview-dbx/tools/my_ip.sh` → shows your public IPv4, warns if it looks dynamic or if two services see different IPs. Confirm it in chat, then `bash purview-dbx/tools/my_ip.sh --confirm`.
2. `bash purview-dbx/tools/discover.sh [--purview <account>]`:
   - tenant of the signed-in user and of every subscription;
   - workspace by id 7405607339118600 (Resource Graph): sub, RG, region, SKU, VNet injection, SCC, public access, NSG rule setting, managed RG and its resources, storage account, storage firewall, Load Balancer, existing PEs;
   - Purview account: MI object id → application id (Graph), public access, ingestion storage, Event Hubs, PEs;
   - VNets (address spaces), private DNS zones, PEs, NAT/Bastion/AKS already there;
   - Databricks REST: caller admin?, metastore id/name/region, catalog, table + columns, warehouses (type, state, HTTP path), IP list state, running clusters, Purview SP present?, its effective grants;
   - account REST: workspace record, account IP lists, SP (401/403 = not account admin).
3. Read `report/01-discovery.md`; every FAIL check becomes a design note or a question.
4. Re-price `WAVE_PLAN.md` for the real region.

**Expected output:** `discovery.*`, `report/01-discovery.md`, `logs/discover.log`.

**Rollback:** none (read-only).
