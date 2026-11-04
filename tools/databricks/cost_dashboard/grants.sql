-- =============================================================================
-- Grants for the PEST cost dashboard (Unity Catalog, run once per metastore).
-- Who runs it: a metastore admin (or an owner / MANAGE holder of the system catalog).
-- Who needs it (F116):
--   * nobody, if the publisher is both an account admin and a metastore admin
--     and the dashboard is published with shared data permissions (default);
--   * the publisher, if it is a service principal or a user without both roles;
--   * every viewer, if you publish with --individual-permissions.
-- Least privilege: table-level SELECT on the 13 tables the dashboard reads, not
-- whole schemas (system.access also holds audit logs; query.history holds
-- statement text, which Learn suggests sharing through dynamic views).
-- Replace <pest-finops-group> with the group or the service principal
-- application id, then run in the SQL editor on the dashboard's warehouse.
-- =============================================================================
GRANT USE CATALOG ON CATALOG system TO `<pest-finops-group>`;

GRANT USE SCHEMA ON SCHEMA system.billing  TO `<pest-finops-group>`;
GRANT USE SCHEMA ON SCHEMA system.access   TO `<pest-finops-group>`;
GRANT USE SCHEMA ON SCHEMA system.compute  TO `<pest-finops-group>`;
GRANT USE SCHEMA ON SCHEMA system.lakeflow TO `<pest-finops-group>`;
GRANT USE SCHEMA ON SCHEMA system.query    TO `<pest-finops-group>`;

GRANT SELECT ON TABLE system.billing.usage                   TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.billing.list_prices             TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.access.workspaces_latest        TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.compute.clusters                TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.compute.node_timeline           TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.compute.warehouses              TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.compute.warehouse_events        TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.lakeflow.jobs                   TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.lakeflow.pipelines              TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.lakeflow.job_run_timeline       TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.lakeflow.job_task_run_timeline  TO `<pest-finops-group>`;
GRANT SELECT ON TABLE system.query.history                   TO `<pest-finops-group>`;

-- Check (read-only):
--   SHOW SCHEMAS IN system;                          -- billing, access, compute, lakeflow, query must be listed
--   SHOW GRANTS `<pest-finops-group>` ON SCHEMA system.billing;
-- Remove: the same statements with REVOKE ... FROM instead of GRANT ... TO.
