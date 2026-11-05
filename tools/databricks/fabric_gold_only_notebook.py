# Databricks notebook source
# MAGIC %md
# MAGIC # Fabric sees gold only
# MAGIC
# MAGIC Gives the Fabric service principal read access to **one gold schema** of a Unity Catalog catalog and nothing else. A *Mirrored Azure Databricks catalog* in Fabric built with that service principal can then show gold only. Bronze, silver cleansed and silver curated stay hidden.
# MAGIC
# MAGIC Fabric reads with the credential of its connection and lists only the schemas that credential can see. So the grants below are the real boundary (`docs/FABRIC_GOLD_ONLY.md`, facts F114–F122).
# MAGIC
# MAGIC **How to run**
# MAGIC 1. Attach Unity Catalog compute (DBR 13.3 LTS or later) in the **workspace the Fabric connection points to**.
# MAGIC 2. Run as the **catalog owner**, or a member of the owning group. Only the catalog owner can grant `EXTERNAL USE SCHEMA` (F115), and only the owner sees every grant.
# MAGIC 3. Fill in the widgets, keep `mode = plan` and choose **Run all**. Nothing changes: you get the findings, the plan and a **plan ID**.
# MAGIC 4. When the plan is right, set `mode = apply`, `approved_by` and `confirm_plan` = that plan ID, then choose **Run all** again.
# MAGIC    - If anything changed since the plan, the plan ID changes and apply is refused.
# MAGIC    - The audit runs again after the change.
# MAGIC 5. Optional proof: store the service principal's Entra client secret in a secret scope and fill in `sp_secret_scope` and `sp_secret_key`.
# MAGIC    - The notebook then lists the schemas **as the service principal**, which is exactly what Fabric sees. It also covers nested groups.
# MAGIC    - The secret is never printed. Limit who can read that secret scope.
# MAGIC
# MAGIC | Level | Target rights for the Fabric service principal |
# MAGIC |---|---|
# MAGIC | Catalog | `USE CATALOG` only |
# MAGIC | Gold schema | `USE SCHEMA`, `SELECT`, `EXTERNAL USE SCHEMA` |
# MAGIC | Every other schema and table | nothing |
# MAGIC
# MAGIC Use a service principal **dedicated to Fabric**.
# MAGIC - `revoke_extra = yes` removes its other rights in this catalog.
# MAGIC - Apply is refused if the service principal owns objects here, because then it is a pipeline identity, not a Fabric one.
# MAGIC - Grants made to its groups or to `account users` are reported, never changed.

# COMMAND ----------

dbutils.widgets.text("sp_application_id", "", "01 Fabric SP application (client) ID")
dbutils.widgets.text("catalog", "", "02 Catalog")
dbutils.widgets.text("gold_schema", "gold", "03 Gold schema")
dbutils.widgets.dropdown("mode", "plan", ["plan", "apply"], "04 Mode")
dbutils.widgets.dropdown("revoke_extra", "no", ["no", "yes"], "05 Revoke the SP's other rights here")
dbutils.widgets.text("approved_by", "", "06 Approved by (apply only)")
dbutils.widgets.text("confirm_plan", "", "07 Plan ID to apply (from the plan run)")
dbutils.widgets.text("sp_secret_scope", "", "08 Optional: secret scope")
dbutils.widgets.text("sp_secret_key", "", "09 Optional: secret key")
dbutils.widgets.text("sp_tenant_id", "", "10 Optional: Entra tenant ID")
dbutils.widgets.dropdown("fail_on_findings", "no", ["no", "yes"], "11 Fail the run on FAIL (jobs)")

# COMMAND ----------

import datetime
import hashlib
import json
import re

import pandas as pd

