# S Purview source and scans (wave 3)

**Objective:** test connection passes over the SHIR; 2 scans Completed; the table is in the Data Map with its columns.

**Inputs:** `discovery.databricks.metastore_id`, `identity.warehouse.http_path`, `scans.ir.name`, workspace URL.

**Steps** (Purview portal on your device, `tools/pw/browser.sh S …`)
1. Start the warehouse (must be running for test and scan).
2. Data Map → Data sources → Register → **Azure Databricks Unity Catalog** → name `pvdbx-dbx-uc`, **Metastore ID**, collection.
3. New scan `pvdbx-scan1-default`: IR `pvdbx-k8s-shir`; credential **Managed Identity** (system-assigned); workspace URL; HTTP path; lineage **off**.
4. **Test connection** → must pass. On failure, log error + fix in `errors[]`, check in order: DNS from the SHIR, PE NSG, IP list (step F8), SP assignment, entitlements, CAN USE, grants, warehouse running.
5. Scope: Advanced mode (preview) include `dbw_pmo_pkt2ofpqmlf3e/default`; if not offered, the catalog. Run once → wait for **Completed**. Record run id, status, assets discovered, duration.
6. Scan 2 `pvdbx-scan2-catalog`: whole catalog `dbw_pmo_pkt2ofpqmlf3e`, run once → Completed (or a rerun of scan 1).
7. Unified Catalog / Data Map search `sample_access_test` → asset with its columns. Screenshot.
8. Then F step 9 (Purview firewall) and re-run test connection.

**Expected output:** `scans.{source, test_connection, runs[]: {name, run_id, status, assets, duration}, asset}`.

**Rollback:** delete the scans and the source (assets stay unless deleted).
