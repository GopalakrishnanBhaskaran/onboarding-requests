#!/usr/bin/env python3
"""Tests for the PEST cost dashboard SQL and dashboard file.

Offline checks (standard library only):
  * generated files (16, 17, .lvdash.json) are up to date
  * every dataset uses the same list-price join, filters billing by usage_date,
    and the waste datasets start with the 13 standard columns
  * the dashboard file references only datasets, fields and parameters that exist
  * the alert SQL has no parameter markers (Learn: alerts do not support parameters)

Spark checks (need PySpark 4.x and Java 17+; skipped with a note otherwise):
  * the documented system-table schemas are created as local tables
    (system.<schema>.<table> -> <schema>.<table>; nothing else in the SQL changes)
  * SAMPLE rows trigger every waste rule; each dataset runs with ANSI mode on and
    the results are compared with values worked out by hand in this file

Run:   python3 tools/databricks/cost_dashboard/test_cost_dashboard.py [--require-spark]
"""
from __future__ import annotations

import datetime as dt
import json
import re
import subprocess
import sys
import tempfile
from decimal import Decimal
from pathlib import Path

HERE = Path(__file__).resolve().parent
SQL = HERE / "sql"
sys.path.insert(0, str(HERE))
import build_dashboard as bd  # noqa: E402

START, END = dt.date(2026, 10, 5), dt.date(2026, 11, 3)   # 30 days
FAILURES: list[str] = []


def check(cond: bool, msg: str) -> None:
    print(("PASS " if cond else "FAIL ") + msg)
    if not cond:
        FAILURES.append(msg)


def approx(a, b, tol=0.011) -> bool:
    return a is not None and abs(float(a) - float(b)) <= tol


# ----------------------------------------------------------------------------- offline
def offline_checks() -> None:
    rc = subprocess.run([sys.executable, str(HERE / "build_dashboard.py"), "--check"],
                        capture_output=True, text=True)
    check(rc.returncode == 0, "generated files are up to date (" + rc.stdout.strip() + ")")

    priced_ref = None
    for path in sorted(SQL.glob("*.sql")):
        text = path.read_text()
        if path.stem in ("16_recommendations", "17_top_identities"):
            continue
        m = re.search(r"priced AS \(\n(.*?)\n  WHERE u\.usage_date", text, re.S)
        check(m is not None, f"{path.name}: has the priced CTE with a usage_date filter")
        if m:
            block = "\n".join(l for l in m.group(1).splitlines() if not l.strip().startswith("--"))
            priced_ref = priced_ref or block
            check(block == priced_ref, f"{path.name}: list-price join identical to the other datasets")
        check("pricing.effective_list.default" in text, f"{path.name}: uses pricing.effective_list.default")
        code = re.sub(r"'(?:[^']|'')*'", "''", re.sub(r"--[^\n]*", "", text)).rstrip()
        check(";" not in code[:-1], f"{path.name}: one statement only")

    dash = json.loads(bd.OUT.read_text())
    names = {d["name"]: d for d in dash["datasets"]}
    check(len(names) == len(bd.DATASETS), "dashboard: dataset names are unique")
    for d in dash["datasets"]:
        sql = "".join(d["queryLines"])
        stem = next(s for s, _ in bd.DATASETS if bd.DS[s] == d["name"])
        check(sql == (SQL / f"{stem}.sql").read_text(), f"dashboard: dataset {stem} matches its SQL file")
        declared = {p["keyword"] for p in d.get("parameters", [])}
        used = set(bd.params_used(sql))
        check(declared == used, f"dashboard: dataset {stem} declares exactly the parameters it uses {sorted(used)}")
    widget_names = set()
    for page in dash["pages"]:
        for item in page["layout"]:
            w = item["widget"]
            check(w["name"] not in widget_names, f"dashboard: widget name {w['name']} unique")
            widget_names.add(w["name"])
            pos = item["position"]
            check(pos["x"] + pos["width"] <= 6, f"dashboard: widget {w['name']} fits the 6-column grid")
            for q in w.get("queries", []):
                ds = names.get(q["query"]["datasetName"])
                check(ds is not None, f"dashboard: widget {w['name']} uses a known dataset")
                for p in q["query"].get("parameters", []):
                    check(p["keyword"] in {x["keyword"] for x in ds.get("parameters", [])},
                          f"dashboard: filter {w['name']} parameter {p['keyword']} exists on its dataset")
            spec = w.get("spec", {})
            if spec.get("widgetType", "").startswith("filter-date"):
                stems = {q["query"]["datasetName"] for q in w["queries"]}
                page_ds = {q["query"]["datasetName"] for it in page["layout"]
                           for q in it["widget"].get("queries", []) if q["name"] == "main_query"}
                param = w["queries"][0]["query"]["parameters"][0]["keyword"]
                needing = {n for n in page_ds if param in {x["keyword"] for x in names[n].get("parameters", [])}}
                check(needing <= stems, f"dashboard: {page['displayName']} {param} filter reaches every dataset on the page")

    alert = (HERE / "alerts" / "daily_cost_alert.sql").read_text()
    check(":" not in re.sub(r"--.*", "", alert).replace("::", ""), "alert SQL has no parameter markers")

    try:  # optional second parser: sqlglot, Databricks dialect
        import sqlglot
        for path in sorted(SQL.glob("*.sql")) + [HERE / "alerts" / "daily_cost_alert.sql"]:
            try:
                n = len(sqlglot.parse(path.read_text(), read="databricks"))
                check(n == 1, f"{path.name}: sqlglot (databricks dialect) parses one statement")
            except sqlglot.errors.ParseError as exc:
                check(False, f"{path.name}: sqlglot parse: {str(exc).splitlines()[0][:200]}")
    except ImportError:
        print("SKIP sqlglot parse (pip install sqlglot)")


