#!/usr/bin/env python3
"""Offline tests for tools/databricks/fabric_gold_only_notebook.py.

Runs the notebook source against stand-ins for spark, dbutils, display, pandas and the Databricks SDK.
GRANT and REVOKE change the stand-in grants (REVOKE ALL PRIVILEGES follows Learn's rule, F122), so the re-audit
after apply is tested too. The stand-ins are a model of Databricks, not Databricks: the live plan run is the proof.
All data is SAMPLE.

  python3 tools/databricks/test_fabric_gold_only.py
"""
from __future__ import annotations

import contextlib
import copy
import io
import json
import re
import sys
import types
from pathlib import Path

NOTEBOOK = Path(__file__).resolve().parent / "fabric_gold_only_notebook.py"
SP = "00000000-0000-4000-8000-00000000f001"      # SAMPLE service principal application id
OTHER_SP = "00000000-0000-4000-8000-00000000f002"  # SAMPLE second service principal
SECRET = "SAMPLE-not-a-real-secret-123"
SCHEMAS = ["bronze", "silver_cleansed", "silver_curated", "gold", "information_schema"]
NOT_IN_ALL = {"MANAGE", "READ METADATA", "EXTERNAL USE LOCATION", "EXTERNAL USE SCHEMA"}  # Learn: REVOKE ALL leaves these


class Exit(Exception):
    def __init__(self, value):
        self.value = value


class SparkError(Exception):
    """Like pyspark's AnalysisException: carries an error class."""

    def getErrorClass(self):
        return "PERMISSION_DENIED"


class Row(dict):
    def asDict(self):
        return dict(self)


class Result:
    def __init__(self, data, columns=None):
        self.data = [Row(d) for d in data]
        self.columns = columns if columns is not None else (list(data[0]) if data else [])

    def collect(self):
        return self.data

    def first(self):
        return self.data[0]


def dirty_state():
    """SAMPLE prod catalog with the problems the notebook must find."""
    return {
        "me": "admin@sample.invalid",
        "my_groups": {"catalog-admins"},
        "catalogs": [{"catalog_name": "prod", "catalog_owner": "catalog-admins"}],
        "schemata": [{"schema_name": s, "schema_owner": "data-eng"} for s in SCHEMAS],
        "cat_privs": [
            {"grantee": SP, "catalog_name": "prod", "privilege_type": "USE_CATALOG", "inherited_from": None},
            {"grantee": SP, "catalog_name": "prod", "privilege_type": "SELECT", "inherited_from": None},
            {"grantee": SP, "catalog_name": "prod", "privilege_type": "EXTERNAL USE SCHEMA", "inherited_from": None},
            {"grantee": SP, "catalog_name": "dev", "privilege_type": "USE CATALOG", "inherited_from": None},
            {"grantee": "account users", "catalog_name": "prod", "privilege_type": "USE CATALOG", "inherited_from": None},
            {"grantee": OTHER_SP, "catalog_name": "prod", "privilege_type": "EXTERNAL_USE_SCHEMA", "inherited_from": None},
        ],
        "schema_privs": [
            {"grantee": SP, "schema_name": "silver_cleansed", "privilege_type": "USE_SCHEMA", "inherited_from": None},
            {"grantee": SP, "schema_name": "gold", "privilege_type": "USE SCHEMA", "inherited_from": None},
            {"grantee": SP, "schema_name": "gold", "privilege_type": "MODIFY", "inherited_from": None},
            {"grantee": "data-readers", "schema_name": "silver_curated", "privilege_type": "USE SCHEMA", "inherited_from": None},
            {"grantee": "data-readers", "schema_name": "silver_curated", "privilege_type": "SELECT", "inherited_from": None},
            {"grantee": "account users", "schema_name": "bronze", "privilege_type": "USE SCHEMA", "inherited_from": None},
        ],
        "table_privs": [
            {"grantee": SP, "schema_name": "bronze", "table_name": "raw_orders", "privilege_type": "SELECT", "inherited_from": "NONE"},
            # the same grant from a second grantor: must give one REVOKE, not two
            {"grantee": SP, "schema_name": "bronze", "table_name": "raw_orders", "privilege_type": "SELECT", "inherited_from": "NONE"},
            # Databricks grants account users SELECT on every information_schema view: never a finding
            {"grantee": "account users", "schema_name": "information_schema", "table_name": "tables", "privilege_type": "SELECT", "inherited_from": "NONE"},
        ],
        "tables": [
            {"schema_name": "gold", "table_name": "dim_customer", "table_type": "MANAGED", "data_source_format": "DELTA", "table_owner": "data-eng"},
            {"schema_name": "gold", "table_name": "v_sales", "table_type": "VIEW", "data_source_format": "UNKNOWN_DATA_SOURCE_FORMAT", "table_owner": "data-eng"},
            {"schema_name": "gold", "table_name": "mv_kpi", "table_type": "MATERIALIZED_VIEW", "data_source_format": "DELTA", "table_owner": "data-eng"},
            {"schema_name": "gold", "table_name": "ext_csv", "table_type": "EXTERNAL", "data_source_format": "CSV", "table_owner": "data-eng"},
            {"schema_name": "silver_cleansed", "table_name": "orders", "table_type": "MANAGED", "data_source_format": "DELTA", "table_owner": SP},
        ],
        "row_filters": [{"table_name": "fact_secure"}],
        "column_masks": [],
        "ms_privs": [],
        "metastore_owner": "metastore-admins",
        "scim": [{"displayName": "sp-fabric-mirror-prod", "applicationId": SP, "active": True, "groups": [{"display": "data-readers"}]}],
        "external_access": True,
        "fail_sql": None,
        "fail_views": set(),  # e.g. {"system.information_schema.row_filters"}: that source raises
        # the real columns of row_filters / column_masks (Learn's spelling; a live workspace differed, F120)
        "mask_columns": ["catalog_name", "schema_name", "table_name", "filter_name"],
    }