GUID = re.compile(r"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")
PRIVILEGE = re.compile(r"^[A-Z ]+$")
GOLD_NEEDS = ["USE SCHEMA", "SELECT", "EXTERNAL USE SCHEMA"]
# Rights that show tables or metadata to the holder, or let it widen its own access.
READ_LIKE = {"SELECT", "BROWSE", "READ METADATA", "EXTERNAL USE SCHEMA", "ALL PRIVILEGES", "MANAGE"}
SELF_EXPOSING = {"BROWSE", "READ METADATA", "ALL PRIVILEGES", "MANAGE"}  # need no USE SCHEMA to expose
WRITE_LIKE = {"MODIFY", "CREATE TABLE", "CREATE SCHEMA", "CREATE FUNCTION", "CREATE VOLUME", "CREATE MODEL",
              "CREATE MATERIALIZED VIEW", "WRITE VOLUME", "APPLY TAG", "REFRESH", "MANAGE", "ALL PRIVILEGES"}
NOT_MIRRORED = {"VIEW", "FOREIGN", "METRIC_VIEW"}  # F118: views, metric views and federated tables are not mirrored
ALWAYS_THERE = "information_schema"                # system schema in every catalog; views only, never mirrored


def widget(name):
    return dbutils.widgets.get(name).strip()


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


def err(e):
    """Error class when Spark gives one (e.g. TABLE_OR_VIEW_NOT_FOUND), else the exception type. Never the message."""
    for name in ("getCondition", "getErrorClass"):
        f = getattr(e, name, None)
        if callable(f):
            try:
                value = f()
                if value:
                    return str(value)
            except Exception:
                pass
    return type(e).__name__


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


def workspace_host():
    try:
        return workspace_client().config.host
    except Exception:
        return "https://" + spark.conf.get("spark.databricks.workspaceUrl")


def current_inputs():
    """The widget values the audit and its plan depend on."""
    return [widget("sp_application_id").lower(), widget("catalog").lower(), widget("gold_schema").lower(), widget("revoke_extra")]


# Inputs. Helpers above always load, so a failed check below leaves a clear message, never a NameError.
INPUTS_OK = False
SP = widget("sp_application_id").lower()
CATALOG = widget("catalog").lower()
GOLD = widget("gold_schema").lower()
MODE = widget("mode")
REVOKE_EXTRA = widget("revoke_extra") == "yes"
APPROVED_BY = widget("approved_by")
CONFIRM_PLAN = widget("confirm_plan").lower()
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
if MODE == "apply" and not CONFIRM_PLAN:
    problems.append("mode = apply needs confirm_plan: the plan ID printed by the plan run.")
if bool(SECRET_SCOPE) != bool(SECRET_KEY):
    problems.append("Fill in both sp_secret_scope and sp_secret_key, or neither.")
if TENANT_ID and not GUID.match(TENANT_ID.lower()):
    problems.append("sp_tenant_id must be a GUID.")
if problems:
    raise ValueError("Fix the widgets:\n- " + "\n- ".join(problems))


SP_SQL = q(SP)
CATALOG_SQL = q(CATALOG)
GOLD_SQL = f"{q(CATALOG)}.{q(GOLD)}"
INPUTS = current_inputs()
INPUTS_OK = True
print(f"Mode: {MODE} | catalog: {CATALOG} | gold schema: {GOLD} | service principal: {SP} | revoke_extra: {REVOKE_EXTRA}")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Audit and plan (read-only)

# COMMAND ----------


before = None
if not globals().get("INPUTS_OK"):
    raise RuntimeError("Choose Run all from the top: the widget check (third cell) has not passed yet. "
                       "Scroll up for 'Fix the widgets', fill them in, then Run all again.")


def lookup_sp():
    """The service principal as this workspace's SCIM API knows it, with its direct groups (best effort).

    Under identity federation and automatic identity management an SP can have access through a group and still be
    missing here, and nested groups are never listed (F122). groups = None means 'unknown'.
    """
    try:
        res = workspace_client().api_client.do(
            "GET",
            "/api/2.0/preview/scim/v2/ServicePrincipals",
            query={"filter": f'applicationId eq "{SP}"', "attributes": "id,applicationId,displayName,active,groups"},
        )
    except Exception as e:
        return {"found": None, "name": "", "groups": None, "active": None, "error": err(e)}
    found = res.get("Resources") or []
    if not found:
        return {"found": False, "name": "", "groups": None, "active": None, "error": ""}
    sp = found[0]
    groups = sorted({low(g.get("display")) for g in sp.get("groups") or [] if g.get("display")})
    return {"found": True, "name": sp.get("displayName") or "", "groups": groups, "active": sp.get("active"), "error": ""}