# ----------------------------------------------------------------------------- Spark
SCHEMAS = {
    "billing.usage": """record_id STRING, account_id STRING, workspace_id STRING, sku_name STRING, cloud STRING,
        usage_start_time TIMESTAMP, usage_end_time TIMESTAMP, usage_date DATE, custom_tags MAP<STRING,STRING>,
        usage_unit STRING, usage_quantity DECIMAL(38,18),
        usage_metadata STRUCT<cluster_id: STRING, job_id: STRING, warehouse_id: STRING, instance_pool_id: STRING,
          node_type: STRING, job_run_id: STRING, notebook_id: STRING, dlt_pipeline_id: STRING, endpoint_name: STRING,
          endpoint_id: STRING, dlt_update_id: STRING, dlt_maintenance_id: STRING, metastore_id: STRING,
          run_name: STRING, job_name: STRING, notebook_path: STRING, usage_policy_id: STRING>,
        identity_metadata STRUCT<run_as: STRING, owned_by: STRING, created_by: STRING>,
        record_type STRING, ingestion_date DATE, billing_origin_product STRING,
        product_features STRUCT<jobs_tier: STRING, sql_tier: STRING, dlt_tier: STRING, is_serverless: BOOLEAN,
          is_photon: BOOLEAN, serving_type: STRING, performance_target: STRING>,
        usage_type STRING""",
    "billing.list_prices": """price_start_time TIMESTAMP, price_end_time TIMESTAMP, account_id STRING, sku_name STRING,
        cloud STRING, currency_code STRING, usage_unit STRING,
        pricing STRUCT<`default`: DECIMAL(30,18), promotional: STRUCT<`default`: DECIMAL(30,18)>,
          effective_list: STRUCT<`default`: DECIMAL(30,18)>>""",
    "access.workspaces_latest": """account_id STRING, workspace_id STRING, workspace_name STRING, workspace_url STRING,
        create_time TIMESTAMP, status STRING""",
    "compute.clusters": """account_id STRING, workspace_id STRING, cluster_id STRING, cluster_name STRING, owned_by STRING,
        create_time TIMESTAMP, delete_time TIMESTAMP, driver_node_type STRING, worker_node_type STRING,
        worker_count BIGINT, min_autoscale_workers BIGINT, max_autoscale_workers BIGINT,
        auto_termination_minutes BIGINT, enable_elastic_disk BOOLEAN, tags MAP<STRING,STRING>, cluster_source STRING,
        init_scripts ARRAY<STRING>, aws_attributes STRUCT<availability: STRING>,
        azure_attributes STRUCT<first_on_demand: BIGINT, availability: STRING, spot_bid_max_price: DOUBLE>,
        gcp_attributes STRUCT<availability: STRING>, driver_instance_pool_id STRING, worker_instance_pool_id STRING,
        dbr_version STRING, change_time TIMESTAMP, change_date DATE, data_security_mode STRING, policy_id STRING""",
    "compute.node_timeline": """account_id STRING, workspace_id STRING, cluster_id STRING, instance_id STRING,
        start_time TIMESTAMP, end_time TIMESTAMP, driver BOOLEAN, cpu_user_percent DOUBLE, cpu_system_percent DOUBLE,
        cpu_wait_percent DOUBLE, mem_used_percent DOUBLE, mem_swap_percent DOUBLE, network_sent_bytes BIGINT,
        network_received_bytes BIGINT, disk_free_bytes_per_mount_point MAP<STRING,BIGINT>, node_type STRING,
        private_ip STRING""",
    "compute.warehouses": """warehouse_id STRING, workspace_id STRING, account_id STRING, warehouse_name STRING,
        warehouse_type STRING, warehouse_channel STRING, warehouse_size STRING, min_clusters INT, max_clusters INT,
        auto_stop_minutes INT, tags MAP<STRING,STRING>, change_time TIMESTAMP, delete_time TIMESTAMP""",
    "compute.warehouse_events": """account_id STRING, workspace_id STRING, warehouse_id STRING, event_type STRING,
        cluster_count INT, event_time TIMESTAMP""",
    "lakeflow.jobs": """account_id STRING, workspace_id STRING, job_id STRING, name STRING, description STRING,
        creator_id STRING, tags MAP<STRING,STRING>, change_time TIMESTAMP, delete_time TIMESTAMP, run_as STRING,
        trigger_type STRING, run_as_user_name STRING, creator_user_name STRING, paused BOOLEAN,
        timeout_seconds BIGINT, create_time TIMESTAMP""",
    "lakeflow.pipelines": """account_id STRING, workspace_id STRING, pipeline_id STRING, pipeline_type STRING, name STRING,
        created_by STRING, run_as STRING, tags MAP<STRING,STRING>, configuration MAP<STRING,STRING>,
        change_time TIMESTAMP, delete_time TIMESTAMP, create_time TIMESTAMP""",
    "lakeflow.job_run_timeline": """account_id STRING, workspace_id STRING, job_id STRING, run_id STRING,
        period_start_time TIMESTAMP, period_end_time TIMESTAMP, trigger_type STRING, run_type STRING, run_name STRING,
        compute_ids ARRAY<STRING>, result_state STRING, termination_code STRING, job_parameters MAP<STRING,STRING>""",
    "lakeflow.job_task_run_timeline": """account_id STRING, workspace_id STRING, job_id STRING, run_id STRING,
        job_run_id STRING, parent_run_id STRING, period_start_time TIMESTAMP, period_end_time TIMESTAMP,
        task_key STRING, compute_ids ARRAY<STRING>, result_state STRING, termination_code STRING""",
    "query.history": """account_id STRING, workspace_id STRING, statement_id STRING, session_id STRING,
        execution_status STRING, compute STRUCT<type: STRING, cluster_id: STRING, warehouse_id: STRING>,
        executed_by_user_id STRING, executed_by STRING, statement_text STRING, statement_type STRING,
        client_application STRING, total_duration_ms BIGINT, start_time TIMESTAMP, end_time TIMESTAMP,
        update_time TIMESTAMP""",
}