def appliable_state():
    """The dirty catalog without the SP-owned table (an SP that owns objects is refused)."""
    s = dirty_state()
    s["tables"] = [t for t in s["tables"] if t["table_owner"] != SP]
    return s


def clean_state():
    s = dirty_state()
    s["cat_privs"] = [
        {"grantee": SP, "catalog_name": "prod", "privilege_type": "USE CATALOG", "inherited_from": None},
        {"grantee": "account users", "catalog_name": "prod", "privilege_type": "USE CATALOG", "inherited_from": None},
    ]
    s["schema_privs"] = [{"grantee": SP, "schema_name": "gold", "privilege_type": p, "inherited_from": None}
                         for p in ("USE SCHEMA", "SELECT", "EXTERNAL USE SCHEMA")]
    s["table_privs"], s["row_filters"] = [], []
    s["tables"] = [t for t in s["tables"] if t["table_name"] == "dim_customer"]
    s["scim"][0]["groups"] = []
    return s


def norm(p):
    return " ".join((p or "").replace("_", " ").upper().split())


def parts(name):
    return [m.replace("``", "`") for m in re.findall(r"`((?:[^`]|``)*)`", name)]


class Spark:
    def __init__(self, state):
        self.s = state
        self.executed = []
        self.conf = types.SimpleNamespace(get=lambda k: "adb-0000000000000000.0.azuredatabricks.net")

    def schema_rows(self):
        out = list(self.s["schema_privs"])
        for r in self.s["cat_privs"]:  # catalog grants show up inherited on every schema
            if r["catalog_name"] == "prod" and norm(r["privilege_type"]) != "USE CATALOG":
                out += [{"grantee": r["grantee"], "schema_name": sc, "privilege_type": r["privilege_type"], "inherited_from": "CATALOG"}
                        for sc in SCHEMAS]
        return out

    def sql(self, query, args=None):
        args = args or {}
        m = re.match(r"^(GRANT|REVOKE) ([A-Z ]+) ON (CATALOG|SCHEMA|TABLE) (.+) (TO|FROM) (`[^`]+`)$", query)
        if m:
            self.executed.append(query)
            if self.s["fail_sql"] and self.s["fail_sql"] in query:
                raise SparkError("[PERMISSION_DENIED] SAMPLE failure\nmore detail")
            self.apply(m.group(1), m.group(2), m.group(3), parts(m.group(4)), parts(m.group(6))[0])
            return Result([])
        if query.startswith(("GRANT", "REVOKE")):
            raise AssertionError(f"unparsed statement: {query}")
        if "current_user()" in query:
            return Result([{"me": self.s["me"]}])
        if "is_account_group_member" in query:
            return Result([{"m": args["g"] in self.s["my_groups"]}])
        if any(v in query for v in self.s["fail_views"]):
            raise Exception("TABLE_OR_VIEW_NOT_FOUND (SAMPLE)")
        table = re.search(r"information_schema\.(\w+)", query).group(1)
        if table in ("row_filters", "column_masks"):
            cols = self.s["mask_columns"]
            if query.endswith("LIMIT 0"):
                return Result([], columns=cols)
            used = re.findall(r"lower\((\w+)\)", query) + re.findall(r"DISTINCT (\w+)", query)
            if any(c not in cols for c in used):
                raise Exception("UNRESOLVED_COLUMN.WITH_SUGGESTION (SAMPLE)")
        if table == "tables":  # the notebook filters by gold or by owner in SQL: check the owner list is passed
            assert SP in args.values() and "account users" in args.values(), args
        data = {
            "catalogs": [c for c in self.s["catalogs"] if c["catalog_name"] == args.get("c")],
            "schemata": self.s["schemata"],
            "catalog_privileges": self.s["cat_privs"],
            "schema_privileges": self.schema_rows(),
            "table_privileges": self.s["table_privs"],
            "tables": self.s["tables"],
            "row_filters": self.s["row_filters"],
            "column_masks": self.s["column_masks"],
            "metastore_privileges": self.s["ms_privs"],
        }[table]
        return Result(copy.deepcopy(data))

    def apply(self, verb, privilege, kind, name, grantee):
        key = {"CATALOG": "cat_privs", "SCHEMA": "schema_privs", "TABLE": "table_privs"}[kind]
        row = {"grantee": grantee, "privilege_type": privilege, "inherited_from": None}
        if kind == "CATALOG":
            row["catalog_name"] = name[0]
        elif kind == "SCHEMA":
            row["schema_name"] = name[1]
        else:
            row.update(schema_name=name[1], table_name=name[2])
        where = {k: v for k, v in row.items() if k not in ("inherited_from", "privilege_type")}
        on_it = lambda r: all(norm(r.get(k)) == norm(v) for k, v in where.items())
        if verb == "REVOKE" and privilege == "ALL PRIVILEGES":  # Learn: also revokes every privilege it implies
            self.s[key] = [r for r in self.s[key] if not on_it(r) or norm(r["privilege_type"]) in NOT_IN_ALL]
            return
        self.s[key] = [r for r in self.s[key] if not (on_it(r) and norm(r["privilege_type"]) == norm(privilege))]
        if verb == "GRANT":
            self.s[key].append(row)


