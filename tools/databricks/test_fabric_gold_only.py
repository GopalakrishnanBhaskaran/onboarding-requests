#!/usr/bin/env python3
"""Offline tests for tools/databricks/fabric_gold_only_notebook.py.

Runs the notebook source against stand-ins for spark, dbutils, display, pandas and the Databricks SDK.
GRANT and REVOKE change the stand-in grants, so the re-audit after apply is tested too. All data is SAMPLE.

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


class Exit(Exception):
    def __init__(self, value):
        self.value = value


class Row(dict):
    def asDict(self):
        return dict(self)


class Result:
    def __init__(self, data):
        self.data = [Row(d) for d in data]

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
        "scim": [{"displayName": "sp-fabric-mirror-prod", "applicationId": SP, "active": True, "groups": [{"display": "data-readers"}]}],
        "external_access": True,
        "fail_sql": None,
    }


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
                raise Exception("PERMISSION_DENIED: SAMPLE failure\nmore detail")
            self.apply(m.group(1), m.group(2), m.group(3), parts(m.group(4)), parts(m.group(6))[0])
            return Result([])
        if query.startswith(("GRANT", "REVOKE")):
            raise AssertionError(f"unparsed statement: {query}")
        if "current_user()" in query:
            return Result([{"me": self.s["me"]}])
        if "is_account_group_member" in query:
            return Result([{"m": args["g"] in self.s["my_groups"]}])
        table = re.search(r"system\.information_schema\.(\w+)", query).group(1)
        data = {
            "catalogs": [c for c in self.s["catalogs"] if c["catalog_name"] == args.get("c")],
            "schemata": self.s["schemata"],
            "catalog_privileges": self.s["cat_privs"],
            "schema_privileges": self.schema_rows(),
            "table_privileges": self.s["table_privs"],
            "tables": self.s["tables"],
            "row_filters": self.s["row_filters"],
            "column_masks": self.s["column_masks"],
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
        same = lambda r: all(norm(r.get(k)) == norm(v) for k, v in row.items() if k != "inherited_from")
        self.s[key] = [r for r in self.s[key] if not same(r)]
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
            return {"external_access_enabled": self.s["external_access"]}
        if path.endswith("/unity-catalog/schemas"):
            assert self.kwargs.get("azure_client_id") == SP and self.kwargs.get("azure_client_secret") == SECRET, "not signed in as the SP"
            assert self.kwargs.get("auth_type") == "azure-client-secret"
            who = {SP, "account users"} | {g["display"] for g in self.s["scim"][0]["groups"]}
            spark = Spark(self.s)
            seen = {r["schema_name"] for r in spark.schema_rows()
                    if r["grantee"] in who and norm(r["privilege_type"]) in ("USE SCHEMA", "ALL PRIVILEGES", "BROWSE")}
            return {"schemas": [{"name": n} for n in sorted(seen)]}
        raise AssertionError(path)


def run(widgets: dict, state: dict):
    """Run the notebook; return (summary or exception, executed statements, printed text)."""
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


def findings(g, which="before"):
    res = g[which]
    return {(f["level"], f["check"]): f for f in res["findings"]}


def check() -> None:
    base = {"sp_application_id": SP.upper(), "catalog": "PROD", "gold_schema": "gold"}

    # 1. plan on a dirty catalog: nothing runs, every problem is named, the plan is in the right order
    res, executed, text, g = run(base, dirty_state())
    assert executed == [], executed
    assert res["mode"] == "plan" and res["fail"] > 0 and res["statements_run"] == 0, res
    f = findings(g)
    assert ("FAIL", "SP rights on the whole catalog") in f and "EXTERNAL USE SCHEMA" in f[("FAIL", "SP rights on the whole catalog")]["detail"]
    assert ("FAIL", "SP rights on gold") in f
    assert "silver_cleansed" in f[("FAIL", "SP rights outside gold")]["detail"]
    assert "bronze.raw_orders" in f[("FAIL", "SP rights outside gold")]["detail"]
    assert "silver_cleansed.orders" in f[("FAIL", "SP owns objects outside gold")]["detail"]
    via = f[("FAIL", "Rights the SP gets through a group")]["detail"]
    assert "data-readers: SELECT on silver_curated" in via and "data-readers: USE SCHEMA on silver_curated" in via, via
    assert f[("WARN", "Minor rights the SP gets through a group")]["detail"] == "account users: USE SCHEMA on bronze"
    assert OTHER_SP in f[("WARN", "Other principals with EXTERNAL USE SCHEMA outside gold")]["detail"]
    assert "dev" in f[("WARN", "SP rights in other catalogs")]["detail"]
    skipped = f[("WARN", "Gold objects Fabric will not show")]["detail"]
    assert all(x in skipped for x in ("v_sales (VIEW)", "ext_csv (external CSV)", "fact_secure (row filter)")), skipped
    assert "mv_kpi" in f[("INFO", "Gold materialized views / streaming tables")]["detail"]
    assert ("WARN", "Extra SP right on gold") in f
    sql = [p["sql"] for p in g["before"]["plan"]]
    q = f"`{SP}`"
    assert sql[:4] == [
        f"GRANT USE CATALOG ON CATALOG `prod` TO {q}",
        f"GRANT USE SCHEMA ON SCHEMA `prod`.`gold` TO {q}",
        f"GRANT SELECT ON SCHEMA `prod`.`gold` TO {q}",
        f"GRANT EXTERNAL USE SCHEMA ON SCHEMA `prod`.`gold` TO {q}",
    ], sql
    assert sql[-2:] == [f"REVOKE EXTERNAL USE SCHEMA ON CATALOG `prod` FROM {q}", f"REVOKE SELECT ON CATALOG `prod` FROM {q}"], sql
    assert f"REVOKE MODIFY ON SCHEMA `prod`.`gold` FROM {q}" in sql
    assert f"REVOKE USE SCHEMA ON SCHEMA `prod`.`silver_cleansed` FROM {q}" in sql
    assert f"REVOKE SELECT ON TABLE `prod`.`bronze`.`raw_orders` FROM {q}" in sql
    assert not any("data-readers" in s or "account users" in s for s in sql), "group grants must never be changed"
    assert g["before"]["plan"][0]["status"] == "already held" and g["before"]["plan"][2]["status"] == "new"

    # 2. apply: same statements in the same order, then the re-audit shows only what needs a person
    res, executed, text, g = run({**base, "mode": "apply", "approved_by": "SAMPLE approver 05 Nov 2026"}, dirty_state())
    assert executed == sql, executed
    assert res["statements_run"] == len(sql) and res["statements_failed"] == 0, res
    after = findings(g, "after")
    for ok in ("SP rights on the catalog", "SP rights on gold", "SP rights outside gold"):
        assert ("PASS", ok) in after, (ok, after.keys())
    left = {c for (lvl, c) in after if lvl == "FAIL"}
    assert left == {"SP owns objects outside gold", "Rights the SP gets through a group"}, left
    assert "approved by: SAMPLE approver 05 Nov 2026" in text

    # 3. apply without approved_by, a bad id, half-filled secret widgets: refused before anything runs
    for bad, msg in (
        ({**base, "mode": "apply"}, "approved_by"),
        ({**base, "sp_application_id": "sp-fabric"}, "GUID"),
        ({**base, "sp_secret_scope": "kv"}, "both"),
    ):
        res, executed, _, _ = run(bad, dirty_state())
        assert isinstance(res, ValueError) and msg in str(res) and executed == [], (bad, res)

    # 4. apply by someone who is not the catalog owner: refused, nothing changed
    s = dirty_state()
    s["my_groups"] = set()
    res, executed, _, _ = run({**base, "mode": "apply", "approved_by": "SAMPLE"}, s)
    assert isinstance(res, RuntimeError) and "only the catalog owner" in str(res) and executed == [], res

    # 5. a failing statement stops the run and is reported
    s = dirty_state()
    s["fail_sql"] = "GRANT SELECT ON SCHEMA"
    res, executed, _, g = run({**base, "mode": "apply", "approved_by": "SAMPLE"}, s)
    assert len(executed) == 3 and res["statements_failed"] == 1, (executed, res)
    assert g["applied"][-1]["result"] == "FAILED: PERMISSION_DENIED: SAMPLE failure"

    # 6. clean catalog: no FAIL, nothing new to do; fail_on_findings does not trip
    res, executed, _, g = run({**base, "fail_on_findings": "yes"}, clean_state())
    assert not isinstance(res, Exception) and res["fail"] == 0, (res, [x for x in g["before"]["findings"] if x["level"] == "FAIL"])
    assert all(p["status"] == "already held" for p in g["before"]["plan"]), g["before"]["plan"]

    # 7. drift in plan mode with fail_on_findings = yes: the job fails
    res, executed, _, _ = run({**base, "fail_on_findings": "yes"}, dirty_state())
    assert isinstance(res, RuntimeError) and "FAIL line" in str(res) and executed == [], res

    # 8. the check signed in as the SP: sees silver before, gold only after apply; the secret never printed
    secret_widgets = {"sp_secret_scope": "SAMPLE-scope", "sp_secret_key": "SAMPLE-key"}
    s = dirty_state()
    s["scim"][0]["groups"] = []
    res, _, _, g = run({**base, **secret_widgets}, s)
    seen = findings(g)[("FAIL", "Schemas the SP can see (signed in as the SP)")]["detail"]
    assert "silver_cleansed" in seen and "bronze" in seen, seen
    s = dirty_state()
    s["scim"][0]["groups"] = []
    s["schema_privs"] = [r for r in s["schema_privs"] if r["grantee"] != "account users"]
    res, _, _, g = run({**base, **secret_widgets, "mode": "apply", "approved_by": "SAMPLE"}, s)
    assert ("PASS", "Schemas the SP can see (signed in as the SP)") in findings(g, "after"), findings(g, "after").keys()

    # 9. metastore external access off, SP missing from the workspace, SP owns the catalog
    s = clean_state()
    s["external_access"] = False
    s["scim"] = []
    s["catalogs"][0]["catalog_owner"] = SP
    s["my_groups"] = set()
    res, _, _, g = run(base, s)
    f = findings(g)
    assert ("FAIL", "Metastore external data access") in f and ("FAIL", "Service principal in this workspace") in f
    assert ("FAIL", "Service principal owns the catalog") in f and ("WARN", "Runner is not the catalog owner") in f

    # 10. names with a backtick are quoted, never break out of the identifier
    s = clean_state()
    s["catalogs"] = [{"catalog_name": "pr`od", "catalog_owner": "catalog-admins"}]
    res, _, _, g = run({**base, "catalog": "pr`od"}, s)
    assert g["before"]["plan"][0]["sql"] == f"GRANT USE CATALOG ON CATALOG `pr``od` TO {q}", g["before"]["plan"][0]

    print("fabric_gold_only_notebook: all checks passed")


if __name__ == "__main__":
    check()
