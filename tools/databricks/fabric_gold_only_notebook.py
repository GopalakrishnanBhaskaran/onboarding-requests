# Databricks notebook source
# MAGIC %md
# MAGIC # Fabric sees gold only
# MAGIC
# MAGIC Gives the Fabric service principal read access to **one gold schema** of a Unity Catalog catalog and nothing else. A *Mirrored Azure Databricks catalog* in Fabric built with that service principal can then show gold only. Bronze, silver cleansed and silver curated stay hidden.
# MAGIC
# MAGIC Fabric reads with the credential of its connection and lists only the schemas that credential can see. So the grants below are the real boundary (`docs/FABRIC_GOLD_ONLY.md`, facts F114–F121).
# MAGIC
# MAGIC **How to run**
# MAGIC 1. Attach Unity Catalog compute (DBR 13.3 LTS or later) in the **workspace the Fabric connection points to**.
# MAGIC 2. Run as the **catalog owner** or a member of the owning group. Only the catalog owner can grant `EXTERNAL USE SCHEMA` (F115).
# MAGIC 3. Fill in the widgets, keep `mode = plan` and choose **Run all**. Nothing changes: you get the plan and the findings.
# MAGIC 4. When the plan is right, set `mode = apply`, fill in `approved_by` and choose **Run all** again. The audit runs again after the change.
# MAGIC 5. Optional proof: store the service principal's Entra client secret in a secret scope and fill in `sp_secret_scope` and `sp_secret_key`. The notebook then lists the schemas **as the service principal**, which is exactly what Fabric sees. The secret is never printed.
# MAGIC
# MAGIC | Level | Target rights for the Fabric service principal |
# MAGIC |---|---|
# MAGIC | Catalog | `USE CATALOG` only |
# MAGIC | Gold schema | `USE SCHEMA`, `SELECT`, `EXTERNAL USE SCHEMA` |
# MAGIC | Every other schema and table | nothing |
# MAGIC
# MAGIC Use a service principal **dedicated to Fabric**: `revoke_extra = yes` removes its other rights in this catalog. Grants made to its groups or to `account users` are reported, never changed.

# COMMAND ----------

dbutils.widgets.text("sp_application_id", "", "1 Fabric SP application (client) ID")
dbutils.widgets.text("catalog", "", "2 Catalog")
dbutils.widgets.text("gold_schema", "gold", "3 Gold schema")
dbutils.widgets.dropdown("mode", "plan", ["plan", "apply"], "4 Mode")
dbutils.widgets.dropdown("revoke_extra", "yes", ["yes", "no"], "5 Revoke the SP's other rights here")
dbutils.widgets.text("approved_by", "", "6 Approved by (apply only)")
dbutils.widgets.text("sp_secret_scope", "", "7 Optional: secret scope")
dbutils.widgets.text("sp_secret_key", "", "8 Optional: secret key")
dbutils.widgets.text("sp_tenant_id", "", "9 Optional: Entra tenant ID")
dbutils.widgets.dropdown("fail_on_findings", "no", ["no", "yes"], "10 Fail the run on FAIL (jobs)")

# COMMAND ----------

import datetime
import json
import re

import pandas as pd

GUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
PRIVILEGE = re.compile(r"^[A-Z ]+$")
GOLD_NEEDS = ["USE SCHEMA", "SELECT", "EXTERNAL USE SCHEMA"]
READ_LIKE = {"SELECT", "BROWSE", "EXTERNAL USE SCHEMA", "ALL PRIVILEGES", "MANAGE"}  # rights that expose tables or let the holder widen access
NOT_MIRRORED = {"VIEW", "FOREIGN", "METRIC_VIEW"}  # F118: views, metric views and federated tables are not mirrored
ALWAYS_THERE = "information_schema"                # system schema in every catalog; views only, never mirrored


def widget(name):
    return dbutils.widgets.get(name).strip()


SP = widget("sp_application_id").lower()
CATALOG = widget("catalog").lower()
GOLD = widget("gold_schema").lower()
MODE = widget("mode")
REVOKE_EXTRA = widget("revoke_extra") == "yes"
APPROVED_BY = widget("approved_by")
SECRET_SCOPE = widget("sp_secret_scope")
SECRET_KEY = widget("sp_secret_key")
TENANT_ID = widget("sp_tenant_id")
FAIL_ON_FINDINGS = widget("fail_on_findings") == "yes"

