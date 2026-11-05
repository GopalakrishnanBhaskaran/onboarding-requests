# Fabric sees gold only (Databricks → Fabric mirroring)

Notebook: `tools/databricks/fabric_gold_only_notebook.py`. Separate from the PMO onboarding app: no Azure resource, no cost.
Written 5 Nov 2026. Facts F114–F121 in `docs/VERIFIED_FACTS.md`, read on Microsoft Learn that day.

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

`EXTERNAL USE SCHEMA` is never part of `ALL PRIVILEGES`, and only the catalog owner can grant it (F115). Granting it on the catalog would cover every schema, so the notebook only grants it on gold.

## Run it

1. **Import.** In Databricks, go to Workspace → Import → File and pick the `.py` file. It opens as a notebook. A Git folder on this repo works too.
2. **Compute.** Use Unity Catalog compute, DBR 13.3 LTS or later, in the **workspace the Fabric connection points to**.
3. **Run as the catalog owner** (or a member of the owning group). Anyone else sees only part of the grants, and apply is refused.
4. Fill in the widgets:

   | Widget | Value |
   |---|---|
   | `sp_application_id` | The Fabric service principal's **application (client) ID** |
   | `catalog` / `gold_schema` | For example `prod` / `gold` |
   | `mode` | `plan` first (read-only), then `apply` |
   | `revoke_extra` | `yes`: removes the service principal's other rights in this catalog. Only use a service principal dedicated to Fabric. |
   | `approved_by` | Needed for `apply`, for example `Gokul 05 Nov 2026` |
   | `sp_secret_scope` / `sp_secret_key` / `sp_tenant_id` | Optional proof: signs in **as the service principal** (Entra client secret) and lists the schemas it can see, which is what the Fabric picker shows. The secret is never printed. |
   | `fail_on_findings` | `yes` for a scheduled job |

5. **Run all** in `plan` mode. Read the findings (FAIL / WARN / INFO / PASS) and the plan:
   - GRANTs first; GRANT is idempotent;
   - then REVOKEs on tables and schemas;
   - catalog-level REVOKEs last, so gold never loses access during the change.
6. Set `mode = apply` and fill in `approved_by`, then **Run all**. The notebook:
   - runs the plan in order and stops at the first failure;
   - prints each statement and its result;
   - audits again.

   Save the run output: it is the change record.

## What it checks

- Metastore *External data access* is on (F114).
- The service principal is in this workspace and does not own the catalog, or anything outside gold.
- The service principal's own rights on the catalog, on gold and outside gold.
- Rights it gets **through its groups or `account users`**. These are reported, never changed: other people may rely on them. A right only counts as FAIL when it makes a non-gold table visible; `USE SCHEMA` alone is a WARN, because Fabric hides empty schemas.
- Other principals that hold `EXTERNAL USE SCHEMA` outside gold (WARN).
- Other catalogs the service principal can reach (WARN): Fabric could mirror those too.
- Gold objects Fabric will not show (F118): views, metric views, federated tables, non-Delta external tables, and tables with row filters or column masks. Materialized views and streaming tables need `pipelines.externalMetadata.enabled`.

## Monthly drift check

Create a job with the notebook as its task:
- widget values as job parameters;
- `mode = plan`, `fail_on_findings = yes`;
- run as the catalog owner;
- monthly schedule, with failure notifications on.

The job changes nothing. It fails as soon as a non-gold right appears.

## Then in Fabric

1. Create a connection with **Service principal** authentication for the same service principal.
2. **New item → Mirrored Azure Databricks catalog**: pick the catalog, select gold, keep auto sync on.
3. In the SQL analytics endpoint, `SELECT DISTINCT TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES;` should return gold only.
4. Access inside Fabric:
   - consumers get Viewer or an item share;
   - Admin, Member and Contributor are for the platform team only (OneLake security doesn't restrict them, F119);
   - use OneLake security roles for table-level access inside gold.
5. Don't create Lakehouse ADLS shortcuts straight to Databricks storage: they bypass Unity Catalog.

## Rollback

Take the *Applied* table from the run output:
- run each `REVOKE … FROM` as `GRANT … TO`;
- revoke each **new** GRANT.

No data moves either way.

## Evidence

- Offline: `python3 tools/databricks/test_fabric_gold_only.py` runs the notebook against stand-ins for Spark, `dbutils` and the SDK. It covers plan, apply, refusals, a failing statement, drift and the sign-in-as-SP check.
- **Not yet run on a live workspace.** The first `plan` run is the proof. Open points are listed in F120 and F121.