class Api:
    def __init__(self, state, kwargs):
        self.s, self.kwargs = state, kwargs

    def do(self, method, path, query=None):
        if path.endswith("/ServicePrincipals"):
            assert query["filter"] == f'applicationId eq "{SP}"', query
            return {"Resources": self.s["scim"]}
        if path.endswith("/metastore_summary"):
            return {"external_access_enabled": self.s["external_access"], "owner": self.s["metastore_owner"]}
        if path.endswith("/unity-catalog/schemas"):
            assert self.kwargs.get("azure_client_id") == SP and self.kwargs.get("azure_client_secret") == SECRET, "not signed in as the SP"
            assert self.kwargs.get("auth_type") == "azure-client-secret"
            who = {SP, "account users"} | {g["display"] for g in (self.s["scim"][0]["groups"] if self.s["scim"] else [])}
            if self.s["catalogs"][0]["catalog_owner"] in who:
                return {"schemas": [{"name": n} for n in SCHEMAS]}
            seen = {r["schema_name"] for r in Spark(self.s).schema_rows()
                    if r["grantee"] in who and norm(r["privilege_type"]) in ("USE SCHEMA", "ALL PRIVILEGES", "BROWSE")}
            seen |= {s["schema_name"] for s in self.s["schemata"] if s["schema_owner"] in who}
            return {"schemas": [{"name": n} for n in sorted(seen)]}
        raise AssertionError(path)


