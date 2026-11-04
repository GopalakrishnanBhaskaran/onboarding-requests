# Databricks cost dashboard: PEST, all workspaces (AI/BI)

Written 4 Nov 2026. Facts F114–F129 in `docs/VERIFIED_FACTS.md` (Learn read that day).
Separate from the PMO onboarding app: **no Azure resource**. The only cost is the SQL warehouse time the refreshes use.
Package: `tools/databricks/cost_dashboard/` and the deploy script `tools/databricks/cost_dashboard.sh`.

## 1. Which approach: the built-in usage dashboard, this one, or both

| | Built-in usage dashboard (account console) | This custom dashboard |
|---|---|---|
| Set-up | Account console → **Usage** → **Setup dashboard** → choose v2.0 (Preview), *entire account*, target workspace → **Import** (F123) | `cost_dashboard.sh apply` or import `cost_dashboard.lvdash.json` |
| Covers | Spend by product, SKU and custom tag; most expensive usage; v2.0 adds forecasting and object drill-down | Pages 1–3 plus **waste with USD**, **recommendations with monthly saving**, user/SP and cost-centre attribution, region coverage |
| Maintained by | Databricks (re-import for new versions) | PEST (SQL in git, tested) |

**Recommendation: use both, in one admin workspace.**
- Import the built-in v2.0 for the standard spend and forecast view. It takes no build work, and Databricks maintains it.
- Deploy this one for waste and savings, which the built-in dashboard does not cover.

Why one workspace is enough:
- A dashboard lives in one workspace, but the billing tables (`billing.usage`, `billing.list_prices`, `access.workspaces_latest`) are **global**. Every PEST workspace's cost shows from any workspace (F114–F116).
- The compute, jobs and query tables are **regional**. Names (page 2) and waste checks (pages 4–5) cover only workspaces in the dashboard workspace's Azure region.
- The **Region coverage** table on page 1 lists any workspace that is not covered. For those, deploy a second copy from a workspace in that region (`--name "… <region>"`).

Publish with **shared data permissions** (the default) and share the dashboard with the FinOps/admin group only. Viewers then see account-wide cost with the publisher's rights and need no system-table grants (F122).

## 2. Files

| File | What |
|---|---|
| `sql/01…15_*.sql` | One query per dataset; each has a comment header: page, visual, tables, rules, edit points |
| `sql/16_recommendations.sql`, `sql/17_top_identities.sql` | **Generated** from 10–15 and 08 by `build_dashboard.py` (do not edit) |
| `cost_dashboard.lvdash.json` | The dashboard file (generated): 5 pages, 17 datasets, date filters default to the last 30 days |
| `alerts/daily_cost_alert.sql` | Daily-cost alert query (no parameters: alerts do not support them, F124) |
| `grants.sql` | Table-level grants, only if needed (step 2) |
| `build_dashboard.py` | Rebuilds the generated files; `--check` fails if they are stale |
| `test_cost_dashboard.py` | Offline checks plus local Spark SQL tests on SAMPLE system tables |

Cost is always **USD at list price**: `usage_quantity × pricing.effective_list.default` (F115). It is not PEST's negotiated rate, and it excludes the Azure VM cost of classic compute (billed by Azure).

## 3. Layout: dataset → visual → page

Every page has *Start date* and *End date* filters (DATE parameters; defaults `now-30d/d` and `now/d`, UTC).

| # | Dataset | Visual | Page |
|---|---|---|---|
| 01 | `kpi_summary`: cost, previous period, change %, daily average, 30-day run rate, workspaces | 6 counters | 1 Cost by workspace |
| 02 | `cost_by_workspace`: USD per workspace, ranked | Bar + table | 1 |
| 03 | `cost_by_workspace_monthly`: last 6 months, previous month, MoM % | Stacked bar + table | 1 |
| 04 | `cost_daily_by_product` | Line (by product) | 1 |
| 05 | `workspace_coverage`: regional data visible yes/no | Table | 1 |
| 06 | `cost_by_product_sku`: billing_origin_product × sku_name → category (all-purpose, jobs, SQL, serverless, DLT, model serving) | Bar + table | 2 Cost by resource |
| 07 | `top_resources`: top 20 all-purpose clusters, SQL warehouses, jobs, pipelines, serving endpoints | Table + type and workspace filters | 2 |
| 17 | `top_identities` (generated from 08) | Bar (by identity type) | 3 Cost by user |
| 08 | `cost_by_identity`: users and service principals ranked (cluster owner, run_as, owned_by, created_by) | Table | 3 |
| 09 | `cost_by_tag`: cost-centre and owner tag values | Bar + table | 3 |
| 10 | Waste 1: all-purpose auto-termination off or > 60 min | Counter + table | 4 Waste |
| 11 | Waste 2: idle or low-activity clusters and warehouses | Counter + table | 4 |
| 12 | Waste 3: SQL warehouse auto-stop too high | Counter + table | 4 |
| 13 | Waste 4: jobs on all-purpose compute | Counter + table | 4 |
| 14 | Waste 5: unattributed spend (no cost-centre or owner tag) | Counter + table | 4 |
| 15 | Waste 6: failed, repaired or long-running job runs | Counter + table | 4 |
| 16 | `recommendations` (generated): all items, fix, monthly saving, highest first | 3 counters + bar + table + workspace and item filters | 5 Recommendations |