def metastore_summary():
    try:
        return workspace_client().api_client.do("GET", "/api/2.1/unity-catalog/metastore_summary")
    except Exception as e:
        return {"error": err(e)}


def schemas_seen_by_sp():
    """List the catalog's schemas signed in AS the service principal (Entra client secret), like Fabric does."""
    secret = dbutils.secrets.get(SECRET_SCOPE, SECRET_KEY)
    try:
        kwargs = {"host": workspace_host(), "azure_client_id": SP, "azure_client_secret": secret, "auth_type": "azure-client-secret"}
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
    findings, grants, revokes, planned = [], [], [], set()

    def add(level, check, detail, fix=""):
        findings.append({"level": level, "check": check, "detail": detail, "fix": fix})

    def grant(p, on_sql, why, status):
        sql = f"GRANT {p} ON {on_sql} TO {SP_SQL}"
        if sql not in planned:
            planned.add(sql)
            grants.append({"action": "GRANT", "sql": sql, "why": why, "status": status})

    def revoke(privilege, on_sql, why):
        p = norm(privilege)
        if not PRIVILEGE.match(p):
            add("FAIL", "Unexpected privilege name", f"{privilege!r} on {on_sql}", "Revoke it by hand.")
            return
        sql = f"REVOKE {p} ON {on_sql} FROM {SP_SQL}"
        if sql not in planned:
            planned.add(sql)
            revokes.append({"action": "REVOKE", "sql": sql, "why": why, "status": "remove", "all": p == "ALL PRIVILEGES"})

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
        add("FAIL", "Runner is not the catalog owner",
            f"{me}; owner is {owner}. This audit only sees the grants you can see, so its PASS lines can't be trusted, and apply is refused.",
            "Run as a member of the owning group (a job's run-as must be a user or service principal in that group).")

    schemata = rows(
        "SELECT schema_name, schema_owner FROM system.information_schema.schemata WHERE lower(catalog_name) = :c",
        c=CATALOG,
    )
    if GOLD not in {low(s["schema_name"]) for s in schemata}:
        raise ValueError(f"Schema '{CATALOG}.{GOLD}' not found (or not visible to {me}).")

    # --- metastore switch (F114) and owner -----------------------------------------------------
    ms = metastore_summary()
    ext = ms.get("external_access_enabled")
    if ext is True:
        add("PASS", "Metastore external data access", "enabled")
    elif ext is False:
        add("FAIL", "Metastore external data access", "disabled: Fabric mirroring cannot read anything",
            "Metastore admin: Catalog > gear icon > Metastore > Details > External data access.")
    else:
        add("WARN", "Metastore external data access", f"could not be read ({ms.get('error') or 'no value'})",
            "Check it in Catalog > gear icon > Metastore > Details.")

    # --- the service principal in this workspace ---------------------------------------------
    sp = lookup_sp()
    if sp["found"] is True and sp["active"] is False:
        add("FAIL", "Service principal in this workspace", f"{sp['name'] or SP} is deactivated", "Activate it, or use the right SP.")
    elif sp["found"] is True:
        add("PASS", "Service principal in this workspace", f"{sp['name'] or SP}, direct groups: {', '.join(sp['groups']) or 'none'}")
    elif sp["found"] is False:
        add("WARN", "Service principal in this workspace", f"{SP} is not directly assigned to this workspace",
            "It may still have access through a group. Fabric needs it to be a workspace user, directly or through a group "
            "(account console > Workspaces > this workspace > Permissions). The sign-in-as-SP check settles it.")
    else:
        add("WARN", "Service principal in this workspace", f"could not be read ({sp['error']})", "Check it in Settings > Identity and access.")

    groups = sp["groups"]                               # None = unknown
    principals = set(groups or []) | {"account users"}  # what the SP inherits from (direct groups only)
    owners_to_check = principals | {SP}

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
    owner_args = {f"o{i}": o for i, o in enumerate(sorted(owners_to_check))}
    tables = rows(
        "SELECT table_schema AS schema_name, table_name, table_type, data_source_format, table_owner "
        "FROM system.information_schema.tables WHERE lower(table_catalog) = :c AND (lower(table_schema) = :s OR lower(table_owner) IN ("
        + ", ".join(f":{k}" for k in owner_args) + "))",
        c=CATALOG, s=GOLD, **owner_args,
    )

    here = [r for r in cat_privs if low(r["catalog_name"]) == CATALOG]
    sp_cat = [r for r in here if low(r["grantee"]) == SP]
    sp_schema = [r for r in schema_privs if low(r["grantee"]) == SP]
    sp_table = [r for r in table_privs if low(r["grantee"]) == SP]

    # Catalog level: USE CATALOG only.
    direct_cat = {norm(r["privilege_type"]) for r in sp_cat if is_direct(r)}
    status = "re-grant" if "ALL PRIVILEGES" in direct_cat else ("already held" if "USE CATALOG" in direct_cat else "new")
    grant("USE CATALOG", f"CATALOG {CATALOG_SQL}", "needed to reach the gold schema", status)
    if "USE CATALOG" not in direct_cat:
        add("FAIL", "SP USE CATALOG", "not granted directly on the catalog", "Fixed by the plan.")
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
        add("PASS", "SP rights on the catalog", "nothing beyond USE CATALOG")
    elif revokes_cat:
        add("FAIL", "SP rights on the whole catalog", ", ".join(sorted(revokes_cat)) + " cover every schema (bronze and silver too)", "Fixed by the plan.")

    # Gold schema: USE SCHEMA, SELECT, EXTERNAL USE SCHEMA, granted on the schema itself.
    gold_direct = {norm(r["privilege_type"]) for r in sp_schema if low(r["schema_name"]) == GOLD and is_direct(r)}
    for p in GOLD_NEEDS:
        status = "already held" if p in gold_direct else "new"
        if "ALL PRIVILEGES" in gold_direct and p != "EXTERNAL USE SCHEMA":
            status = "re-grant"
        grant(p, f"SCHEMA {GOLD_SQL}", "Fabric reads gold", status)
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
        if s in (GOLD, ALWAYS_THERE) or not is_direct(r):
            continue
        leaks.append(f"{norm(r['privilege_type'])} on {s}.{low(r['table_name'])}")
        if REVOKE_EXTRA:
            revoke(r["privilege_type"], f"TABLE {q(CATALOG)}.{q(s)}.{q(low(r['table_name']))}", "not gold")
    leaks = sorted(set(leaks))
    if leaks:
        add("FAIL", "SP schema and table rights outside gold", "; ".join(leaks),
            "Fixed by the plan." if REVOKE_EXTRA else "Set revoke_extra = yes, or revoke by hand.")
    else:
        add("PASS", "SP schema and table rights outside gold", "none (volumes, functions and models not checked; harmless without USE SCHEMA)")
    for p in sorted(revokes_cat):  # catalog-level revokes go last, after gold has its own grants
        revoke(p, f"CATALOG {CATALOG_SQL}", "covers every schema")

    writes = sorted({norm(r["privilege_type"]) for r in sp_cat + sp_schema + sp_table if norm(r["privilege_type"]) in WRITE_LIKE})
    if writes:
        add("WARN", "SP holds write or admin rights here", ", ".join(writes),
            "A Fabric connection only reads. Make sure this SP is not also used by pipelines before you apply.")

    # Ownership: an owner sees and manages everything below it, without any grant (F122).
    owned = []
    if low(owner) in owners_to_check:
        owned.append(f"catalog {CATALOG} (owner {low(owner)})")
    ms_owner = low(ms.get("owner"))
    if ms_owner and ms_owner in owners_to_check:
        owned.append(f"the metastore (owner {ms_owner})")
    owned += [f"schema {low(s['schema_name'])} (owner {low(s['schema_owner'])})" for s in schemata
              if low(s["schema_owner"]) in owners_to_check and low(s["schema_name"]) not in (GOLD, ALWAYS_THERE)]
    owned += [f"table {low(t['schema_name'])}.{low(t['table_name'])} (owner {low(t['table_owner'])})" for t in tables
              if low(t["table_owner"]) in owners_to_check and low(t["schema_name"]) not in (GOLD, ALWAYS_THERE)]
    sp_owns = [t for t in tables if low(t["table_owner"]) == SP] + [s for s in schemata if low(s["schema_owner"]) == SP]
    sp_owns += [owner] if low(owner) == SP else []
    if owned:
        add("FAIL", "Ownership by the SP or its groups", "; ".join(owned),
            "Owners see every schema regardless of grants. Move ownership to an admin group the SP is not in "
            "(ALTER ... OWNER TO), or use a separate SP for Fabric.")
    else:
        add("PASS", "Ownership by the SP or its groups", "none outside gold (direct groups only)")
    if sp_owns:
        add("FAIL", "SP owns objects in this catalog", f"{len(sp_owns)} object(s): it writes data, so it is not a Fabric-only SP",
            "Apply is refused. Use a separate SP for the Fabric connection.")

    # Metastore-level grants inherit to every catalog (READ METADATA, BROWSE, MANAGE).
    try:
        mp = rows("SELECT grantee, privilege_type FROM system.information_schema.metastore_privileges")
        hits = [(low(r["grantee"]), norm(r["privilege_type"])) for r in mp if low(r["grantee"]) in owners_to_check]
        bad = [f"{g}: {p}" for g, p in hits if p in SELF_EXPOSING]
        other = [f"{g}: {p}" for g, p in hits if p not in SELF_EXPOSING]
        if bad:
            add("FAIL", "Metastore-level rights of the SP or its groups", "; ".join(sorted(set(bad))),
                "These reach every catalog. The metastore admin revokes them, or the SP leaves that group.")
        else:
            add("PASS", "Metastore-level rights of the SP or its groups", "nothing that exposes data")
        if other:
            add("WARN", "Other metastore-level rights of the SP or its groups", "; ".join(sorted(set(other))), "Not needed by Fabric.")
    except Exception as e:
        add("WARN", "Metastore-level rights", f"could not be read ({err(e)})", "Check: SELECT * FROM system.information_schema.metastore_privileges")

    # Proof: what the SP itself can list (= the Fabric picker). Covers nested groups and ownership.
    sp_check = "skipped"
    if SECRET_SCOPE:
        try:
            seen = schemas_seen_by_sp()
            extra = [s for s in seen if s not in (GOLD, ALWAYS_THERE)]
            if extra:
                sp_check = "fail"
                add("FAIL", "Schemas the SP can see (signed in as the SP)", ", ".join(extra) + " (Fabric would list these)", "Fix the FAIL lines above, then run again.")
            elif GOLD not in seen:
                sp_check = "fail"
                add("FAIL", "Schemas the SP can see (signed in as the SP)", "gold is not visible", "Apply the plan; check the SP is a workspace user.")
            else:
                sp_check = "pass"
                add("PASS", "Schemas the SP can see (signed in as the SP)", ", ".join(s for s in seen if s != ALWAYS_THERE))
        except Exception as e:
            sp_check = "error"
            add("WARN", "Schemas the SP can see (signed in as the SP)", f"sign-in or listing failed ({err(e)})",
                "Check the secret, the tenant ID and that the SP is a workspace user.")
    else:
        add("INFO", "Schemas the SP can see (signed in as the SP)", "skipped (no secret widgets)", "")

    # Groups: the SP also gets every right of its groups and of `account users` (reported, never changed).
    # A read-like right shows tables in Fabric; USE SCHEMA alone lists an empty schema at most, which Fabric hides.
    can_use = lambda p: p in ("USE SCHEMA", "ALL PRIVILEGES")
    use_everywhere = any(low(r["grantee"]) in principals and can_use(norm(r["privilege_type"])) for r in here)
    usable = {low(r["schema_name"]) for r in schema_privs
              if low(r["grantee"]) in principals and can_use(norm(r["privilege_type"]))} - {GOLD, ALWAYS_THERE}

    def exposes(p, schema):
        """True when this group right makes non-gold tables visible to the SP (schema None = whole catalog)."""
        if p in SELF_EXPOSING:
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
        if g in principals and s not in (GOLD, ALWAYS_THERE) and is_direct(r) and p in READ_LIKE:
            items.append((s, f"{g}: {p} on {s}.{low(r['table_name'])}", exposes(p, s)))
    exposed = {s for s, _, e in items if e}
    via = sorted({t for s, t, e in items if e or (s is not None and s in exposed)})
    via_minor = sorted({t for s, t, e in items if not (e or (s is not None and s in exposed))})
    if via:
        add("FAIL", "Rights the SP gets through a group", "; ".join(via),
            "Remove the SP from that group, or narrow the group's grant (check who else relies on it first).")
    elif groups is None:
        add("WARN", "Rights the SP gets through a group", "the SP's groups are unknown; only `account users` was checked",
            "Run the sign-in-as-SP check (secret widgets), or confirm in Fabric that only gold appears.")
    else:
        add("PASS", "Rights the SP gets through a group", "nothing readable outside gold (direct groups and `account users`)")
    if via_minor:
        add("WARN", "Minor rights the SP gets through a group", "; ".join(via_minor),
            "No non-gold table becomes visible from these alone (Fabric hides empty schemas); remove them if you can.")
    if groups is not None and sp_check != "pass":
        add("WARN", "Nested groups not checked", "the workspace API lists direct groups only (F122)",
            "Run the sign-in-as-SP check (secret widgets), or confirm in Fabric that only gold appears.")

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
        add("WARN", "Other principals with EXTERNAL USE SCHEMA outside gold", "; ".join(sorted(set(others))),
            "Look each application ID up in Settings > Identity and access > Service principals. An older Fabric connection or "
            "external engine with this right can read bronze and silver from outside Databricks; if it isn't intended, its owner "
            f"runs REVOKE EXTERNAL USE SCHEMA ON CATALOG {CATALOG_SQL} FROM `<id>` (this notebook never changes other principals).")
    else:
        add("PASS", "Other principals with EXTERNAL USE SCHEMA outside gold", "none")

    # Other catalogs this SP can reach: Fabric could mirror them too.
    elsewhere = sorted({low(r["catalog_name"]) for r in cat_privs if low(r["grantee"]) == SP and low(r["catalog_name"]) != CATALOG})
    if elsewhere:
        add("WARN", "SP rights in other catalogs (at least)", ", ".join(elsewhere),
            "A Fabric connection with this SP can mirror those catalogs too (group rights there are not checked). "
            "Prefer one SP per environment; otherwise run this notebook for each of those catalogs as well.")

    # Gold objects Fabric will not mirror (F118).
    gold_objs = [t for t in tables if low(t["schema_name"]) == GOLD]
    skipped = [f"{low(t['table_name'])} ({t['table_type']})" for t in gold_objs if (t["table_type"] or "").upper() in NOT_MIRRORED]
    skipped += [f"{low(t['table_name'])} (external {t['data_source_format']})" for t in gold_objs
                if (t["table_type"] or "").upper() == "EXTERNAL" and (t["data_source_format"] or "").upper() != "DELTA"]
    # Learn names these columns catalog_name / schema_name, but a live workspace answered UNRESOLVED_COLUMN (F120):
    # read the view's real columns first and use whichever of the known spellings it has.
    unchecked = []
    for view in ("row_filters", "column_masks"):
        errors = []
        for source in ("system.information_schema", f"{CATALOG_SQL}.information_schema"):  # system first, then the catalog's own
            try:
                cols = [c.lower() for c in spark.sql(f"SELECT * FROM {source}.{view} LIMIT 0").columns]
                pick = lambda *names: next((n for n in names if n in cols), None)
                cat_col, schema_col, table_col = pick("catalog_name", "table_catalog"), pick("schema_name", "table_schema"), pick("table_name")
                if not (cat_col and schema_col and table_col):
                    errors.append(f"{source}: columns are {', '.join(cols) or 'none'}")
                    continue
                hits = rows(
                    f"SELECT DISTINCT {table_col} AS table_name FROM {source}.{view} "
                    f"WHERE lower({cat_col}) = :c AND lower({schema_col}) = :s",
                    c=CATALOG, s=GOLD,
                )
                skipped += [f"{low(h['table_name'])} ({view.replace('_', ' ')[:-1]})" for h in hits]
                break
            except Exception as e:
                errors.append(f"{source}: {err(e)}")
        else:
            unchecked.append(view)
            add("WARN", f"Gold {view}", "could not be read (" + "; ".join(errors) + ")",
                f"Check by hand: SELECT * FROM {CATALOG_SQL}.information_schema.{view} LIMIT 20")
    pipelines = [low(t["table_name"]) for t in gold_objs if (t["table_type"] or "").upper() in ("MATERIALIZED_VIEW", "STREAMING_TABLE")]
    if skipped:
        add("WARN", "Gold objects Fabric will not show", "; ".join(sorted(set(skipped))),
            "Views, metric views, federated, non-Delta external and row-filter/column-mask tables are not mirrored; publish a Delta table instead.")
    else:
        note = f"; {' and '.join(unchecked)} not checked" if unchecked else ""
        add("PASS", "Gold objects Fabric will not show", f"none ({len(gold_objs)} objects in gold{note}; ABAC policies not checked)")
    if pipelines:
        add("INFO", "Gold materialized views / streaming tables", ", ".join(sorted(pipelines)),
            "They need TBLPROPERTIES ('pipelines.externalMetadata.enabled' = 'true') or the pipeline setting, then a pipeline run.")

    # Order: REVOKE ALL PRIVILEGES first (it also removes the privileges it implies, F122), then the GRANTs,
    # then the other REVOKEs: gold extras, schemas, tables, and the catalog last.
    plan = [r for r in revokes if r["all"]] + grants + [r for r in revokes if not r["all"]]
    for i, step in enumerate(plan, 1):
        step.pop("all", None)
        step["step"] = i
    plan_id = hashlib.sha256(json.dumps([SP, CATALOG, GOLD, [p["sql"] for p in plan]]).encode()).hexdigest()[:8]

    return {"findings": findings, "plan": plan, "plan_id": plan_id, "inputs": INPUTS, "me": me, "owner": owner,
            "runner_is_owner": runner_is_owner, "sp_name": sp["name"], "sp_owns": len(sp_owns)}