def run(widgets: dict, state: dict):
    """Run the notebook; return (summary or exception, executed statements, printed text, notebook globals)."""
    spark = Spark(state)
    shown = []
    sdk = types.ModuleType("databricks.sdk")
    sdk.WorkspaceClient = lambda **kw: types.SimpleNamespace(api_client=Api(state, kw))
    sys.modules["databricks"] = types.ModuleType("databricks")
    sys.modules["databricks.sdk"] = sdk
    pandas = types.ModuleType("pandas")
    pandas.DataFrame = lambda data: data
    sys.modules["pandas"] = pandas

    defaults = {}
    w = types.SimpleNamespace(
        text=lambda n, d, label: defaults.setdefault(n, d),
        dropdown=lambda n, d, choices, label: defaults.setdefault(n, d),
        get=lambda n: widgets.get(n, defaults[n]),
    )

    def exit_(value):
        raise Exit(value)

    dbutils = types.SimpleNamespace(
        widgets=w,
        secrets=types.SimpleNamespace(get=lambda scope, key: SECRET),
        notebook=types.SimpleNamespace(exit=exit_),
    )
    g = {"__name__": "__notebook__", "spark": spark, "dbutils": dbutils, "display": shown.append}
    out = io.StringIO()
    result = None
    with contextlib.redirect_stdout(out):
        try:
            exec(compile(NOTEBOOK.read_text(), str(NOTEBOOK), "exec"), g)
            raise AssertionError("notebook did not call dbutils.notebook.exit")
        except Exit as e:
            result = json.loads(e.value)
        except (ValueError, RuntimeError) as e:
            result = e
    text = out.getvalue() + json.dumps(shown, default=str)
    assert SECRET not in text, "secret leaked into the output"
    return result, spark.executed, text, g


def apply_run(widgets: dict, state: dict):
    """Plan on a copy, then apply that plan ID on the real state (what an operator does)."""
    _, executed, _, g = run({**widgets, "mode": "plan"}, copy.deepcopy(state))
    assert executed == []
    return run({**widgets, "mode": "apply", "approved_by": "SAMPLE approver 05 Nov 2026", "confirm_plan": g["before"]["plan_id"]}, state)


def findings(g, which="before"):
    return {(f["level"], f["check"]): f for f in g[which]["findings"]}