problems = []
if not GUID.match(SP):
    problems.append("sp_application_id must be the service principal's application (client) ID, a GUID.")
if not CATALOG:
    problems.append("catalog is empty.")
if not GOLD:
    problems.append("gold_schema is empty.")
if GOLD == ALWAYS_THERE:
    problems.append("gold_schema cannot be information_schema.")
if MODE == "apply" and not APPROVED_BY:
    problems.append("mode = apply needs approved_by, for example 'Gokul 05 Nov 2026'.")
if bool(SECRET_SCOPE) != bool(SECRET_KEY):
    problems.append("Fill in both sp_secret_scope and sp_secret_key, or neither.")
if TENANT_ID and not GUID.match(TENANT_ID.lower()):
    problems.append("sp_tenant_id must be a GUID.")
if problems:
    raise ValueError("Fix the widgets:\n- " + "\n- ".join(problems))


def q(name):
    """Quote one identifier part with backticks."""
    return "`" + name.replace("`", "``") + "`"


def low(value):
    return (value or "").strip().lower()


def norm(privilege):
    """'EXTERNAL_USE_SCHEMA' or 'external use schema' -> 'EXTERNAL USE SCHEMA'."""
    return " ".join((privilege or "").replace("_", " ").upper().split())


def is_direct(row):
    return (row.get("inherited_from") or "").strip().upper() in ("", "NONE")


def rows(sql, **args):
    return [r.asDict() for r in spark.sql(sql, args=args).collect()]


def show(title, data):
    print(f"\n=== {title}: {len(data)}")
    if data:
        display(pd.DataFrame(data))


def member_of(group):
    try:
        return bool(spark.sql("SELECT is_account_group_member(:g) AS m", args={"g": group}).first()["m"])
    except Exception:
        return False


def workspace_client(**kwargs):
    from databricks.sdk import WorkspaceClient

    return WorkspaceClient(**kwargs)


SP_SQL = q(SP)
CATALOG_SQL = q(CATALOG)
GOLD_SQL = f"{q(CATALOG)}.{q(GOLD)}"
print(f"Mode: {MODE} | catalog: {CATALOG} | gold schema: {GOLD} | service principal: {SP} | revoke_extra: {REVOKE_EXTRA}")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Audit and plan (read-only)

# COMMAND ----------


def lookup_sp():
    """The service principal in this workspace, with its direct groups (best effort, SCIM)."""
    try:
        res = workspace_client().api_client.do(
            "GET",
            "/api/2.0/preview/scim/v2/ServicePrincipals",
            query={"filter": f'applicationId eq "{SP}"', "attributes": "id,applicationId,displayName,active,groups"},
        )
    except Exception as e:
        return {"found": None, "name": "", "groups": None, "error": type(e).__name__}
    found = res.get("Resources") or []
    if not found:
        return {"found": False, "name": "", "groups": [], "error": ""}
    sp = found[0]
    groups = sorted({low(g.get("display")) for g in sp.get("groups") or [] if g.get("display")})
    return {"found": True, "name": sp.get("displayName") or "", "groups": groups, "active": sp.get("active"), "error": ""}


def external_access_enabled():
    try:
        return workspace_client().api_client.do("GET", "/api/2.1/unity-catalog/metastore_summary").get("external_access_enabled")
    except Exception:
        return "unknown"


def schemas_seen_by_sp():
    """List the catalog's schemas signed in AS the service principal (Entra client secret), like Fabric does."""
    secret = dbutils.secrets.get(SECRET_SCOPE, SECRET_KEY)
    try:
        kwargs = {
            "host": "https://" + spark.conf.get("spark.databricks.workspaceUrl"),
            "azure_client_id": SP,
            "azure_client_secret": secret,
            "auth_type": "azure-client-secret",
        }
        if TENANT_ID:
            kwargs["azure_tenant_id"] = TENANT_ID
        client = workspace_client(**kwargs)
        seen, query = [], {"catalog_name": CATALOG, "include_browse": "true", "max_results": 0}
        while True:
            res = client.api_client.do("GET", "/api/2.1/unity-catalog/schemas", query=query)
            seen += [low(s.get("name")) for s in res.get("schemas") or []]
            if not res.get("next_page_token"):
                return sorted(set(seen))
            query["page_token"] = res["next_page_token"]
    finally:
        del secret