ORDER = {"FAIL": 0, "WARN": 1, "INFO": 2, "PASS": 3}


def report(result, title):
    f = sorted(result["findings"], key=lambda x: ORDER[x["level"]])
    counts = {k: sum(1 for x in f if x["level"] == k) for k in ORDER}
    print(f"\n##### {title}: {counts['FAIL']} FAIL, {counts['WARN']} WARN, {counts['PASS']} PASS")
    show("Findings", f)
    return counts


before = audit()
counts_before = report(before, "Before")
show(f"Plan {before['plan_id']} (runs in this order on apply; GRANT and REVOKE are idempotent)",
     [{k: p[k] for k in ("step", "action", "sql", "why", "status")} for p in before["plan"]])
print(f"\nPlan ID: {before['plan_id']}  (to apply exactly this plan: mode = apply, approved_by, confirm_plan = {before['plan_id']})")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Apply (only when `mode = apply`)

# COMMAND ----------

applied, after, counts_after = [], None, None
if not globals().get("before") or not globals().get("INPUTS_OK"):
    raise RuntimeError("Choose Run all from the top: the audit above has not run with the current widgets. Nothing changed.")
MODE, APPROVED_BY, CONFIRM_PLAN = widget("mode"), widget("approved_by"), widget("confirm_plan").lower()  # read again, never stale
if MODE != "apply":
    print("mode = plan: nothing changed. Review the findings and the plan, then set mode = apply, approved_by and confirm_plan.")
