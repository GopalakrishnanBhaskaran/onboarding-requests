# Fabric sees gold only (Databricks → Fabric mirroring)

Notebook: `tools/databricks/fabric_gold_only_notebook.py`. Separate from the PMO onboarding app: no Azure resource, no cost.
Written 5 Nov 2026. Facts F114–F123 in `docs/VERIFIED_FACTS.md`, read on Microsoft Learn that day. Reviewed by an independent agent the same day; its findings are fixed (see *Evidence*).

## Why grants are the boundary (F116, F117)

- A *Mirrored Azure Databricks catalog* reads with **the credential of its Fabric connection**. Fabric lists only what that credential can see in Unity Catalog.
- *Automatically sync future catalog changes* is on by default and also adds **new schemas**. So unticking schemas in Fabric is not a control on its own.
- Unity Catalog permissions are **not** copied into Fabric. Anyone with Read on the Fabric item sees all its tables.

So the Fabric service principal gets exactly this, and nothing else:

| Level | Rights |
|---|---|
| Catalog | `USE CATALOG` |
| Gold schema | `USE SCHEMA`, `SELECT`, `EXTERNAL USE SCHEMA` |
| Bronze, silver cleansed, silver curated, every other schema and table | none |
| Ownership | none: not the catalog, the metastore, or any schema or table, directly or through a group (owners see everything, F122) |

`EXTERNAL USE SCHEMA` is never part of `ALL PRIVILEGES`, and only the catalog owner can grant it (F115). Granting it on the catalog would cover every schema, so the notebook only grants it on gold.

## Before you change anything: is the SP used only by Fabric?

Revoking catalog-level rights removes them for **every** tool that uses the service principal. Check its last 30 days in the audit log (F123) as a user who can read `system.access.audit`:

```sql
SELECT workspace_id, service_name, action_name, user_agent,
       COUNT(*) AS events, MIN(event_date) AS first_seen, MAX(event_date) AS last_seen
FROM system.access.audit
WHERE event_date >= current_date() - INTERVAL 30 DAYS
  AND (user_identity.email = '<sp-application-id>' OR identity_metadata.run_as = '<sp-application-id>')
GROUP BY ALL ORDER BY events DESC LIMIT 200;
```

- Fabric mirroring shows as `fabric/… (service=workload-adb …)`. Its table reads (`generateTemporaryTableCredential`) have an empty user agent.
- Any other user agent means another tool depends on the service principal. In that case, create a separate service principal for Fabric.

To see which non-gold tables Fabric actually reads (these stop working after the change), filter on `action_name = 'generateTemporaryTableCredential'` and `request_params['table_full_name']`. Tell the owners of anything that uses them first.

## Run it

1. **Import.** In Databricks, go to Workspace → Import → File and pick the `.py` file. It opens as a notebook.
2. **Compute.** Use Unity Catalog compute, DBR 13.3 LTS or later, in the **workspace the Fabric connection points to**.
3. **Run as a member of the catalog-owning group.** Anyone else sees only part of the grants, so the audit FAILs and apply is refused.
4. Fill in the widgets:

   | Widget | Value |
   |---|---|
   | `sp_application_id` | The Fabric service principal's **application (client) ID** |
   | `catalog` / `gold_schema` | For example `prod` / `gold` |
   | `mode` | `plan` first (read-only), then `apply` |
   | `revoke_extra` | Default `no`: only GRANTs. `yes`: also removes the service principal's other rights in this catalog. Only use `yes` for a service principal dedicated to Fabric. |
   | `approved_by` | Needed for `apply`, for example `Gokul 05 Nov 2026` |
   | `confirm_plan` | Needed for `apply`: the **plan ID** printed by the plan run |
   | `sp_secret_scope` / `sp_secret_key` / `sp_tenant_id` | Optional proof: signs in **as the service principal** (Entra client secret) and lists the schemas it can see. That is what the Fabric picker shows, including nested groups. The secret is never printed. Limit who can read that secret scope. |
   | `fail_on_findings` | `yes` for a scheduled job |

5. **Run all** in `plan` mode. Read the findings (FAIL / WARN / INFO / PASS), the plan and the **plan ID**. Plan order:
   - `REVOKE ALL PRIVILEGES` first: it also removes the privileges it implies, so it must come before the GRANTs (F122);
   - then the GRANTs;
   - then the other REVOKEs on gold extras, schemas and tables;
   - catalog-level REVOKEs last.
6. Set `mode = apply`, `approved_by` and `confirm_plan` = the plan ID, then **Run all**.
   - If any grant changed since the plan run, the plan ID changes and apply is refused. The same happens on a later Run all with stale widgets.
   - Apply is also refused if the runner isn't the catalog owner, or if the service principal owns objects in the catalog: that makes it a pipeline identity.
   - The notebook runs the plan in order, stops at the first failure (and then fails the run), and audits again.
   - Save the output: it is the change record.