def audit():
    findings, plan = [], []

    def add(level, check, detail, fix=""):
        findings.append({"level": level, "check": check, "detail": detail, "fix": fix})

    def change(action, sql, why, status):
        plan.append({"step": len(plan) + 1, "action": action, "sql": sql, "why": why, "status": status})

    def revoke(privilege, on_sql, why):
        p = norm(privilege)
        if not PRIVILEGE.match(p):
            add("FAIL", "Unexpected privilege name", f"{privilege!r} on {on_sql}", "Revoke it by hand.")
            return
        change("REVOKE", f"REVOKE {p} ON {on_sql} FROM {SP_SQL}", why, "remove")

    # --- runner and catalog ---------------------------------------------------------------
    me = spark.sql("SELECT current_user() AS me").first()["me"]
    cats = rows(
        "SELECT catalog_name, catalog_owner FROM system.information_schema.catalogs WHERE lower(catalog_name) = :c",
        c=CATALOG,
    )
    if not cats:
        raise ValueError(f"Catalog '{CATALOG}' is not visible to {me} in this workspace (wrong name, or not bound to this workspace).")
    owner = cats[0]["catalog_owner"]
    runner_is_owner = low(owner) == low(me) or member_of(owner)
    if runner_is_owner:
        add("PASS", "Runner is the catalog owner", f"{me} (owner: {owner})")
    else:
        add("WARN", "Runner is not the catalog owner",
            f"{me}; owner is {owner}. Grants you cannot see are missing from this audit, and apply is refused.",
            "Run as the owner, or as a job whose run-as identity is the owner.")

    schemata = rows(
        "SELECT schema_name, schema_owner FROM system.information_schema.schemata WHERE lower(catalog_name) = :c",
        c=CATALOG,
    )
    if GOLD not in {low(s["schema_name"]) for s in schemata}:
        raise ValueError(f"Schema '{CATALOG}.{GOLD}' not found (or not visible to {me}).")

    # --- metastore switch (F114) --------------------------------------------------------------
    ext = external_access_enabled()
    if ext is True:
        add("PASS", "Metastore external data access", "enabled")
    elif ext is False:
        add("FAIL", "Metastore external data access", "disabled: Fabric mirroring cannot read anything",
            "Metastore admin: Catalog > gear icon > Metastore > Details > External data access.")
    else:
        add("WARN", "Metastore external data access", "could not be read", "Check it in Catalog > gear icon > Metastore > Details.")

    # --- the service principal in this workspace ---------------------------------------------
    sp = lookup_sp()
    if sp["found"] is True:
        add("PASS", "Service principal in this workspace", f"{sp['name'] or SP}, direct groups: {', '.join(sp['groups']) or 'none'}")
    elif sp["found"] is False:
        add("FAIL", "Service principal in this workspace", f"{SP} not found",
            "Add it to this workspace (Settings > Identity and access > Service principals), then run again.")
    else:
        add("WARN", "Service principal in this workspace", f"could not be read ({sp['error']})", "Check it in Settings > Identity and access.")

    # --- grants ---------------------------------------------------------------------------------
    cat_privs = rows(
        "SELECT grantee, catalog_name, privilege_type, inherited_from FROM system.information_schema.catalog_privileges "
        "WHERE lower(catalog_name) = :c OR lower(grantee) = :p",
        c=CATALOG, p=SP,
    )
    schema_privs = rows(
        "SELECT grantee, schema_name, privilege_type, inherited_from FROM system.information_schema.schema_privileges "
        "WHERE lower(catalog_name) = :c",
        c=CATALOG,
    )
    table_privs = rows(
        "SELECT grantee, table_schema AS schema_name, table_name, privilege_type, inherited_from "
        "FROM system.information_schema.table_privileges WHERE lower(table_catalog) = :c",
        c=CATALOG,
    )
    tables = rows(
        "SELECT table_schema AS schema_name, table_name, table_type, data_source_format, table_owner "
        "FROM system.information_schema.tables WHERE lower(table_catalog) = :c AND (lower(table_schema) = :s OR lower(table_owner) = :p)",
        c=CATALOG, s=GOLD, p=SP,
    )

    here = [r for r in cat_privs if low(r["catalog_name"]) == CATALOG]
    sp_cat = [r for r in here if low(r["grantee"]) == SP]
    sp_schema = [r for r in schema_privs if low(r["grantee"]) == SP]
    sp_table = [r for r in table_privs if low(r["grantee"]) == SP]

    # The SP must not own the catalog: an owner sees every schema.
    if low(owner) == SP:
        add("FAIL", "Service principal owns the catalog", "an owner sees every schema, so Fabric would show bronze and silver",
            f"ALTER CATALOG {CATALOG_SQL} OWNER TO `<admin group>`; use a separate service principal for Fabric.")

    # Catalog level: USE CATALOG only.
    direct_cat = {norm(r["privilege_type"]) for r in sp_cat if is_direct(r)}
    change("GRANT", f"GRANT USE CATALOG ON CATALOG {CATALOG_SQL} TO {SP_SQL}", "needed to reach the gold schema",
           "already held" if "USE CATALOG" in direct_cat else "new")
    extra_cat = [r for r in sp_cat if norm(r["privilege_type"]) != "USE CATALOG"]
    revokes_cat = []
    for r in extra_cat:
        p = norm(r["privilege_type"])
        if not is_direct(r):
            add("FAIL", "SP right on the whole catalog", f"{p} inherited from {r['inherited_from']}", "Revoke it where it is granted.")
        elif REVOKE_EXTRA:
            revokes_cat.append(p)
        else:
            add("FAIL", "SP right on the whole catalog", f"{p} covers every schema", "Set revoke_extra = yes, or revoke it by hand.")
    if not extra_cat:
        add("PASS", "SP rights on the catalog", "USE CATALOG only" if "USE CATALOG" in direct_cat else "none yet (plan grants USE CATALOG)")
    elif revokes_cat:
        add("FAIL", "SP rights on the whole catalog", ", ".join(sorted(revokes_cat)) + " cover every schema (bronze and silver too)", "Fixed by the plan.")

    # Gold schema: USE SCHEMA, SELECT, EXTERNAL USE SCHEMA, granted on the schema itself.
    gold_direct = {norm(r["privilege_type"]) for r in sp_schema if low(r["schema_name"]) == GOLD and is_direct(r)}
    for p in GOLD_NEEDS:
        change("GRANT", f"GRANT {p} ON SCHEMA {GOLD_SQL} TO {SP_SQL}", "Fabric reads gold", "already held" if p in gold_direct else "new")
    missing = [p for p in GOLD_NEEDS if p not in gold_direct]
    if missing:
        add("FAIL", "SP rights on gold", "missing on the schema itself: " + ", ".join(missing), "Fixed by the plan.")
    else:
        add("PASS", "SP rights on gold", ", ".join(GOLD_NEEDS))
    for p in sorted(gold_direct - set(GOLD_NEEDS)):
        if REVOKE_EXTRA:
            revoke(p, f"SCHEMA {GOLD_SQL}", "Fabric only reads")
        add("WARN", "Extra SP right on gold", f"{p} is not needed (Fabric only reads)", "Removed by the plan." if REVOKE_EXTRA else "")

    # Every other schema and table: nothing.
    leaks = []
    for r in sp_schema:
        s = low(r["schema_name"])
        if s in (GOLD, ALWAYS_THERE) or not is_direct(r):
            continue  # inherited rows are removed at the catalog level above
        leaks.append(f"{norm(r['privilege_type'])} on {s}")
        if REVOKE_EXTRA:
            revoke(r["privilege_type"], f"SCHEMA {q(CATALOG)}.{q(s)}", "not gold")
    for r in sp_table:
        s = low(r["schema_name"])
        if s == GOLD or not is_direct(r):
            continue
        leaks.append(f"{norm(r['privilege_type'])} on {s}.{low(r['table_name'])}")
        if REVOKE_EXTRA:
            revoke(r["privilege_type"], f"TABLE {q(CATALOG)}.{q(s)}.{q(low(r['table_name']))}", "not gold")
    if leaks:
        add("FAIL", "SP rights outside gold", "; ".join(leaks), "Fixed by the plan." if REVOKE_EXTRA else "Set revoke_extra = yes, or revoke by hand.")
    else:
        add("PASS", "SP rights outside gold", "none")
    for p in sorted(revokes_cat):  # catalog-level revokes go last, after gold has its own grants
        revoke(p, f"CATALOG {CATALOG_SQL}", "covers every schema")

    # Ownership outside gold: an owner always sees its objects.
    owned = [f"schema {low(s['schema_name'])}" for s in schemata
             if low(s["schema_owner"]) == SP and low(s["schema_name"]) not in (GOLD, ALWAYS_THERE)]
    owned += [f"table {low(t['schema_name'])}.{low(t['table_name'])}" for t in tables
              if low(t["table_owner"]) == SP and low(t["schema_name"]) != GOLD]
    if owned:
        add("FAIL", "SP owns objects outside gold", "; ".join(owned),
            "ALTER SCHEMA/TABLE ... OWNER TO `<team group>`. If the SP runs the pipelines, use a separate SP for Fabric.")
    else:
        add("PASS", "SP owns nothing outside gold", "")

    # Groups: the SP also gets every right of its groups and of `account users` (reported, never changed).
    # A read-like right shows tables in Fabric; USE SCHEMA alone lists an empty schema at most, which Fabric hides.
    groups = sp["groups"]
    principals = set(groups or []) | {"account users"}
    can_use = lambda p: p in ("USE SCHEMA", "ALL PRIVILEGES")
    use_everywhere = any(low(r["grantee"]) in principals and can_use(norm(r["privilege_type"])) for r in here)
    usable = {low(r["schema_name"]) for r in schema_privs
              if low(r["grantee"]) in principals and can_use(norm(r["privilege_type"]))} - {GOLD, ALWAYS_THERE}

    def exposes(p, schema):
        """True when this group right makes non-gold tables visible to the SP (schema None = whole catalog)."""
        if p in ("BROWSE", "ALL PRIVILEGES", "MANAGE"):
            return True
        if p in ("SELECT", "EXTERNAL USE SCHEMA"):
            return use_everywhere or (schema in usable if schema else bool(usable))
        return False

    items = []  # (schema or None for the whole catalog, text, exposes)
    for r in here:
        g, p = low(r["grantee"]), norm(r["privilege_type"])
        if g in principals and p != "USE CATALOG":
            items.append((None, f"{g}: {p} on the whole catalog", exposes(p, None)))
    for r in schema_privs:
        g, s, p = low(r["grantee"]), low(r["schema_name"]), norm(r["privilege_type"])
        if g in principals and s not in (GOLD, ALWAYS_THERE) and is_direct(r):
            items.append((s, f"{g}: {p} on {s}", exposes(p, s)))
    for r in table_privs:
        g, s, p = low(r["grantee"]), low(r["schema_name"]), norm(r["privilege_type"])
        if g in principals and s != GOLD and is_direct(r) and p in READ_LIKE:
            items.append((s, f"{g}: {p} on {s}.{low(r['table_name'])}", exposes(p, s)))
    exposed = {s for s, _, e in items if e}
    via = [t for s, t, e in items if e or (s is not None and s in exposed)]
    via_minor = [t for s, t, e in items if not (e or (s is not None and s in exposed))]
    if via:
        add("FAIL", "Rights the SP gets through a group", "; ".join(via),
            "Remove the SP from that group, or narrow the group's grant (check who else relies on it first).")
    else:
        add("PASS", "Rights the SP gets through a group", "nothing readable outside gold")
    if via_minor:
        add("WARN", "Minor rights the SP gets through a group", "; ".join(via_minor),
            "No non-gold table becomes visible from these alone (Fabric hides empty schemas); remove them if you can.")
    if groups is None:
        add("WARN", "SP groups", "could not be read; only `account users` was checked", "Use the optional SP check (secret widgets).")
    elif groups:
        add("INFO", "SP groups", "direct groups only; nested groups are not expanded here", "The optional SP check covers them.")

    # Anyone else who can read non-gold schemas from outside Databricks (F115).
    others = []
    for r in here:
        g = low(r["grantee"])
        if g != SP and g not in principals and norm(r["privilege_type"]) == "EXTERNAL USE SCHEMA":
            others.append(f"{g}: whole catalog")
    for r in schema_privs:
        g, s = low(r["grantee"]), low(r["schema_name"])
        if g != SP and g not in principals and s != GOLD and is_direct(r) and norm(r["privilege_type"]) == "EXTERNAL USE SCHEMA":
            others.append(f"{g}: {s}")
    if others:
        add("WARN", "Other principals with EXTERNAL USE SCHEMA outside gold", "; ".join(others),
            "Confirm each one is intended (another Fabric connection or external engine).")
    else:
        add("PASS", "Other principals with EXTERNAL USE SCHEMA outside gold", "none")

    # Other catalogs this SP can reach: Fabric could mirror them too.
    elsewhere = sorted({low(r["catalog_name"]) for r in cat_privs if low(r["grantee"]) == SP and low(r["catalog_name"]) != CATALOG})
    if elsewhere:
        add("WARN", "SP rights in other catalogs", ", ".join(elsewhere), "Revoke them if Fabric must not mirror those catalogs (this notebook manages one catalog).")

    # Gold objects Fabric will not mirror (F118).
    gold_objs = [t for t in tables if low(t["schema_name"]) == GOLD]
    skipped = [f"{low(t['table_name'])} ({t['table_type']})" for t in gold_objs if (t["table_type"] or "").upper() in NOT_MIRRORED]
    skipped += [f"{low(t['table_name'])} (external {t['data_source_format']})" for t in gold_objs
                if (t["table_type"] or "").upper() == "EXTERNAL" and (t["data_source_format"] or "").upper() != "DELTA"]
    for view in ("row_filters", "column_masks"):
        try:
            hits = rows(
                f"SELECT DISTINCT table_name FROM system.information_schema.{view} "
                "WHERE lower(catalog_name) = :c AND lower(schema_name) = :s",
                c=CATALOG, s=GOLD,
            )
            skipped += [f"{low(h['table_name'])} ({view.replace('_', ' ')[:-1]})" for h in hits]
        except Exception:
            add("WARN", f"Gold {view}", "could not be read on this runtime", "")
    pipelines = [low(t["table_name"]) for t in gold_objs if (t["table_type"] or "").upper() in ("MATERIALIZED_VIEW", "STREAMING_TABLE")]
    if skipped:
        add("WARN", "Gold objects Fabric will not show", "; ".join(sorted(set(skipped))),
            "Views, metric views, federated, non-Delta external and row-filter/column-mask tables are not mirrored; publish a Delta table instead.")
    else:
        add("PASS", "Gold objects Fabric will not show", f"none ({len(gold_objs)} objects in gold)")
    if pipelines:
        add("INFO", "Gold materialized views / streaming tables", ", ".join(sorted(pipelines)),
            "They need TBLPROPERTIES ('pipelines.externalMetadata.enabled' = 'true') or the pipeline setting, then a pipeline run.")

    # Proof: what the SP itself can list (= the Fabric picker).
    if SECRET_SCOPE:
        try:
            seen = schemas_seen_by_sp()
            extra = [s for s in seen if s not in (GOLD, ALWAYS_THERE)]
            if extra:
                add("FAIL", "Schemas the SP can see (signed in as the SP)", ", ".join(extra) + " (Fabric would list these)", "Fix the FAIL lines above, then run again.")
            elif GOLD not in seen:
                add("FAIL", "Schemas the SP can see (signed in as the SP)", "gold is not visible", "Apply the plan; check the SP is in this workspace.")
            else:
                add("PASS", "Schemas the SP can see (signed in as the SP)", ", ".join(s for s in seen if s != ALWAYS_THERE))
        except Exception as e:
            add("WARN", "Schemas the SP can see (signed in as the SP)", f"sign-in or listing failed ({type(e).__name__})",
                "Check the secret, the tenant ID and that the SP is in this workspace.")
    else:
        add("INFO", "Schemas the SP can see (signed in as the SP)", "skipped (no secret widgets)", "")

    return {"findings": findings, "plan": plan, "me": me, "owner": owner, "runner_is_owner": runner_is_owner}