else:
    refusals = []
    if current_inputs() != before["inputs"]:
        refusals.append("the widgets changed since the audit above (service principal, catalog, gold schema or revoke_extra); choose Run all")
    if not APPROVED_BY or not CONFIRM_PLAN:
        refusals.append("apply needs approved_by and confirm_plan")
    if not before["runner_is_owner"]:
        refusals.append(f"only the catalog owner ({before['owner']}) can grant EXTERNAL USE SCHEMA; you are {before['me']}")
    if CONFIRM_PLAN != before["plan_id"]:
        refusals.append(f"confirm_plan is '{CONFIRM_PLAN}' but the current plan is {before['plan_id']} "
                        "(the grants changed since your plan run, or the ID is from another run): review the plan above")
    if before["sp_owns"]:
        refusals.append("the SP owns objects in this catalog, so it is a pipeline identity; use a separate SP for Fabric")
    if refusals:
        raise RuntimeError("Refused, nothing changed:\n- " + "\n- ".join(refusals))
    started = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
    print(f"Applying plan {before['plan_id']}: {len(before['plan'])} statements | SP: {before['sp_name'] or '?'} ({SP}) "
          f"| approved by: {APPROVED_BY} | run by: {before['me']} | {started}")
    for step in before["plan"]:
        try:
            spark.sql(step["sql"])
            applied.append({"step": step["step"], "sql": step["sql"], "result": "done"})
        except Exception as e:
            applied.append({"step": step["step"], "sql": step["sql"], "result": "FAILED: " + err(e)})
            print("Stopped at the first failure; later statements were not run.")
            break
    show("Applied", applied)
    after = audit()
    counts_after = report(after, "After")
    left = [{k: p[k] for k in ("step", "action", "sql", "status")} for p in after["plan"] if p["status"] != "already held"]
    show("Changes still pending", left)