## What it checks

- Metastore *External data access* is on (F114).
- The service principal: directly assigned to this workspace or not (WARN, since access through a group is possible), and active.
- The service principal's own rights on the catalog, on gold and outside gold. Write or admin rights are a WARN: a Fabric connection only reads.
- **Ownership** of the catalog, the metastore and the non-gold schemas and tables, by the service principal, its direct groups or `account users` (FAIL).
- **Metastore-level rights** of the same principals. `READ METADATA`, `BROWSE` and `MANAGE` reach every catalog (FAIL).
- Rights it gets **through its direct groups or `account users`**. These are reported, never changed: other people may rely on them.
  - A right counts as FAIL when it makes a non-gold table visible.
  - `USE SCHEMA` alone is a WARN, because Fabric hides empty schemas.
  - Nested groups can't be read through the workspace API (F122). Unless the sign-in-as-SP check passes, a WARN says so.
- Other principals that hold `EXTERNAL USE SCHEMA` outside gold (WARN). Treat this as a go-live blocker until you know who they are.
- Other catalogs the service principal can reach, as far as the runner can see (WARN): Fabric could mirror those too.
- Gold objects Fabric will not show (F118): views, metric views, federated tables, non-Delta external tables, and tables with row filters or column masks. ABAC row and mask policies are not checked. Materialized views and streaming tables need `pipelines.externalMetadata.enabled`.
- Not covered: volume, function and model grants outside gold (harmless without `USE SCHEMA`).

## Monthly drift check

Create a job with the notebook as its task:
- widget values as job parameters;
- `mode = plan`, `fail_on_findings = yes`;
- monthly schedule, with failure notifications on;
- run as a **user or service principal that is a member of the catalog-owning group**. A job can't run as a group.

The job changes nothing and fails as soon as a non-gold right or ownership appears.

## Then in Fabric

1. Create a connection with **Service principal** authentication for the same service principal.
2. **New item → Mirrored Azure Databricks catalog**: pick the catalog, select gold, keep auto sync on. For an existing mirror, unselect the non-gold schemas **before** applying the REVOKEs.
3. In the SQL analytics endpoint, `SELECT DISTINCT TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES;` should return gold only. This is also the final proof for nested groups.
4. Access inside Fabric:
   - consumers get Viewer or an item share;
   - Admin, Member and Contributor are for the platform team only (OneLake security doesn't restrict them, F119);
   - use OneLake security roles for table-level access inside gold.
5. Storage:
   - don't create Lakehouse ADLS shortcuts straight to Databricks storage: they bypass Unity Catalog;
   - if the storage is behind a firewall, the mirror's ADLS connection and the workspace identity's role must be scoped to **gold's folder only**, not to a container or account that also holds bronze and silver (F116).

## Rollback

Take the *Applied* table from the run output:
- run each `REVOKE … FROM` as `GRANT … TO`;
- revoke each **new** GRANT.

No data moves either way.

## Evidence

- **Offline:** `python3 tools/databricks/test_fabric_gold_only.py` runs the notebook against stand-ins for Spark, `dbutils` and the SDK; `REVOKE ALL PRIVILEGES` follows Learn's rule. It covers:
  - plan and apply, including the plan ID and drift refusal;
  - `ALL PRIVILEGES` ordering;
  - ownership through groups and the metastore;
  - `READ METADATA` and metastore grants;
  - non-owner and pipeline-SP refusals;
  - a failing statement;
  - the sign-in-as-SP check.

  Breaking the `ALL PRIVILEGES` order, the group-ownership check, the plan-ID check or the failed-apply exit makes it fail.
- **Live, 5 Nov 2026:** plan runs against the prod catalog by a member of the owning group.
  - Parameter markers, `is_account_group_member` and the information schema queries worked.
  - The system `row_filters` / `column_masks` views could not be read; the notebook now falls back to the catalog's own views.
  - The run found the Fabric service principal holding `USE SCHEMA`, `SELECT` and `EXTERNAL USE SCHEMA` on the whole catalog. The audit log showed only Fabric uses it.
  - Apply not yet run at the time of writing.
- **Independent review, 5 Nov 2026:** 3 high, 6 medium and 9 low findings.
  - Fixed: ownership through groups and the metastore, `ALL PRIVILEGES` ordering, plan-bound apply, default `revoke_extra = no`, workspace-assignment WARN, non-owner FAIL, `READ METADATA` and metastore grants, a failed apply always failing, storage scope, error classes, duplicate REVOKEs, wording.
  - Not changed: `lower()` on identifier columns. The queries are proven live; on a very large catalog the `table_privileges` read may be slow.