## 4. How waste and saving are estimated

Each cost is counted in **one** item only: item 2 leaves out the resources of items 1 and 3, so the totals do not double count.
Monthly saving = waste in the period × 30 / days in the period.

| Item | Flagged when | Estimated waste | Fix (in the dashboard) |
|---|---|---|---|
| 1 | All-purpose (`cluster_source` UI/API), not deleted, auto-termination 0/NULL or > 60 | Cost of idle billed hours (busiest node < 5 % CPU). Saving: × (1 − 30/setting); if off: minus 30 min per active day | Auto-termination 30 min; policy `"autotermination_minutes": {"type": "range", "minValue": 10, "maxValue": 60, "defaultValue": 30}` (F126) |
| 2 | Other clusters: idle hour (< 5 %) or low hour (average < 20 %); warehouses: billed hour with no statement | Idle 100 % + low 50 % (right-sizing estimate) | Lower auto-termination, fewer/smaller workers, job compute or serverless; warehouses: min clusters 1, merge, serverless |
| 3 | Warehouse auto-stop 0, serverless > 10, pro/classic > 45 (Learn defaults, F118) | Larger of: stops × (setting − target) × cost per uptime hour; idle billed hours | Auto-stop 10 min (serverless) or upgrade to serverless |
| 4 | Job tasks on an all-purpose cluster (`job_task_run_timeline.compute_ids`, F119) | Cluster hour cost shared by task minutes × (1 − jobs price / all-purpose price) | Job compute or serverless jobs; policy `"workload_type.clients.jobs": {"type": "fixed", "value": false}` |
| 5 | Usage without a cost-centre or owner tag | 0 (visibility); the USD at risk is shown | Mandatory `custom_tags.*` in policies, warehouse tags, serverless usage policies |
| 6 | Failed/ERROR/TIMED_OUT runs; repaired runs; runs > 2 × the job's median and ≥ 30 min over it (≥ 3 clean runs) | Failed: run cost; repaired: cost up to the last failure; long: cost above the median | Fix the root cause (termination codes shown), retries with an interval, job timeout and duration warning |

The thresholds are marked `EDIT HERE` in the SQL. After a change, run `python3 tools/databricks/cost_dashboard/build_dashboard.py`.

## 5. Deploy (Azure Cloud Shell, Bash)

**Prerequisites:**
- You are a PEST account admin who is also a **metastore admin** and a user of the target workspace.
- The workspace is Unity Catalog-enabled, in the region of most PEST workspaces.

**Step 1: system schemas.**
- In that workspace's SQL editor, run `SHOW SCHEMAS IN system;`.
- `billing`, `access`, `compute`, `lakeflow` and `query` must be listed.
- If one is missing, enable it (account or metastore admin): `databricks system-schemas enable <metastore-id> <schema>` (F116).

**Step 2: grants (only if needed).**
- Nothing to grant when you publish as an account admin + metastore admin with shared data permissions.
- If a service principal publishes, or you use `--individual-permissions`, run `grants.sql` with your group or SP: table-level SELECT on the 13 tables, F116.

**Step 3: SQL warehouse.**
- Use a **serverless** SQL warehouse (Learn recommends it), size 2X-Small, auto-stop 10 min (5 is the UI minimum), max 1 cluster, with `CostCenter` and `Owner` tags.
- An existing serverless warehouse works too.

**Step 4: plan, validate, apply.**
```bash
az login --tenant <PEST tenant id>          # only if Cloud Shell is in another tenant
cd ~ && { [ -d onboarding-requests ] || git clone -q https://github.com/GopalakrishnanBhaskaran/onboarding-requests.git; }
cd ~/onboarding-requests && git fetch -q origin && git checkout -q claude/amazing-wozniak-3r0xkt && git pull -q
D=tools/databricks/cost_dashboard.sh
bash $D self-test                          # offline: "self-test passed"
python3 tools/databricks/cost_dashboard/test_cost_dashboard.py   # offline part (Spark part skips without PySpark)

H='https://adb-<workspace-id>.<n>.azuredatabricks.net'   # the admin workspace URL
W='<warehouse id or name>'
bash $D plan     --host "$H" --warehouse "$W"            # read-only, no SQL
bash $D validate --host "$H" --warehouse "$W"            # runs all 17 datasets + the alert once on real data: PASS, rows, seconds
bash $D apply    --host "$H" --warehouse "$W" --approved-by "<name> <date>" \
                 --alert-threshold <daily USD> --alert-email <finops mailbox>
bash $D status   --host "$H"
```
- `apply` creates the draft in `/Workspace/Shared/FinOps` (`--folder` to change), publishes it with embedded credentials and adds the refresh schedule.
- A second `apply` updates the draft in place. It never adds a second schedule or alert.