# COMMAND ----------

final = counts_after or counts_before
summary = {
    "mode": MODE,
    "catalog": CATALOG,
    "gold_schema": GOLD,
    "service_principal": SP,
    "plan_id": before["plan_id"],
    "approved_by": APPROVED_BY if MODE == "apply" else "",
    "statements_run": len(applied),
    "statements_failed": sum(1 for a in applied if a["result"] != "done"),
    "fail": final["FAIL"],
    "warn": final["WARN"],
    "pass": final["PASS"],
}
print(json.dumps(summary, indent=2))
if summary["statements_failed"]:
    raise RuntimeError(f"{summary['statements_failed']} statement(s) failed during apply. See the Applied table above.")
if FAIL_ON_FINDINGS and summary["fail"]:
    raise RuntimeError(f"{summary['fail']} FAIL line(s). See the findings above.")

# COMMAND ----------

# MAGIC %md
# MAGIC ## Then in Fabric
# MAGIC 1. Create the connection with **Service principal** authentication for this service principal.
# MAGIC 2. Go to **New item → Mirrored Azure Databricks catalog**, pick the catalog and select **gold** only.
# MAGIC    - The picker lists only what this service principal can see.
# MAGIC    - Keep *Automatically sync future catalog changes*: new gold tables arrive by themselves, and other schemas stay invisible.
# MAGIC 3. Proof in the mirror's **SQL analytics endpoint** (expect `gold` only):
# MAGIC    `SELECT DISTINCT TABLE_SCHEMA FROM INFORMATION_SCHEMA.TABLES;`
# MAGIC 4. Access inside Fabric:
# MAGIC    - consumers get Viewer or an item share;
# MAGIC    - Admin, Member and Contributor go to the platform team only;
# MAGIC    - use OneLake security roles for table-level access inside gold.
# MAGIC 5. Storage:
# MAGIC    - don't create Lakehouse ADLS shortcuts straight to Databricks storage: they bypass Unity Catalog;
# MAGIC    - if the storage is behind a firewall, scope the mirror's ADLS connection and the workspace identity's role to **gold's folder only**.
# MAGIC
# MAGIC Schedule this notebook as a monthly job:
# MAGIC - `plan` mode, `fail_on_findings = yes`, failure alerts on;
# MAGIC - run as a user or service principal in the catalog-owning group.
# MAGIC
# MAGIC It changes nothing and fails as soon as drift appears.

# COMMAND ----------

dbutils.notebook.exit(json.dumps(summary))