ORDER = {"FAIL": 0, "WARN": 1, "INFO": 2, "PASS": 3}


def report(result, title):
    f = sorted(result["findings"], key=lambda x: ORDER[x["level"]])
    counts = {k: sum(1 for x in f if x["level"] == k) for k in ORDER}
    print(f"\n##### {title}: {counts['FAIL']} FAIL, {counts['WARN']} WARN, {counts['PASS']} PASS")
    show("Findings", f)
    return counts


before = audit()
counts_before = report(before, "Before")
show("Plan (runs in this order on apply; GRANT is idempotent)", before["plan"])

# COMMAND ----------

# MAGIC %md
# MAGIC ## Apply (only when `mode = apply`)

# COMMAND ----------

applied, after, counts_after = [], None, None
if MODE != "apply":
    print("mode = plan: nothing changed. Review the plan, then set mode = apply and approved_by, and choose Run all.")
else:
    if not before["runner_is_owner"]:
        raise RuntimeError(
            f"Refused, nothing changed: only the catalog owner ({before['owner']}) can grant EXTERNAL USE SCHEMA. "
            f"You are {before['me']}. Run as the owner, or as a job whose run-as identity is the owner."
        )
    started = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    print(f"Applying {len(before['plan'])} statements | approved by: {APPROVED_BY} | run by: {before['me']} | {started}")
    for step in before["plan"]:
        try:
            spark.sql(step["sql"])
            applied.append({"step": step["step"], "sql": step["sql"], "result": "done"})
        except Exception as e:
            first = (str(e).strip().splitlines() or [type(e).__name__])[0][:300]
            applied.append({"step": step["step"], "sql": step["sql"], "result": "FAILED: " + first})
            print("Stopped at the first failure; later statements were not run.")
            break
    show("Applied", applied)
    after = audit()
    counts_after = report(after, "After")
    left = [p for p in after["plan"] if p["status"] != "already held"]
    show("Changes still pending", left)