def check() -> None:
    base = {"sp_application_id": SP.upper(), "catalog": "PROD", "gold_schema": "gold"}
    fix = {**base, "revoke_extra": "yes"}
    qsp = f"`{SP}`"

    # 1. plan on a dirty catalog: nothing runs, every problem is named, the plan is in the right order
    res, executed, text, g = run(fix, dirty_state())
    assert executed == [], executed
    assert res["mode"] == "plan" and res["fail"] > 0 and res["statements_run"] == 0 and len(res["plan_id"]) == 8, res
    assert f"Plan ID: {res['plan_id']}" in text
    f = findings(g)
    assert "EXTERNAL USE SCHEMA" in f[("FAIL", "SP rights on the whole catalog")]["detail"]
    assert ("FAIL", "SP rights on gold") in f
    leaks = f[("FAIL", "SP schema and table rights outside gold")]["detail"]
    assert leaks == "SELECT on bronze.raw_orders; USE SCHEMA on silver_cleansed", leaks
    assert "silver_cleansed.orders" in f[("FAIL", "Ownership by the SP or its groups")]["detail"]
    assert ("FAIL", "SP owns objects in this catalog") in f
    via = f[("FAIL", "Rights the SP gets through a group")]["detail"]
    assert "data-readers: SELECT on silver_curated" in via and "data-readers: USE SCHEMA on silver_curated" in via, via
    assert f[("WARN", "Minor rights the SP gets through a group")]["detail"] == "account users: USE SCHEMA on bronze"
    assert OTHER_SP in f[("WARN", "Other principals with EXTERNAL USE SCHEMA outside gold")]["detail"]
    assert "dev" in f[("WARN", "SP rights in other catalogs (at least)")]["detail"]
    assert "MODIFY" in f[("WARN", "SP holds write or admin rights here")]["detail"]
    assert ("WARN", "Nested groups not checked") in f
    skipped = f[("WARN", "Gold objects Fabric will not show")]["detail"]
    assert all(x in skipped for x in ("v_sales (VIEW)", "ext_csv (external CSV)", "fact_secure (row filter)")), skipped
    assert "mv_kpi" in f[("INFO", "Gold materialized views / streaming tables")]["detail"]
    sql = [p["sql"] for p in g["before"]["plan"]]
    assert sql[:4] == [
        f"GRANT USE CATALOG ON CATALOG `prod` TO {qsp}",
        f"GRANT USE SCHEMA ON SCHEMA `prod`.`gold` TO {qsp}",
        f"GRANT SELECT ON SCHEMA `prod`.`gold` TO {qsp}",
        f"GRANT EXTERNAL USE SCHEMA ON SCHEMA `prod`.`gold` TO {qsp}",
    ], sql
    assert sql[-2:] == [f"REVOKE EXTERNAL USE SCHEMA ON CATALOG `prod` FROM {qsp}", f"REVOKE SELECT ON CATALOG `prod` FROM {qsp}"], sql
    assert sql.count(f"REVOKE SELECT ON TABLE `prod`.`bronze`.`raw_orders` FROM {qsp}") == 1, "duplicate grant rows must give one REVOKE"
    assert f"REVOKE MODIFY ON SCHEMA `prod`.`gold` FROM {qsp}" in sql
    assert f"REVOKE USE SCHEMA ON SCHEMA `prod`.`silver_cleansed` FROM {qsp}" in sql
    assert not any("data-readers" in s or "account users" in s for s in sql), "group grants must never be changed"
    assert g["before"]["plan"][0]["status"] == "already held" and g["before"]["plan"][2]["status"] == "new"

    # 1b. default revoke_extra = no: only GRANTs, the leaks stay FAIL
    _, _, _, g = run(base, dirty_state())
    assert all(p["action"] == "GRANT" for p in g["before"]["plan"]), g["before"]["plan"]
    assert "revoke_extra" in findings(g)[("FAIL", "SP schema and table rights outside gold")]["fix"]

    # 2. apply: same statements in the same order, then the re-audit shows only what needs a person
    _, _, _, gp = run(fix, appliable_state())
    res, executed, text, g = apply_run(fix, appliable_state())
    assert executed == [p["sql"] for p in gp["before"]["plan"]], executed
    assert res["statements_run"] == len(executed) and res["statements_failed"] == 0, res
    after = findings(g, "after")
    for ok in ("SP rights on the catalog", "SP rights on gold", "SP schema and table rights outside gold"):
        assert ("PASS", ok) in after, (ok, after.keys())
    left = {c for (lvl, c) in after if lvl == "FAIL"}
    assert left == {"Rights the SP gets through a group"}, left
    assert "approved by: SAMPLE approver 05 Nov 2026" in text and "SP: sp-fabric-mirror-prod" in text

    # 3. widget errors: refused before anything runs
    for bad, msg in (
        ({**fix, "mode": "apply", "confirm_plan": "x"}, "approved_by"),
        ({**fix, "mode": "apply", "approved_by": "SAMPLE"}, "confirm_plan"),
        ({**fix, "sp_application_id": "sp-fabric"}, "GUID"),
        ({**fix, "sp_secret_scope": "kv"}, "both"),
    ):
        res, executed, _, _ = run(bad, dirty_state())
        assert isinstance(res, ValueError) and msg in str(res) and executed == [], (bad, res)

    # 4. not the catalog owner: plan says FAIL, apply refused
    s = appliable_state()
    s["my_groups"] = set()
    res, executed, _, g = run(fix, copy.deepcopy(s))
    assert ("FAIL", "Runner is not the catalog owner") in findings(g)
    res, executed, _, _ = run({**fix, "mode": "apply", "approved_by": "SAMPLE", "confirm_plan": g["before"]["plan_id"]}, s)
    assert isinstance(res, RuntimeError) and "only the catalog owner" in str(res) and executed == [], res

    # 5. the approved plan ID must match the current plan: drift after the plan run is refused
    s = appliable_state()
    _, _, _, g = run(fix, copy.deepcopy(s))
    s["schema_privs"].append({"grantee": SP, "schema_name": "bronze", "privilege_type": "SELECT", "inherited_from": None})
    res, executed, _, _ = run({**fix, "mode": "apply", "approved_by": "SAMPLE", "confirm_plan": g["before"]["plan_id"]}, s)
    assert isinstance(res, RuntimeError) and "confirm_plan" in str(res) and executed == [], res

    # 6. an SP that owns objects is a pipeline identity: apply refused
    res, executed, _, _ = apply_run(fix, dirty_state())
    assert isinstance(res, RuntimeError) and "pipeline identity" in str(res) and executed == [], res

    # 7. a failing statement stops the run and always fails it, even without fail_on_findings
    s = appliable_state()
    s["fail_sql"] = "GRANT SELECT ON SCHEMA"
    res, executed, _, g = apply_run(fix, s)
    assert isinstance(res, RuntimeError) and "failed during apply" in str(res) and len(executed) == 3, (res, executed)
    assert g["applied"][-1]["result"] == "FAILED: PERMISSION_DENIED", g["applied"][-1]

    # 8. clean catalog: no FAIL, nothing new to do; fail_on_findings does not trip
    res, executed, _, g = run({**fix, "fail_on_findings": "yes"}, clean_state())
    assert not isinstance(res, Exception) and res["fail"] == 0, (res, [x for x in g["before"]["findings"] if x["level"] == "FAIL"])
    assert all(p["status"] == "already held" for p in g["before"]["plan"]), g["before"]["plan"]

    # 9. drift in plan mode with fail_on_findings = yes: the job fails
    res, executed, _, _ = run({**fix, "fail_on_findings": "yes"}, dirty_state())
    assert isinstance(res, RuntimeError) and "FAIL line" in str(res) and executed == [], res

    # 10. the check signed in as the SP: sees silver before, gold only after apply; the secret never printed
    secret_widgets = {"sp_secret_scope": "SAMPLE-scope", "sp_secret_key": "SAMPLE-key"}
    s = appliable_state()
    s["scim"][0]["groups"] = []
    res, _, _, g = run({**fix, **secret_widgets}, s)
    seen = findings(g)[("FAIL", "Schemas the SP can see (signed in as the SP)")]["detail"]
    assert "silver_cleansed" in seen and "bronze" in seen, seen
    s = appliable_state()
    s["scim"][0]["groups"] = []
    s["schema_privs"] = [r for r in s["schema_privs"] if r["grantee"] != "account users"]
    res, _, _, g = apply_run({**fix, **secret_widgets}, s)
    after = findings(g, "after")
    assert ("PASS", "Schemas the SP can see (signed in as the SP)") in after, after.keys()
    assert ("WARN", "Nested groups not checked") not in after

    # 11. metastore access off; SP not directly in the workspace = WARN, groups unknown = WARN (not PASS); inactive SP = FAIL
    s = clean_state()
    s["external_access"] = False
    s["scim"] = []
    _, _, _, g = run(base, s)
    f = findings(g)
    assert ("FAIL", "Metastore external data access") in f
    assert ("WARN", "Service principal in this workspace") in f and "through a group" in f[("WARN", "Service principal in this workspace")]["fix"]
    assert ("WARN", "Rights the SP gets through a group") in f and ("PASS", "Rights the SP gets through a group") not in f
    s = clean_state()
    s["scim"][0]["active"] = False
    assert ("FAIL", "Service principal in this workspace") in findings(run(base, s)[3])

    # 12. ALL PRIVILEGES on the catalog: revoked FIRST, then USE CATALOG granted again; the re-audit proves it
    s = clean_state()
    s["cat_privs"] = [r for r in s["cat_privs"] if r["grantee"] != SP and r["grantee"] != "account users"]
    s["cat_privs"].append({"grantee": SP, "catalog_name": "prod", "privilege_type": "ALL PRIVILEGES", "inherited_from": None})
    res, executed, _, g = apply_run(fix, s)
    assert executed[0] == f"REVOKE ALL PRIVILEGES ON CATALOG `prod` FROM {qsp}", executed
    assert executed[1] == f"GRANT USE CATALOG ON CATALOG `prod` TO {qsp}", executed
    after = findings(g, "after")
    assert ("PASS", "SP rights on the catalog") in after and ("FAIL", "SP USE CATALOG") not in after, after.keys()
    assert res["fail"] == 0, [x for x in g["after"]["findings"] if x["level"] == "FAIL"]

    # 13. ALL PRIVILEGES on gold: revoked first, the three gold rights granted after it
    s = clean_state()
    s["schema_privs"] = [{"grantee": SP, "schema_name": "gold", "privilege_type": "ALL PRIVILEGES", "inherited_from": None}]
    res, executed, _, g = apply_run(fix, s)
    assert executed[0] == f"REVOKE ALL PRIVILEGES ON SCHEMA `prod`.`gold` FROM {qsp}", executed
    assert ("PASS", "SP rights on gold") in findings(g, "after") and res["fail"] == 0, findings(g, "after").keys()

    # 14. ownership through a group, of the catalog, a schema or the metastore: FAIL
    s = clean_state()
    s["scim"][0]["groups"] = [{"display": "data-eng"}]  # data-eng owns every schema in the sample
    own = findings(run(base, s)[3])[("FAIL", "Ownership by the SP or its groups")]["detail"]
    assert "schema silver_curated (owner data-eng)" in own and "schema gold" not in own, own
    s = clean_state()
    s["scim"][0]["groups"] = [{"display": "catalog-admins"}]
    s["metastore_owner"] = "catalog-admins"
    own = findings(run(base, s)[3])[("FAIL", "Ownership by the SP or its groups")]["detail"]
    assert "catalog prod" in own and "the metastore" in own, own

    # 15. READ METADATA through a group, or on the metastore: FAIL
    s = clean_state()
    s["scim"][0]["groups"] = [{"display": "auditors"}]
    s["cat_privs"].append({"grantee": "auditors", "catalog_name": "prod", "privilege_type": "READ_METADATA", "inherited_from": None})
    s["ms_privs"] = [{"grantee": "account users", "privilege_type": "READ METADATA"}, {"grantee": "auditors", "privilege_type": "CREATE CATALOG"}]
    f = findings(run(base, s)[3])
    assert "auditors: READ METADATA on the whole catalog" in f[("FAIL", "Rights the SP gets through a group")]["detail"]
    assert "account users: READ METADATA" in f[("FAIL", "Metastore-level rights of the SP or its groups")]["detail"]
    assert "auditors: CREATE CATALOG" in f[("WARN", "Other metastore-level rights of the SP or its groups")]["detail"]

    # 16. row filters: falls back to the catalog's own information_schema; both missing = WARN, PASS says "not checked"
    s = dirty_state()
    s["fail_views"] = {"system.information_schema.row_filters", "system.information_schema.column_masks"}
    f = findings(run(base, s)[3])
    assert "fact_secure (row filter)" in f[("WARN", "Gold objects Fabric will not show")]["detail"]
    assert not any(c.startswith("Gold row_filters") or c.startswith("Gold column_masks") for _, c in f), f.keys()
    s = clean_state()
    s["fail_views"] = {"information_schema.row_filters"}
    f = findings(run(base, s)[3])
    assert "could not be read" in f[("WARN", "Gold row_filters")]["detail"] and "Check by hand" in f[("WARN", "Gold row_filters")]["fix"]
    assert "row_filters not checked" in f[("PASS", "Gold objects Fabric will not show")]["detail"]

    # 16b. the live spelling: table_catalog / table_schema instead of catalog_name / schema_name; unknown columns = WARN naming them
    s = dirty_state()
    s["mask_columns"] = ["table_catalog", "table_schema", "table_name", "filter_name"]
    f = findings(run(base, s)[3])
    assert "fact_secure (row filter)" in f[("WARN", "Gold objects Fabric will not show")]["detail"], f.keys()
    s = clean_state()
    s["mask_columns"] = ["cat", "sch", "tab"]
    f = findings(run(base, s)[3])
    assert "columns are cat, sch, tab" in f[("WARN", "Gold row_filters")]["detail"], f[("WARN", "Gold row_filters")]

    # 17. names with a backtick are quoted, never break out of the identifier
    s = clean_state()
    s["catalogs"] = [{"catalog_name": "pr`od", "catalog_owner": "catalog-admins"}]
    _, _, _, g = run({**base, "catalog": "pr`od"}, s)
    assert g["before"]["plan"][0]["sql"] == f"GRANT USE CATALOG ON CATALOG `pr``od` TO {qsp}", g["before"]["plan"][0]

    print("fabric_gold_only_notebook: all checks passed")


if __name__ == "__main__":
    check()
