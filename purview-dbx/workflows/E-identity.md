# E Identity and permissions (wave 1)

**Objective:** the Purview system-assigned MI can sign in to the workspace and read metadata of the scan target, nothing more.

**Inputs:** `discovery.purview.mi_app_id`, `discovery.databricks.{warehouses,metastore_id}`, `inputs.scan_*`. You must be workspace admin (and account admin for the account steps).

**Locks:** `workspace` (shared with A/F: E only touches identity, A only network; take the lock per step).

**Steps** (browser on your device with `tools/pw/browser.sh E …`; the REST equivalent verifies each step)
1. Account console → User management → Service principals → Add → **Microsoft Entra ID managed** → application id = Purview MI app id, name `purview-<account>-mi`. (REST: `POST /api/2.0/accounts/{acct}/scim/v2/ServicePrincipals`.)
2. Account console → Workspaces → 7405607339118600 → Permissions → add the SP as **User**. (REST: `PUT …/workspaces/{id}/permissionassignments/principals/{sp}` `{"permissions":["USER"]}`.)
3. Workspace → Settings → Identity and access → the SP has **Workspace access** and **Databricks SQL access** entitlements.
4. Warehouse (existing serverless/pro, else create `pvdbx-scan-wh` 2X-Small serverless, auto-stop 10 min) → Permissions → SP **Can use**. Record the HTTP path.
5. SQL editor (as you):
   ```sql
   GRANT USE CATALOG ON CATALOG dbw_pmo_pkt2ofpqmlf3e TO `<purview-mi-app-id>`;
   GRANT USE SCHEMA ON SCHEMA dbw_pmo_pkt2ofpqmlf3e.default TO `<purview-mi-app-id>`;
   GRANT SELECT ON TABLE dbw_pmo_pkt2ofpqmlf3e.default.sample_access_test TO `<purview-mi-app-id>`;
   ```
   The docs ask for SELECT (not BROWSE). Lineage stays off, so no `system.access` grants. Scan 2 (whole catalog) still sees only objects with these grants: add `SELECT ON SCHEMA …default` only if you want every table of the schema.
6. Verify with effective-permissions REST; screenshots `evidence/E-0N-*.png`.
7. Azure RBAC: none for the MI (P1). You need Purview *Data source admin* + *Data reader* on the collection to register and scan.

**Expected output:** `identity.{sp_id, workspace_assignment, entitlements, warehouse:{id,http_path,can_use}, grants[]}`.

**Rollback:** `REVOKE` the three grants; remove CAN USE; remove the workspace assignment; delete the SP from the account.