# COMMAND ----------

final = counts_after or counts_before
summary = {
    "mode": MODE,
    "catalog": CATALOG,
    "gold_schema": GOLD,
    "service_principal": SP,
    "approved_by": APPROVED_BY if MODE == "apply" else "",
    "statements_run": len(applied),
    "statements_failed": sum(1 for a in applied if a["result"] != "done"),
    "fail": final["FAIL"],
    "warn": final["WARN"],
    "pass": final["PASS"],
}
print(json.dumps(summary, indent=2))
if FAIL_ON_FINDINGS and (summary["fail"] or summary["statements_failed"]):
    raise RuntimeError(f"{summary['fail']} FAIL line(s), {summary['statements_failed']} failed statement(s). See the findings above.")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Then in Fabric
# MAGIC 1. Create the connection with **Service principal** authentication for this service principal.
# MAGIC 2. Go to **New item → Mirrored Azure Databricks catalog**, pick the catalog and select **gold** only. The picker lists only what this service principal can see. Keep *Automatically sync future catalog changes*: new gold tables arrive by themselves, and other schemas stay invisible.
# MAGIC 3. Proof in the mirror's **SQL analytics endpoint** (expect `gold` only):
# MAGIC    `SELECT DISTINCT TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES;`
# MAGIC 4. Consumers get Viewer or an item share. Admin, Member and Contributor go to the platform team only. Use OneLake security roles for table-level access inside gold.
# MAGIC 5. Don't create Lakehouse ADLS shortcuts straight to Databricks storage: they bypass Unity Catalog.
# MAGIC
# MAGIC Schedule this notebook as a monthly job in `plan` mode with `fail_on_findings = yes` and turn on failure alerts: it changes nothing and fails when drift appears.

# COMMAND ----------

dbutils.notebook.exit(json.dumps(summary))