**Step 5: share.**
- Open **Dashboards** → *PEST Databricks cost monitor* → **Share**.
- Give the FinOps/admin group **CAN RUN**. Don't use *Anyone in my account*: the data is account-wide cost.

**Refresh schedule.**
- Default: daily at **07:15 Asia/Kolkata** (`--cron "0 15 7 * * ?" --timezone Asia/Kolkata`, Quartz syntax, F122).
- Billing rows arrive within about 12 hours, so the last UTC day still grows during the morning.
- Each refresh runs 17 queries on the warehouse. `validate` prints the seconds, and the warehouse's own cost appears on page 2.

**Daily-cost alert (optional).**
- `--alert-threshold` creates *PEST daily Databricks cost above threshold*: yesterday's (UTC) account cost > threshold.
- It runs daily at 14:00 UTC (19:30 IST), after the 12-hour availability window, and mails `--alert-email` (Alerts API v2, F124).
- The query also returns the 7-day average and the change %. To alert on a jump instead, set the condition on `change_vs_7d_avg_pct` in the alert page.
- For a monthly view with no warehouse, add an account console budget: **Usage → Budgets → Add budget**, up to 4 monthly thresholds with mail, list price, mail up to 24 h late (F129).

**Manual alternative (no script).**
1. Dashboards → **Create** ▾ → **Import dashboard from file** → `cost_dashboard.lvdash.json`.
2. Choose the warehouse, then **Publish** (Share data permissions).
3. **Schedule** → daily 07:15, Asia/Kolkata.
4. For the alert: Alerts → **Create alert** → paste `alerts/daily_cost_alert.sql` → condition `cost_usd` > threshold → schedule → notifications.

**Rollback.**
- Delete the schedule on the dashboard (**Schedule** → kebab menu → **Delete**), then unpublish (`DELETE /api/2.0/lakeview/dashboards/{id}/published`) and move the draft to the trash (`DELETE /api/2.0/lakeview/dashboards/{id}`, recoverable, F122).
- Delete the alert on its page.
- `REVOKE` the grants if you ran them. Nothing else was created.

## 6. Assumptions (state them when you share the numbers)

- **List price:** list price, not PEST's discounted rate. Classic compute VM cost is extra and is in Azure Cost Management.
- **Tag keys:** cost-centre keys are `CostCenter`/`cost_center`/`CostCentre`; owner keys are `Owner`/`BusinessOwner`/`TechnicalOwner` (any case or punctuation). Edit `tag_keys` in 09 and 14 if PEST uses others.
- **Thresholds:** CPU < 5 % = idle; average < 20 % = low, and 50 % of a low hour is counted as right-sizing saving. These are judgement values; change them at `EDIT HERE`.
- **Estimates:**
  - Jobs on all-purpose compute: approximate by design (Learn, F119).
  - Warehouse tail: counts manual stops too, so it is an upper bound.
  - Long runs: compared with the job's own median.
- **Names:** the latest owner and name of a cluster, job or warehouse are used for the whole period.
- **Region:** the region coverage table shows what the regional tables can see (F116).
- **Not tested on PEST:**
  - The SQL ran on local Spark 4.1 with ANSI mode and SAMPLE rows in the documented schemas, not on PEST data.
  - The dashboard file was not imported into a workspace from here.
  - `validate` is the first live test; the import is the proof for the widget format (F122).

## 7. Test evidence (4 Nov 2026, this repository)

- `python3 tools/databricks/cost_dashboard/test_cost_dashboard.py` with PySpark 4.1.1 and Java 21: **593 checks, 0 failures**.
  - It covers every dataset and the alert.
  - Totals with a price change, effective vs default price, corrections (retraction + restatement), each waste rule with hand-worked values, no double counting, saving order, other-region workspace, tag matching.
  - Also the dashboard file: dataset and parameter references, and all 159 widget fields checked against the real result columns of their dataset; plus the sqlglot (Databricks dialect) parse of all 18 SQL files.
- **Mutation check:** 5 deliberate bugs, all caught by the matching checks:
  - the default price instead of the effective list price;
  - a broken idle threshold;
  - item-3 warehouses counted again in item 2;
  - the job hour share without its 60-minute floor;
  - the tail-only warehouse waste.
- `bash tools/databricks/cost_dashboard.sh self-test`: **17 checks passed** (mocked Azure CLI and Databricks).
  - plan writes nothing; apply needs `--approved-by`; create then update with etag; one schedule and one alert only.
  - The alert query is sent without parameters, and the token is never printed.