ACC = "SAMPLE-account"
WS_DEV, WS_PROD, WS_FAR = "1001", "1002", "1003"
SP = "11111111-2222-3333-4444-555555555555"   # SAMPLE service principal application id
PRICES = {  # sku: (default, effective_list)  USD per DBU, SAMPLE values
    "PREMIUM_ALL_PURPOSE_COMPUTE": (0.55, 0.55),
    "PREMIUM_JOBS_COMPUTE": (0.30, 0.30),
    "PREMIUM_JOBS_SERVERLESS_COMPUTE": (0.45, 0.35),        # promotional: effective 0.35
    "PREMIUM_SERVERLESS_SQL_COMPUTE": (0.70, 0.70),
    "PREMIUM_SQL_PRO_COMPUTE": (0.55, 0.55),
    "PREMIUM_DLT_CORE_COMPUTE": (0.30, 0.30),
    "PREMIUM_SERVERLESS_REAL_TIME_INFERENCE": (0.07, 0.07),
}


def ts(day: str, hour: int, minute: int = 0) -> dt.datetime:
    d = dt.date.fromisoformat(day)
    return dt.datetime(d.year, d.month, d.day, hour, minute)


class Data:
    def __init__(self):
        self.rows = {k: [] for k in SCHEMAS}
        self.n = 0

    def usage(self, ws, sku, product, day, hour, dbus, *, cluster=None, job=None, run=None, warehouse=None,
              pipeline=None, endpoint=None, run_as=None, owned_by=None, tags=None, serverless=None,
              record_type="ORIGINAL", job_name=None):
        self.n += 1
        start = ts(day, hour)
        md = {k: None for k in ("cluster_id", "job_id", "warehouse_id", "instance_pool_id", "node_type", "job_run_id",
                                "notebook_id", "dlt_pipeline_id", "endpoint_name", "endpoint_id", "dlt_update_id",
                                "dlt_maintenance_id", "metastore_id", "run_name", "job_name", "notebook_path",
                                "usage_policy_id")}
        md.update(cluster_id=cluster, job_id=job, job_run_id=run, warehouse_id=warehouse, dlt_pipeline_id=pipeline,
                  endpoint_name=endpoint, job_name=job_name)
        self.rows["billing.usage"].append({
            "record_id": f"SAMPLE-{self.n}", "account_id": ACC, "workspace_id": ws, "sku_name": sku, "cloud": "AZURE",
            "usage_start_time": start, "usage_end_time": start + dt.timedelta(hours=1), "usage_date": start.date(),
            "custom_tags": tags or {}, "usage_unit": "DBU", "usage_quantity": Decimal(str(dbus)),
            "usage_metadata": md, "identity_metadata": {"run_as": run_as, "owned_by": owned_by, "created_by": None},
            "record_type": record_type, "ingestion_date": start.date(), "billing_origin_product": product,
            "product_features": {"jobs_tier": None, "sql_tier": None, "dlt_tier": None, "is_serverless": serverless,
                                 "is_photon": False, "serving_type": None, "performance_target": None},
            "usage_type": "COMPUTE_TIME"})

    def cluster(self, ws, cid, name, owner, source, autoterm, deleted=None, change=None):
        change = change or ts("2026-09-01", 0)
        self.rows["compute.clusters"].append({
            "account_id": ACC, "workspace_id": ws, "cluster_id": cid, "cluster_name": name, "owned_by": owner,
            "create_time": change, "delete_time": deleted, "driver_node_type": "Standard_D4ds_v5",
            "worker_node_type": "Standard_D4ds_v5", "worker_count": 1, "min_autoscale_workers": None,
            "max_autoscale_workers": None, "auto_termination_minutes": autoterm, "enable_elastic_disk": True,
            "tags": {}, "cluster_source": source, "init_scripts": [], "aws_attributes": None,
            "azure_attributes": {"first_on_demand": 1, "availability": "ON_DEMAND_AZURE", "spot_bid_max_price": -1.0},
            "gcp_attributes": None, "driver_instance_pool_id": None, "worker_instance_pool_id": None,
            "dbr_version": "16.4.x-scala2.13", "change_time": change, "change_date": change.date(),
            "data_security_mode": "USER_ISOLATION", "policy_id": None})

    def cpu(self, ws, cid, day, hour, pct_by_node: dict[str, float]):
        for node, pct in pct_by_node.items():
            for minute in (0, 20, 40):
                st = ts(day, hour, minute)
                self.rows["compute.node_timeline"].append({
                    "account_id": ACC, "workspace_id": ws, "cluster_id": cid, "instance_id": f"{cid}-{node}",
                    "start_time": st, "end_time": st + dt.timedelta(minutes=1), "driver": node == "driver",
                    "cpu_user_percent": pct * 0.8, "cpu_system_percent": pct * 0.2, "cpu_wait_percent": 0.0,
                    "mem_used_percent": 30.0, "mem_swap_percent": 0.0, "network_sent_bytes": 1,
                    "network_received_bytes": 1, "disk_free_bytes_per_mount_point": {"/": 1}, "node_type": "Standard_D4ds_v5",
                    "private_ip": "10.0.0.1"})

    def warehouse(self, ws, wid, name, wtype, autostop, deleted=None):
        self.rows["compute.warehouses"].append({
            "warehouse_id": wid, "workspace_id": ws, "account_id": ACC, "warehouse_name": name, "warehouse_type": wtype,
            "warehouse_channel": "CURRENT", "warehouse_size": "SMALL", "min_clusters": 1, "max_clusters": 1,
            "auto_stop_minutes": autostop, "tags": {}, "change_time": ts("2026-09-01", 0), "delete_time": deleted})

    def wh_event(self, ws, wid, event, when):
        self.rows["compute.warehouse_events"].append({
            "account_id": ACC, "workspace_id": ws, "warehouse_id": wid, "event_type": event, "cluster_count": 1,
            "event_time": when})

    def query(self, ws, wid, start, end):
        self.n += 1
        self.rows["query.history"].append({
            "account_id": ACC, "workspace_id": ws, "statement_id": f"SAMPLE-q{self.n}", "session_id": "s",
            "execution_status": "FINISHED", "compute": {"type": "WAREHOUSE", "cluster_id": None, "warehouse_id": wid},
            "executed_by_user_id": "1", "executed_by": "SAMPLE-user@sample.example", "statement_text": "<REDACTED>",
            "statement_type": "SELECT", "client_application": "Databricks SQL Editor", "total_duration_ms": 1,
            "start_time": start, "end_time": end, "update_time": end})

    def job(self, ws, jid, name, run_as):
        self.rows["lakeflow.jobs"].append({
            "account_id": ACC, "workspace_id": ws, "job_id": jid, "name": name, "description": None, "creator_id": "1",
            "tags": {}, "change_time": ts("2026-09-01", 0), "delete_time": None, "run_as": "1",
            "trigger_type": "CRON", "run_as_user_name": run_as, "creator_user_name": run_as, "paused": False,
            "timeout_seconds": 0, "create_time": ts("2026-09-01", 0)})

    def run_slice(self, ws, jid, rid, start, end, state=None, code=None, trigger="CRON", compute=None):
        self.rows["lakeflow.job_run_timeline"].append({
            "account_id": ACC, "workspace_id": ws, "job_id": jid, "run_id": rid, "period_start_time": start,
            "period_end_time": end, "trigger_type": trigger, "run_type": "JOB_RUN", "run_name": None,
            "compute_ids": compute or [], "result_state": state, "termination_code": code, "job_parameters": {}})

    def task_slice(self, ws, jid, task_run, job_run, start, end, clusters, state="SUCCEEDED"):
        self.rows["lakeflow.job_task_run_timeline"].append({
            "account_id": ACC, "workspace_id": ws, "job_id": jid, "run_id": task_run, "job_run_id": job_run,
            "parent_run_id": job_run, "period_start_time": start, "period_end_time": end, "task_key": "t",
            "compute_ids": clusters, "result_state": state, "termination_code": "SUCCESS"})


def build_sample() -> Data:
    d = Data()
    # Prices: one price change for all-purpose on 1 Oct 2026 (0.50 before).
    for sku, (default, eff) in PRICES.items():
        start = ts("2026-10-01", 0) if sku == "PREMIUM_ALL_PURPOSE_COMPUTE" else ts("2025-01-01", 0)
        d.rows["billing.list_prices"].append({
            "price_start_time": start, "price_end_time": None, "account_id": ACC, "sku_name": sku, "cloud": "AZURE",
            "currency_code": "USD", "usage_unit": "DBU",
            "pricing": {"default": Decimal(str(default)), "promotional": {"default": Decimal(str(eff))},
                        "effective_list": {"default": Decimal(str(eff))}}})
    d.rows["billing.list_prices"].append({
        "price_start_time": ts("2025-01-01", 0), "price_end_time": ts("2026-10-01", 0), "account_id": ACC,
        "sku_name": "PREMIUM_ALL_PURPOSE_COMPUTE", "cloud": "AZURE", "currency_code": "USD", "usage_unit": "DBU",
        "pricing": {"default": Decimal("0.50"), "promotional": {"default": Decimal("0.50")},
                    "effective_list": {"default": Decimal("0.50")}}})
    for ws, name in ((WS_DEV, "SAMPLE-pest-dev"), (WS_PROD, "SAMPLE-pest-prod"), (WS_FAR, "SAMPLE-pest-far")):
        d.rows["access.workspaces_latest"].append({
            "account_id": ACC, "workspace_id": ws, "workspace_name": name,
            "workspace_url": f"https://adb-{ws}.SAMPLE.azuredatabricks.net", "create_time": ts("2025-01-01", 0),
            "status": "RUNNING"})
    AP, JOBS, SQLP = "PREMIUM_ALL_PURPOSE_COMPUTE", "PREMIUM_JOBS_COMPUTE", "PREMIUM_SQL_PRO_COMPUTE"

    # Item 1: AP1 auto-termination off; 10 hours on 10 Oct at 2 DBU (1.10 USD/h); 4 busy, 6 idle.
    d.cluster(WS_DEV, "ap1", "SAMPLE-ap1-no-autoterm", "alice@sample.example", "UI", 60, change=ts("2026-08-01", 0))
    d.cluster(WS_DEV, "ap1", "SAMPLE-ap1-no-autoterm", "alice@sample.example", "UI", 0)   # latest row: off
    for h in range(9, 19):
        d.usage(WS_DEV, AP, "ALL_PURPOSE", "2026-10-10", h, 2, cluster="ap1", run_as="creator@sample.example")
        d.cpu(WS_DEV, "ap1", "2026-10-10", h, {"driver": 50.0 if h < 13 else 1.0})
    # Previous period (Sept, old price 0.50): 2 DBU -> 1.00 USD.
    d.usage(WS_DEV, AP, "ALL_PURPOSE", "2026-09-20", 10, 2, cluster="ap1", run_as="creator@sample.example")
    # Item 1: AP2 auto-termination 120; 4 hours on 11 Oct at 4 DBU (2.20 USD/h); 2 busy, 2 idle.
    d.cluster(WS_DEV, "ap2", "SAMPLE-ap2-autoterm-120", "carol@sample.example", "API", 120)
    for h in range(10, 14):
        d.usage(WS_DEV, AP, "ALL_PURPOSE", "2026-10-11", h, 4, cluster="ap2",
                tags={"CostCenter": "CC-100", "Owner": "team-c"})
        d.cpu(WS_DEV, "ap2", "2026-10-11", h, {"driver": 60.0 if h < 12 else 2.0})
    # Item 4: jobs J9 (30 min at 10:00) and J10 (45 min at 11:00) ran on AP2.
    d.job(WS_DEV, "J9", "SAMPLE-job-on-ap-1", "dave@sample.example")
    d.job(WS_DEV, "J10", "SAMPLE-job-on-ap-2", "dave@sample.example")
    d.task_slice(WS_DEV, "J9", "t9", "r9", ts("2026-10-11", 10, 0), ts("2026-10-11", 10, 30), ["ap2"])
    d.task_slice(WS_DEV, "J10", "t10", "r10", ts("2026-10-11", 11, 0), ts("2026-10-11", 11, 45), ["ap2"])

    # Item 2: AP3 auto-termination 30; 8 busy, 9 low (max 15, avg 10), 10 idle, 11 no CPU data.
    d.cluster(WS_PROD, "ap3", "SAMPLE-ap3-ok", "erin@sample.example", "UI", 30)
    for h in range(8, 12):
        d.usage(WS_PROD, AP, "ALL_PURPOSE", "2026-10-12", h, 2, cluster="ap3",
                tags={"CostCenter": "CC-100", "Owner": "team-a"})
    # Correction on the 08:00 record: retraction + restatement, net unchanged.
    d.usage(WS_PROD, AP, "ALL_PURPOSE", "2026-10-12", 8, -2, cluster="ap3", record_type="RETRACTION",
            tags={"CostCenter": "CC-100", "Owner": "team-a"})
    d.usage(WS_PROD, AP, "ALL_PURPOSE", "2026-10-12", 8, 2, cluster="ap3", record_type="RESTATEMENT",
            tags={"CostCenter": "CC-100", "Owner": "team-a"})
    d.cpu(WS_PROD, "ap3", "2026-10-12", 8, {"driver": 70.0, "worker": 50.0})
    d.cpu(WS_PROD, "ap3", "2026-10-12", 9, {"driver": 15.0, "worker": 5.0})
    d.cpu(WS_PROD, "ap3", "2026-10-12", 10, {"driver": 2.0, "worker": 1.0})
    # Item 2: job J2 on classic job compute, low CPU for 2 hours at 5 DBU (1.50 USD/h).
    d.cluster(WS_PROD, "job-j2-run", "job-J2-run-1", "frank@sample.example", "JOB", 0)
    d.job(WS_PROD, "J2", "SAMPLE-low-cpu-job", "frank@sample.example")
    for h in (2, 3):
        d.usage(WS_PROD, JOBS, "JOBS", "2026-10-14", h, 5, cluster="job-j2-run", job="J2", run="R21",
                run_as="frank@sample.example", serverless=False)
        d.cpu(WS_PROD, "job-j2-run", "2026-10-14", h, {"driver": 12.0, "worker": 8.0})
    d.run_slice(WS_PROD, "J2", "R21", ts("2026-10-14", 2), ts("2026-10-14", 3), compute=["job-j2-run"])
    d.run_slice(WS_PROD, "J2", "R21", ts("2026-10-14", 3), ts("2026-10-14", 4), "SUCCEEDED", "SUCCESS",
                compute=["job-j2-run"])

    # Item 6b: J1 classic job compute, four 60-min runs (0.90 USD) and one 180-min run (2.70 USD).
    d.job(WS_PROD, "J1", "SAMPLE-long-job", "grace@sample.example")
    for i, day in enumerate(("2026-10-15", "2026-10-16", "2026-10-17", "2026-10-18")):
        rid = f"R1{i}"
        d.usage(WS_PROD, JOBS, "JOBS", day, 1, 3, cluster=f"job-j1-{i}", job="J1", run=rid, serverless=False,
                run_as="grace@sample.example")
        d.run_slice(WS_PROD, "J1", rid, ts(day, 1), ts(day, 2), "SUCCEEDED", "SUCCESS")
    for h in (1, 2, 3):
        d.usage(WS_PROD, JOBS, "JOBS", "2026-10-19", h, 3, cluster="job-j1-4", job="J1", run="R14",
                serverless=False, run_as="grace@sample.example")
    d.run_slice(WS_PROD, "J1", "R14", ts("2026-10-19", 1), ts("2026-10-19", 2))
    d.run_slice(WS_PROD, "J1", "R14", ts("2026-10-19", 2), ts("2026-10-19", 3))
    d.run_slice(WS_PROD, "J1", "R14", ts("2026-10-19", 3), ts("2026-10-19", 4), "SUCCEEDED", "SUCCESS")

    # Item 6a: J3 serverless (effective price 0.35): R31 failed (0.70), R32 failed then repaired (0.70 + 0.70).
    d.job(WS_PROD, "J3", "SAMPLE-flaky-job", SP)
    sl = "PREMIUM_JOBS_SERVERLESS_COMPUTE"
    tags3 = {"COST-CENTER": "CC-300", "owner": "team-x"}
    d.usage(WS_PROD, sl, "JOBS", "2026-10-15", 5, 2, job="J3", run="R31", run_as=SP, serverless=True, tags=tags3)
    d.run_slice(WS_PROD, "J3", "R31", ts("2026-10-15", 5), ts("2026-10-15", 5, 50), "FAILED", "RUN_EXECUTION_ERROR")
    d.usage(WS_PROD, sl, "JOBS", "2026-10-16", 5, 2, job="J3", run="R32", run_as=SP, serverless=True, tags=tags3)
    d.usage(WS_PROD, sl, "JOBS", "2026-10-16", 7, 2, job="J3", run="R32", run_as=SP, serverless=True, tags=tags3)
    d.run_slice(WS_PROD, "J3", "R32", ts("2026-10-16", 5), ts("2026-10-16", 5, 40), "FAILED", "CLUSTER_ERROR")
    d.run_slice(WS_PROD, "J3", "R32", ts("2026-10-16", 7), ts("2026-10-16", 7, 30), "SUCCEEDED", "SUCCESS")

    # Item 3: WH1 serverless, auto-stop 60; up 3 h (20 Oct) + 2 h (21 Oct) at 4 DBU (2.80 USD/h); busy 1 h each day.
    d.warehouse(WS_DEV, "wh1", "SAMPLE-wh1-autostop-60", "SERVERLESS", 60)
    for day, hours in (("2026-10-20", (9, 10, 11)), ("2026-10-21", (9, 10))):
        d.wh_event(WS_DEV, "wh1", "STARTING", ts(day, 9))
        d.wh_event(WS_DEV, "wh1", "RUNNING", ts(day, 9, 1))
        d.wh_event(WS_DEV, "wh1", "STOPPING", ts(day, hours[-1] + 1) - dt.timedelta(minutes=1))
        d.wh_event(WS_DEV, "wh1", "STOPPED", ts(day, hours[-1] + 1))
        d.query(WS_DEV, "wh1", ts(day, 9, 5), ts(day, 9, 10))
        for h in hours:
            d.usage(WS_DEV, "PREMIUM_SERVERLESS_SQL_COMPUTE", "SQL", day, h, 4, warehouse="wh1",
                    owned_by="bob@sample.example", serverless=True, tags={"cost_center": "CC-200"})
    # Item 3: WH2 pro, auto-stop off; 6 hours on 22 Oct at 2 DBU (1.10 USD/h), one busy hour.
    d.warehouse(WS_PROD, "wh2", "SAMPLE-wh2-never-stops", "PRO", 0)
    d.wh_event(WS_PROD, "wh2", "STARTING", ts("2026-09-01", 0))
    for h in range(0, 6):
        d.usage(WS_PROD, SQLP, "SQL", "2026-10-22", h, 2, warehouse="wh2", owned_by="bob@sample.example",
                serverless=False)
    d.query(WS_PROD, "wh2", ts("2026-10-22", 1, 30), ts("2026-10-22", 1, 31))
    # Item 2: WH3 serverless, auto-stop 10 (fine); 14:00 busy, 15:00 idle (2.80 USD).
    d.warehouse(WS_PROD, "wh3", "SAMPLE-wh3-ok", "SERVERLESS", 10)
    for h in (14, 15):
        d.usage(WS_PROD, "PREMIUM_SERVERLESS_SQL_COMPUTE", "SQL", "2026-10-23", h, 4, warehouse="wh3",
                owned_by="bob@sample.example", serverless=True)
    d.query(WS_PROD, "wh3", ts("2026-10-23", 14, 50), ts("2026-10-23", 14, 52))
    # Another region: billing only (no regional rows) -> cost shown, never judged idle.
    d.usage(WS_FAR, "PREMIUM_SERVERLESS_SQL_COMPUTE", "SQL", "2026-10-25", 10, 4, warehouse="wh9",
            owned_by="far@sample.example", serverless=True)
    # Pipeline, serving endpoint, an unpriced SKU.
    d.rows["lakeflow.pipelines"].append({
        "account_id": ACC, "workspace_id": WS_PROD, "pipeline_id": "P1", "pipeline_type": "ETL_PIPELINE",
        "name": "SAMPLE-pipeline", "created_by": "heidi@sample.example", "run_as": "heidi@sample.example",
        "tags": {}, "configuration": {}, "change_time": ts("2026-09-01", 0), "delete_time": None,
        "create_time": ts("2026-09-01", 0)})
    d.usage(WS_PROD, "PREMIUM_DLT_CORE_COMPUTE", "DLT", "2026-10-26", 3, 2, pipeline="P1",
            run_as="heidi@sample.example", serverless=False)
    d.usage(WS_PROD, "PREMIUM_SERVERLESS_REAL_TIME_INFERENCE", "MODEL_SERVING", "2026-10-27", 3, 10,
            endpoint="SAMPLE-endpoint", run_as=SP)
    d.usage(WS_DEV, "SAMPLE_UNKNOWN_SKU", "SQL", "2026-10-28", 3, 1)
    return d


def expected_total(d: Data, start: dt.date, end: dt.date) -> float:
    total = 0.0
    for r in d.rows["billing.usage"]:
        if not (start <= r["usage_date"] <= end):
            continue
        sku = r["sku_name"]
        if sku == "PREMIUM_ALL_PURPOSE_COMPUTE" and r["usage_end_time"] < ts("2026-10-01", 0):
            price = 0.50
        elif sku in PRICES:
            price = PRICES[sku][1]
        else:
            continue
        total += float(r["usage_quantity"]) * price
    return total


def spark_checks() -> None:
    try:
        from pyspark.sql import SparkSession
    except ImportError:
        msg = "SKIP Spark checks: PySpark not installed (python3 -m venv .venv && .venv/bin/pip install pyspark==4.1.1)"
        print(msg)
        if "--require-spark" in sys.argv:
            FAILURES.append(msg)
        return
    tmp = tempfile.mkdtemp(prefix="cost_dashboard_test_")
    spark = (SparkSession.builder.master("local[2]").appName("cost-dashboard-test")
             .config("spark.sql.ansi.enabled", "true").config("spark.sql.session.timeZone", "UTC")
             .config("spark.sql.warehouse.dir", tmp).config("spark.ui.enabled", "false")
             .config("spark.sql.shuffle.partitions", "2").getOrCreate())
    spark.sparkContext.setLogLevel("ERROR")
    d = build_sample()
    # Alert query uses current_date(): SAMPLE serverless SQL usage yesterday (2.80 USD) and 3 days ago (1.40 USD).
    today = dt.date.today()
    for back, dbus in ((1, 4), (3, 2)):
        d.usage(WS_FAR, "PREMIUM_SERVERLESS_SQL_COMPUTE", "SQL", (today - dt.timedelta(days=back)).isoformat(), 6, dbus,
                warehouse="wh9", owned_by="far@sample.example", serverless=True)
    for table, ddl in SCHEMAS.items():
        db = table.split(".")[0]
        spark.sql(f"CREATE DATABASE IF NOT EXISTS {db}")
        spark.createDataFrame(d.rows[table], schema=ddl).write.mode("overwrite").saveAsTable(table)

    columns: dict[str, list[str]] = {}

    def run(stem: str):
        sql = (SQL / f"{stem}.sql").read_text().rstrip().rstrip(";")
        local = re.sub(r"\bsystem\.(billing|access|compute|lakeflow|query)\.", r"\1.", sql)
        check(local.count("system.") == 0, f"{stem}: only system.<schema> prefixes were mapped")
        args = {p: (START if p == "start_date" else END) for p in bd.params_used(sql)}
        try:
            df = spark.sql(local, args=args)
            columns[stem] = df.columns
            rows = [r.asDict() for r in df.collect()]
            check(True, f"{stem}: runs in Spark SQL (ANSI) and returns {len(rows)} rows")
            if stem in bd.WASTE_FILES:
                check(df.columns[:13] == bd.STANDARD_COLUMNS, f"{stem}: first 13 output columns are the standard set")
            return rows
        except Exception as exc:  # noqa: BLE001
            check(False, f"{stem}: runs in Spark SQL: {str(exc).splitlines()[0][:400]}")
            return []

    res = {stem: run(stem) for stem, _ in bd.DATASETS}
    alert_sql = (HERE / "alerts" / "daily_cost_alert.sql").read_text().rstrip().rstrip(";")
    alert = spark.sql(re.sub(r"\bsystem\.(billing|access|compute|lakeflow|query)\.", r"\1.", alert_sql)).collect()
    a = alert[0].asDict() if alert else {}
    y_cost = expected_total(d, today - dt.timedelta(days=1), today - dt.timedelta(days=1))
    avg7 = expected_total(d, today - dt.timedelta(days=8), today - dt.timedelta(days=2)) / 7
    check(len(alert) == 1 and approx(a.get("cost_usd"), y_cost) and approx(a.get("trailing_7d_avg_usd"), avg7),
          f"alert: one row, yesterday {y_cost:.2f} USD, 7-day average {avg7:.2f} (days without usage count as 0): {a}")

    kpi = res["01_kpi_summary"][0] if res["01_kpi_summary"] else {}
    exp = expected_total(d, START, END)
    check(approx(kpi.get("cost_usd"), exp), f"01 total cost {kpi.get('cost_usd')} = {exp:.2f} (effective list, price change)")
    check(approx(kpi.get("previous_period_cost_usd"), 1.00), "01 previous period uses the old 0.50 price -> 1.00")
    check(approx(kpi.get("unpriced_quantity"), 1), "01 one DBU of an unknown SKU is reported as unpriced")
    check(kpi.get("workspaces_with_spend") == 3, "01 three workspaces with spend")

    ws = {r["workspace_id"]: r for r in res["02_cost_by_workspace"]}
    check(res["02_cost_by_workspace"][0]["cost_rank"] == 1 and
          res["02_cost_by_workspace"][0]["cost_usd"] == max(r["cost_usd"] for r in ws.values()),
          "02 ranked highest first")
    check(ws.get(WS_FAR, {}).get("workspace_name") == "SAMPLE-pest-far", "02 names from workspaces_latest")

    mom = res["03_cost_by_workspace_monthly"]
    check(len(mom) == 6 * 3, f"03 six months x three workspaces ({len(mom)} rows)")
    oct_dev = next((r for r in mom if r["workspace_id"] == WS_DEV and r["month_label"] == "2026-10"), {})
    check(approx(oct_dev.get("previous_month_cost_usd"), 1.00), "03 October previous month = September cost")

    cov = {r["workspace_id"]: r["regional_data_visible"] for r in res["05_workspace_coverage"]}
    check(cov.get(WS_FAR) == "No" and cov.get(WS_DEV) == "Yes", "05 other-region workspace marked No")

    cats = {r["compute_category"] for r in res["06_cost_by_product_sku"]}
    check({"All-purpose compute (classic)", "Jobs (serverless)", "Jobs compute (classic)",
           "SQL warehouse (serverless)", "SQL warehouse (pro/classic)", "Lakeflow pipelines / DLT (classic)",
           "Model serving"} <= cats, "06 compute categories cover all-purpose, jobs, SQL, serverless, DLT, serving")

    top = res["07_top_resources"]
    check(all(r["type_rank"] <= 20 for r in top), "07 at most 20 per type")
    names = {(r["resource_type"], r["resource_id"]): r for r in top}
    check(names.get(("All-purpose cluster", "ap1"), {}).get("resource_name") == "SAMPLE-ap1-no-autoterm",
          "07 cluster name from the latest clusters row")
    check(names.get(("SQL warehouse", "wh9"), {}).get("resource_name") == "wh9", "07 other-region warehouse shows its id")
    check(("Job", "J9") not in names, "07 jobs on all-purpose compute are not ranked as jobs (no job_id in billing)")

    ids = {r["identity"]: r for r in res["08_cost_by_identity"]}
    check("alice@sample.example" in ids and "creator@sample.example" not in ids,
          "08 all-purpose cost goes to the cluster owner, not run_as")
    check(ids.get(SP, {}).get("identity_type") == "Service principal", "08 GUID run_as typed as service principal")
    check(ids.get("bob@sample.example", {}).get("attribution_sources") == "warehouse owner",
          "08 SQL warehouse usage attributed to owned_by")
    check(len(res["17_top_identities"]) == min(20, len(ids)), "17 top identities limited to 20")

    tags = {(r["cost_center"], r["owner_tag"]): r for r in res["09_cost_by_tag"]}
    check(("CC-300", "team-x") in tags, "09 tag keys matched case- and punctuation-insensitively (COST-CENTER)")
    check(("CC-200", "(no owner tag)") in tags, "09 missing owner tag shown")

    w1 = {r["resource_id"]: r for r in res["10_waste_cluster_autotermination"]}
    check(set(w1) == {"ap1", "ap2"}, f"10 flags ap1 (off) and ap2 (120 min) only: {sorted(w1)}")
    check(approx(w1.get("ap1", {}).get("estimated_waste_usd"), 6.60), "10 ap1 waste = 6 idle hours x 1.10")
    check(approx(w1.get("ap1", {}).get("estimated_monthly_saving_usd"), 6.05), "10 ap1 saving = 6.60 - 0.5 h x 1.10")
    check(approx(w1.get("ap2", {}).get("estimated_waste_usd"), 4.40), "10 ap2 waste = 2 idle hours x 2.20")
    check(approx(w1.get("ap2", {}).get("estimated_monthly_saving_usd"), 3.30), "10 ap2 saving = 4.40 x (1 - 30/120)")

    w2 = {r["resource_id"]: r for r in res["11_waste_idle_compute"]}
    check(set(w2) == {"ap3", "J2", "wh3"}, f"11 idle compute = ap3, J2, wh3 (items 1 and 3 left out): {sorted(w2)}")
    check(approx(w2.get("ap3", {}).get("estimated_waste_usd"), 1.65), "11 ap3 = idle 1.10 + 50 % of low 1.10")
    check(approx(w2.get("J2", {}).get("estimated_waste_usd"), 1.50), "11 J2 job compute rolled up by job = 50 % x 3.00")
    check(approx(w2.get("wh3", {}).get("estimated_waste_usd"), 2.80), "11 wh3 one billed hour with no statement")

    w3 = {r["resource_id"]: r for r in res["12_waste_warehouse_autostop"]}
    check(set(w3) == {"wh1", "wh2"}, f"12 flags wh1 (60 min serverless) and wh2 (off): {sorted(w3)}")
    check(w3.get("wh1", {}).get("stop_events") == 2 and approx(w3.get("wh1", {}).get("uptime_hours"), 5.0, 0.05),
          "12 wh1 2 stops, 5.0 uptime hours")
    check(approx(w3.get("wh1", {}).get("estimated_waste_usd"), 8.40), "12 wh1 = max(tail 4.67, idle 3 h x 2.80 = 8.40)")
    check(approx(w3.get("wh2", {}).get("estimated_waste_usd"), 5.50), "12 wh2 auto-stop off = 5 idle hours x 1.10")

    w4 = {r["resource_id"]: r for r in res["13_waste_jobs_on_all_purpose"]}
    check(approx(w4.get("J9", {}).get("affected_cost_usd"), 1.10), "13 J9 attributed 30/60 of 2.20")
    check(approx(w4.get("J9", {}).get("estimated_waste_usd"), 0.50), "13 J9 premium (2.20 - 1.20) x 0.5")
    check(approx(w4.get("J10", {}).get("estimated_waste_usd"), 0.75), "13 J10 premium (2.20 - 1.20) x 0.75")

    w5 = res["14_unattributed_spend"]
    check(all(r["estimated_waste_usd"] == 0 and r["estimated_monthly_saving_usd"] == 0 for r in w5),
          "14 unattributed spend claims no saving")
    ap_dev = next((r for r in w5 if r["workspace_id"] == WS_DEV and r["resource_name"] == "All-purpose compute (classic)"), {})
    check(approx(ap_dev.get("affected_cost_usd"), 11.0), "14 dev all-purpose untagged = ap1 10 hours x 1.10")

    w6 = {(r["waste_item"][:2], r["resource_id"]): r for r in res["15_waste_job_runs"]}
    check(approx(w6.get(("6a", "J3"), {}).get("estimated_waste_usd"), 1.40), "15 J3 failed 0.70 + repaired-until-failure 0.70")
    check(approx(w6.get(("6a", "J3"), {}).get("affected_cost_usd"), 2.10), "15 J3 runs cost 2.10 at the effective price")
    check(approx(w6.get(("6b", "J1"), {}).get("estimated_waste_usd"), 1.80), "15 J1 long run 2.70 - median 0.90")
    check(("6b", "J2") not in w6, "15 a job without 3 clean runs gets no long-run baseline")

    dash = json.loads(bd.OUT.read_text())
    by_name = {bd.DS[s]: s for s, _ in bd.DATASETS}
    for page in dash["pages"]:
        for item in page["layout"]:
            for q in item["widget"].get("queries", []):
                stem = by_name[q["query"]["datasetName"]]
                for f in q["query"].get("fields", []):
                    for col in re.findall(r"`([^`]+)`", f["expression"]):
                        if col != "associative_filter_predicate_group":
                            check(col in columns.get(stem, []),
                                  f"dashboard {page['displayName']}: widget field {col} is a column of {stem}")

    rec = res["16_recommendations"]
    n_items = sum(len(res[s]) for s in bd.WASTE_FILES)
    check(len(rec) == n_items, f"16 recommendations = all {n_items} waste rows")
    savings = [r["estimated_monthly_saving_usd"] for r in rec]
    check(savings == sorted(savings, reverse=True), "16 ordered by monthly saving, highest first")
    check(rec and rec[0]["resource_id"] == "wh1" and rec[0]["priority"] == 1, "16 priority 1 = wh1 (8.40)")
    check(all(r["recommended_fix"] for r in rec), "16 every item has a fix")
    keys = [(r["resource_type"], r["resource_id"]) for r in rec if r["waste_item"][0] in "123"]
    check(len(keys) == len(set(keys)), "16 no resource is counted in two of items 1-3")
    spark.stop()


def main() -> int:
    offline_checks()
    spark_checks()
    print(f"\n{'FAILED' if FAILURES else 'self-test passed'}: {len(FAILURES)} failure(s)")
    return 1 if FAILURES else 0


if __name__ == "__main__":
    sys.exit(main())
