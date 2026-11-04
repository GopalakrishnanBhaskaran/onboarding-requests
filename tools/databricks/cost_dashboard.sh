#!/usr/bin/env bash
# PEST cost-monitoring AI/BI dashboard: deploy, refresh schedule and optional daily-cost alert. Azure Cloud Shell (Bash).
#
#   cost_dashboard.sh plan     --host <workspace URL> --warehouse <id|name> [--folder <path>] [--name <name>]
#   cost_dashboard.sh validate --host <workspace URL> --warehouse <id|name>          runs every dataset once (warehouse cost)
#   cost_dashboard.sh apply    --host <workspace URL> --warehouse <id|name> --approved-by "<name> <date>"
#                              [--folder <path>] [--name <name>] [--cron "<quartz>"] [--timezone <Java tz id>]
#                              [--individual-permissions] [--alert-threshold <USD> --alert-email <mail>]
#   cost_dashboard.sh status   --host <workspace URL> [--folder <path>] [--name <name>]
#   cost_dashboard.sh self-test                                                    offline, mocked Azure CLI and Databricks
#
# plan      changes nothing and runs no SQL: checks the warehouse, looks for the dashboard, prints what apply would do.
# validate  read-only SQL: runs the 17 datasets and the alert query on the warehouse with the dashboard defaults
#           (last 30 days) through the Statement Execution API, and prints rows and seconds per dataset.
# apply     creates or updates the draft (Lakeview API), publishes it (shared data permissions by default: viewers see the
#           data with YOUR rights, account-wide cost; share it with the FinOps/admin group only), adds the refresh schedule
#           once, and with --alert-threshold and --alert-email creates the daily-cost alert once (Alerts API v2).
# Sign-in: the Azure CLI as a workspace user with SELECT on the system schemas and CAN USE on the warehouse (F113, F116).
# The token stays in memory; curl reads it from a file descriptor, never from the command line or the output.
# Files: tools/databricks/cost_dashboard/ (SQL, dashboard file, alert). Guide: docs/DATABRICKS_COST_DASHBOARD.md.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PKG="$HERE/cost_dashboard"
DBX_RESOURCE="2ff814a6-3304-4ab8-85cb-cd0e6f879c1d"                    # Azure Databricks resource id (F113)
WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
DBX_TOKEN="" HOST="" WAREHOUSE="" WH_ID="" APPROVED_BY="" ALERT_THRESHOLD="" ALERT_EMAIL=""
FOLDER="/Workspace/Shared/FinOps" NAME="PEST Databricks cost monitor"
CRON="0 15 7 * * ?" TZ_ID="Asia/Kolkata" EMBED=true
ALERT_NAME="PEST daily Databricks cost above threshold"
ALERT_CRON="0 0 14 * * ?" ALERT_TZ="UTC"

say() { printf '%s\n' "$*"; }
mask() { local s="$1"; [[ -n "$DBX_TOKEN" ]] && s="${s//"$DBX_TOKEN"/******}"; printf '%s' "$s"; }
die() { printf 'ERROR: %s\n' "$(mask "$*")" >&2; exit 1; }
http_status() { cat "$WORK/status"; }
api_error() { jq -r '.message // .error_code // .status.error.message // .' "$WORK/body" 2>/dev/null | head -c 400; }
azq() { if [[ -n "${COST_DASH_MOCK:-}" ]]; then mock_az "$@"; else az "$@"; fi; }

# One Databricks call: call METHOD PATH [body]. A request body comes on stdin; the response goes to $WORK/body.
call() {
  local method="$1" path="$2" with_body="${3:-}" status
  if [[ -n "${COST_DASH_MOCK:-}" ]]; then mock_call "$@"; return; fi
  if [[ "$with_body" == body ]]; then
    status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" "$HOST$path" \
      -H @<(printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$DBX_TOKEN") --data-binary @-) || status=000
  else
    status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" "$HOST$path" \
      -H @<(printf 'Authorization: Bearer %s\n' "$DBX_TOKEN")) || status=000
  fi
  printf '%s' "$status" > "$WORK/status"
  [[ "$status" == 2* ]]
}

need_host() {
  [[ -n "$HOST" ]] || die "--host <workspace URL> is required, for example https://adb-1234567890123456.7.azuredatabricks.net"
  HOST="${HOST%/}"
  [[ "$HOST" =~ ^https://[A-Za-z0-9.-]+\.azuredatabricks\.net$ ]] || die "--host must be an Azure Databricks workspace URL (https://adb-<id>.<n>.azuredatabricks.net)"
}

login() {
  DBX_TOKEN=$(azq account get-access-token --resource "$DBX_RESOURCE" --query accessToken -o tsv 2>/dev/null || true)
  [[ -n "$DBX_TOKEN" ]] || die "the Azure CLI gave no Azure Databricks token: run az login (tenant of the PEST workspace) and try again"
  if ! call GET "/api/2.0/sql/warehouses"; then
    case "$(http_status)" in
      401|403) die "the workspace did not accept your sign-in ($(http_status)): you must be a user of this workspace" ;;
      *) die "listing SQL warehouses failed ($(http_status)): $(api_error)" ;;
    esac
  fi
  cp "$WORK/body" "$WORK/warehouses"
}

resolve_warehouse() {
  [[ -n "$WAREHOUSE" ]] || die "--warehouse <id or name> is required (a serverless SQL warehouse is recommended)"
  WH_ID=$(jq -r --arg w "$WAREHOUSE" '[.warehouses[]? | select(.id == $w or .name == $w)] | if length == 1 then .[0].id else "" end' "$WORK/warehouses")
  [[ -n "$WH_ID" ]] || die "no single SQL warehouse with id or name '$WAREHOUSE' in this workspace"
  local info; info=$(jq -r --arg w "$WH_ID" '.warehouses[] | select(.id == $w) |
    "\(.name)\t\(if .enable_serverless_compute then "SERVERLESS" else (.warehouse_type // "?") end)\t\(.cluster_size // "?")\t\(.auto_stop_mins // "?")\t\(.state // "?")"' "$WORK/warehouses")
  IFS=$'\t' read -r WH_NAME WH_TYPE WH_SIZE WH_STOP WH_STATE <<<"$info"
  say "SQL warehouse: $WH_NAME ($WH_ID) $WH_TYPE $WH_SIZE, auto-stop $WH_STOP min, now $WH_STATE."
  [[ "$WH_TYPE" == SERVERLESS ]] || say "  Advice: use a serverless SQL warehouse (Learn: recommended when available); it starts in seconds and stops after its auto-stop."
  [[ "$WH_STOP" =~ ^[0-9]+$ && "$WH_STOP" -le 10 && "$WH_STOP" -gt 0 ]] || say "  Advice: auto-stop 5-10 min keeps the refresh cost to minutes a day."
}

# Draft dashboard with this name in this folder (the list API returns no path, so each name match is read once).
find_dashboard() {
  DASH_ID="" DASH_ETAG=""
  local token="" ids id
  while :; do
    call GET "/api/2.0/lakeview/dashboards?page_size=1000${token:+&page_token=$token}" || die "listing dashboards failed ($(http_status)): $(api_error)"
    ids=$(jq -r --arg n "$NAME" '.dashboards[]? | select(.display_name == $n) | .dashboard_id' "$WORK/body")
    token=$(jq -r '.next_page_token // empty' "$WORK/body")
    for id in $ids; do
      call GET "/api/2.0/lakeview/dashboards/$id" || continue
      if [[ "$(jq -r '.parent_path // ""' "$WORK/body")" == "$FOLDER" ]]; then
        DASH_ID="$id"; DASH_ETAG=$(jq -r '.etag // ""' "$WORK/body"); return 0
      fi
    done
    [[ -n "$token" ]] || return 0
  done
}

dashboard_file() {
  [[ -f "$PKG/cost_dashboard.lvdash.json" ]] || die "missing $PKG/cost_dashboard.lvdash.json"
  if command -v python3 >/dev/null; then
    python3 "$PKG/build_dashboard.py" --check >/dev/null || die "the dashboard file is older than the SQL: run python3 $PKG/build_dashboard.py"
  fi
}

plan_cmd() {
  need_host; login; resolve_warehouse; dashboard_file; find_dashboard
  say ""
  say "Plan (nothing changed, no SQL run):"
  if [[ -n "$DASH_ID" ]]; then say "  - update the draft dashboard '$NAME' ($DASH_ID) in $FOLDER from cost_dashboard.lvdash.json"
  else say "  - create the folder $FOLDER if needed and the draft dashboard '$NAME' there"; fi
  if $EMBED; then say "  - publish with shared data permissions (your credentials): viewers see account-wide cost; share with the FinOps/admin group only"
  else say "  - publish with individual data permissions: every viewer needs SELECT on the system tables (sql/../grants.sql)"; fi
  say "  - refresh schedule '$NAME daily' if missing: quartz '$CRON' ($TZ_ID) on warehouse $WH_ID"
  if [[ -n "$ALERT_THRESHOLD" ]]; then say "  - alert '$ALERT_NAME': yesterday's cost > $ALERT_THRESHOLD USD, quartz '$ALERT_CRON' ($ALERT_TZ), mail ${ALERT_EMAIL:-<--alert-email missing>}"
  else say "  - no alert (add --alert-threshold <USD> --alert-email <mail>)"; fi
  say ""
  say "Cost: no Azure resource. Each refresh runs 17 queries on the warehouse (minutes of DBUs a day); 'validate' shows the seconds."
  say "Next: bash $0 validate --host $HOST --warehouse $WH_ID   then   apply ... --approved-by \"<name> <date>\""
}

# Runs one SQL file through the Statement Execution API with the dashboard defaults; prints one line.
run_sql_file() {
  local file="$1" label params state id t0 t1 i
  label=$(basename "$file" .sql)
  params=$(jq -n --arg s "$(date -u -d '-30 days' +%F 2>/dev/null || date -u -v-30d +%F)" --arg e "$(date -u +%F)" \
    --rawfile q "$file" '[(if ($q|test(":start_date\\b")) then {name:"start_date", value:$s, type:"DATE"} else empty end),
                          (if ($q|test(":end_date\\b")) then {name:"end_date", value:$e, type:"DATE"} else empty end)]')
  t0=$(date +%s)
  jq -n --arg w "$WH_ID" --rawfile q "$file" --argjson p "$params" \
    '{warehouse_id:$w, statement:($q|sub(";\\s*$";"")), parameters:$p, wait_timeout:"50s", on_wait_timeout:"CONTINUE",
      format:"JSON_ARRAY", disposition:"INLINE", row_limit:1000, query_tags:[{key:"app", value:"pest-cost-dashboard-validate"}]}' \
    | call POST "/api/2.0/sql/statements" body || { say "  FAIL $label: request refused ($(http_status)): $(api_error)"; return 1; }
  id=$(jq -r '.statement_id' "$WORK/body"); state=$(jq -r '.status.state' "$WORK/body")
  i=0
  while [[ "$state" == PENDING || "$state" == RUNNING ]] && (( i < 120 )); do   # up to 10 min
    sleep "${COST_DASH_POLL:-5}"; i=$((i + 1))
    call GET "/api/2.0/sql/statements/$id" || break
    state=$(jq -r '.status.state' "$WORK/body")
  done
  t1=$(date +%s)
  if [[ "$state" == SUCCEEDED ]]; then
    say "  PASS $label: $(jq -r '.manifest.total_row_count // 0' "$WORK/body") rows, $((t1 - t0)) s"
  else
    say "  FAIL $label: $state $(jq -r '.status.error.message // ""' "$WORK/body" | head -c 300)"; return 1
  fi
}

validate_cmd() {
  need_host; login; resolve_warehouse
  say ""; say "Running every dataset once (last 30 days) on $WH_NAME:"
  local f fails=0
  for f in "$PKG"/sql/*.sql "$PKG/alerts/daily_cost_alert.sql"; do run_sql_file "$f" || fails=$((fails + 1)); done
  if (( fails == 0 )); then say "validate passed: every dataset runs on this warehouse"; else say "validate: $fails failed"; exit 1; fi
}

apply_cmd() {
  [[ -n "$APPROVED_BY" ]] || die "apply creates and publishes a dashboard that shows account-wide cost: run plan, get the approval, then add --approved-by \"<name> <date>\""
  if [[ -n "$ALERT_THRESHOLD" || -n "$ALERT_EMAIL" ]]; then
    [[ "$ALERT_THRESHOLD" =~ ^[0-9]+([.][0-9]+)?$ && -n "$ALERT_EMAIL" ]] || die "the alert needs both --alert-threshold <USD number> and --alert-email <mail>"
  fi
  need_host; login; resolve_warehouse; dashboard_file; find_dashboard
  say "Approved by: $APPROVED_BY"
  jq -n --arg p "$FOLDER" '{path:$p}' | call POST "/api/2.0/workspace/mkdirs" body || die "creating $FOLDER failed ($(http_status)): $(api_error)"
  if [[ -n "$DASH_ID" ]]; then
    jq -n --arg n "$NAME" --arg w "$WH_ID" --arg e "$DASH_ETAG" --rawfile s "$PKG/cost_dashboard.lvdash.json" \
      '{display_name:$n, warehouse_id:$w, serialized_dashboard:$s} + (if $e != "" then {etag:$e} else {} end)' \
      | call PATCH "/api/2.0/lakeview/dashboards/$DASH_ID" body || die "updating the draft failed ($(http_status)): $(api_error)"
    say "Draft updated: $NAME ($DASH_ID)."
  else
    jq -n --arg n "$NAME" --arg w "$WH_ID" --arg p "$FOLDER" --rawfile s "$PKG/cost_dashboard.lvdash.json" \
      '{display_name:$n, warehouse_id:$w, parent_path:$p, serialized_dashboard:$s}' \
      | call POST "/api/2.0/lakeview/dashboards" body || die "creating the draft failed ($(http_status)): $(api_error)"
    DASH_ID=$(jq -r '.dashboard_id' "$WORK/body"); say "Draft created: $NAME ($DASH_ID) in $FOLDER."
  fi
  jq -n --argjson e "$EMBED" --arg w "$WH_ID" '{embed_credentials:$e, warehouse_id:$w}' \
    | call POST "/api/2.0/lakeview/dashboards/$DASH_ID/published" body || die "publishing failed ($(http_status)): $(api_error)"
  say "Published (embed_credentials=$EMBED)."
  call GET "/api/2.0/lakeview/dashboards/$DASH_ID/schedules" || die "reading schedules failed ($(http_status)): $(api_error)"
  if jq -e --arg n "$NAME daily" '.schedules[]? | select(.display_name == $n)' "$WORK/body" >/dev/null; then
    say "Schedule '$NAME daily' already there: $(jq -r --arg n "$NAME daily" '.schedules[] | select(.display_name == $n) | "\(.cron_schedule.quartz_cron_expression) \(.cron_schedule.timezone_id)"' "$WORK/body") (change it on the dashboard: Schedule)."
  else
    jq -n --arg n "$NAME daily" --arg c "$CRON" --arg z "$TZ_ID" --arg w "$WH_ID" \
      '{display_name:$n, cron_schedule:{quartz_cron_expression:$c, timezone_id:$z}, warehouse_id:$w, pause_status:"UNPAUSED"}' \
      | call POST "/api/2.0/lakeview/dashboards/$DASH_ID/schedules" body || die "creating the schedule failed ($(http_status)): $(api_error)"
    say "Schedule created: '$CRON' ($TZ_ID)."
  fi
  if [[ -n "$ALERT_THRESHOLD" ]]; then alert_apply; fi
  say ""
  say "Done. Open Dashboards in the workspace and share '$NAME' with the FinOps/admin group (Share > CAN RUN)."
}

alert_apply() {
  local token="" found=""
  while :; do
    call GET "/api/2.0/alerts?page_size=100${token:+&page_token=$token}" || die "listing alerts failed ($(http_status)): $(api_error)"
    found=$(jq -r --arg n "$ALERT_NAME" '[.alerts[]? | select(.display_name == $n) | .id] | first // empty' "$WORK/body")
    token=$(jq -r '.next_page_token // empty' "$WORK/body")
    [[ -z "$found" && -n "$token" ]] || break
  done
  if [[ -n "$found" ]]; then say "Alert '$ALERT_NAME' already there ($found): change the threshold on the alert page."; return; fi
  jq -n --arg n "$ALERT_NAME" --arg w "$WH_ID" --arg p "$FOLDER" --rawfile q "$PKG/alerts/daily_cost_alert.sql" \
    --argjson t "$ALERT_THRESHOLD" --arg m "$ALERT_EMAIL" --arg c "$ALERT_CRON" --arg z "$ALERT_TZ" \
    '{display_name:$n, query_text:($q|sub(";\\s*$";"")), warehouse_id:$w, parent_path:$p,
      evaluation:{comparison_operator:"GREATER_THAN", source:{name:"cost_usd"}, threshold:{value:{double_value:$t}},
                  empty_result_state:"OK", notification:{notify_on_ok:false, subscriptions:[{user_email:$m}]}},
      schedule:{quartz_cron_schedule:$c, timezone_id:$z, pause_status:"UNPAUSED"}}' \
    | call POST "/api/2.0/alerts" body || die "creating the alert failed ($(http_status)): $(api_error)"
  say "Alert created: yesterday's cost > $ALERT_THRESHOLD USD mails $ALERT_EMAIL ('$ALERT_CRON' $ALERT_TZ)."
}

status_cmd() {
  need_host; login; find_dashboard
  [[ -n "$DASH_ID" ]] || { say "No dashboard '$NAME' in $FOLDER."; return 0; }
  say "Dashboard '$NAME' ($DASH_ID) in $FOLDER, warehouse $(jq -r '.warehouse_id' "$WORK/body")."
  if call GET "/api/2.0/lakeview/dashboards/$DASH_ID/published"; then
    say "  published $(jq -r '.revision_create_time // "?"' "$WORK/body"), embed_credentials=$(jq -r '.embed_credentials' "$WORK/body")"
  else say "  not published"; fi
  call GET "/api/2.0/lakeview/dashboards/$DASH_ID/schedules" &&
    jq -r '.schedules[]? | "  schedule \(.display_name): \(.cron_schedule.quartz_cron_expression) \(.cron_schedule.timezone_id) \(.pause_status)"' "$WORK/body"
}

# ---- offline self-test (mocked Azure CLI and Databricks) --------------------------------------------------------------

mock_az() {
  printf 'az %s\n' "$*" >> "$COST_DASH_MOCK/calls.log"
  case "$*" in "account get-access-token"*) printf 'mock-DBX-token-7e41\n' ;; *) return 1 ;; esac
}

mock_call() {
  local method="$1" path="$2" with_body="${3:-}" m="$COST_DASH_MOCK" in="" code=200 out='{}'
  [[ "$with_body" == body ]] && in=$(cat)
  printf 'dbx %s %s\n' "$method" "$path" >> "$m/calls.log"
  [[ -n "$in" ]] && printf '%s' "$in" > "$m/last_${method}_$(printf '%s' "$path" | tr -c 'a-z0-9' '_')"
  case "$method $path" in
    "GET /api/2.0/sql/warehouses") out='{"warehouses":[{"id":"wh0001","name":"SAMPLE serverless","enable_serverless_compute":true,"cluster_size":"2X-Small","auto_stop_mins":10,"state":"STOPPED"}]}' ;;
    "GET /api/2.0/lakeview/dashboards?"*)
      if [[ -f "$m/dash" ]]; then out='{"dashboards":[{"dashboard_id":"d0001","display_name":"PEST Databricks cost monitor"}]}'; else out='{"dashboards":[]}'; fi ;;
    "GET /api/2.0/lakeview/dashboards/d0001") out='{"dashboard_id":"d0001","parent_path":"/Workspace/Shared/FinOps","etag":"7","warehouse_id":"wh0001"}' ;;
    "POST /api/2.0/workspace/mkdirs") ;;
    "POST /api/2.0/lakeview/dashboards") touch "$m/dash"; out='{"dashboard_id":"d0001"}' ;;
    "PATCH /api/2.0/lakeview/dashboards/d0001") ;;
    "POST /api/2.0/lakeview/dashboards/d0001/published") out='{"embed_credentials":true}' ;;
    "GET /api/2.0/lakeview/dashboards/d0001/published") out='{"embed_credentials":true,"revision_create_time":"2026-11-04T00:00:00Z"}' ;;
    "GET /api/2.0/lakeview/dashboards/d0001/schedules")
      if [[ -f "$m/sched" ]]; then out='{"schedules":[{"display_name":"PEST Databricks cost monitor daily","cron_schedule":{"quartz_cron_expression":"0 15 7 * * ?","timezone_id":"Asia/Kolkata"},"pause_status":"UNPAUSED"}]}'; else out='{"schedules":[]}'; fi ;;
    "POST /api/2.0/lakeview/dashboards/d0001/schedules") touch "$m/sched"; out='{"schedule_id":"s1"}' ;;
    "GET /api/2.0/alerts?"*) if [[ -f "$m/alert" ]]; then out='{"alerts":[{"id":"a1","display_name":"PEST daily Databricks cost above threshold"}]}'; else out='{"alerts":[]}'; fi ;;
    "POST /api/2.0/alerts") touch "$m/alert"; out='{"id":"a1"}' ;;
    "POST /api/2.0/sql/statements")
      if [[ "$in" == *"bad_column_for_test"* ]]; then out='{"statement_id":"x","status":{"state":"FAILED","error":{"message":"UNRESOLVED_COLUMN bad_column_for_test"}}}'
      else out='{"statement_id":"q1","status":{"state":"PENDING"}}'; fi ;;
    "GET /api/2.0/sql/statements/q1") out='{"statement_id":"q1","status":{"state":"SUCCEEDED"},"manifest":{"total_row_count":3}}' ;;
    *) code=404; out='{"message":"mock: no route"}' ;;
  esac
  printf '%s' "$out" > "$WORK/body"; printf '%s' "$code" > "$WORK/status"
  [[ "$code" == 2* ]]
}

# shellcheck disable=SC2016,SC2064
self_test() {
  local m out all="" fails=0 token="mock-DBX-token-7e41" H=(--host https://adb-1234567890123456.7.azuredatabricks.net --warehouse wh0001)
  check() { if eval "$2"; then say "  PASS $1"; else say "  FAIL $1"; fails=$((fails + 1)); fi; }
  run() { local o rc; o=$(COST_DASH_MOCK="$m" COST_DASH_POLL=0 bash "$0" "$@" 2>&1) && rc=0 || rc=$?; all+="$o"; printf '%s' "$o"; return "$rc"; }
  nwrites() { grep -E '^dbx (POST|PATCH|PUT|DELETE) ' "$m/calls.log" | grep -vc '/sql/statements' || true; }
  m=$(mktemp -d); trap "rm -rf '$WORK' '$m'" EXIT; : > "$m/calls.log"
  say "self-test:"
  check "host must be an Azure Databricks URL" '! run plan --host https://example.com --warehouse wh0001 >/dev/null'
  out=$(run plan "${H[@]}")
  check "plan: no write, no SQL" '[[ "$(nwrites)" == 0 ]] && ! grep -q sql/statements "$m/calls.log"'
  check "plan: says it will create the dashboard and publish with shared data permissions" '[[ "$out" == *"create the folder"* && "$out" == *"shared data permissions"* ]]'
  check "unknown warehouse refused" '! run plan --host https://adb-1.2.azuredatabricks.net --warehouse nope >/dev/null'
  check "apply without --approved-by refused, nothing written" '! run apply "${H[@]}" >/dev/null && [[ "$(nwrites)" == 0 ]]'
  check "alert needs threshold and mail" '! run apply "${H[@]}" --approved-by "SAMPLE 2026-11-04" --alert-threshold 500 >/dev/null && [[ "$(nwrites)" == 0 ]]'
  out=$(run apply "${H[@]}" --approved-by "SAMPLE 2026-11-04" --alert-threshold 500 --alert-email finops@sample.example)
  check "apply: draft created in the folder with the dashboard file" 'grep -q "^dbx POST /api/2.0/lakeview/dashboards$" "$m/calls.log" && jq -e ".parent_path == \"/Workspace/Shared/FinOps\" and (.serialized_dashboard | fromjson | .datasets | length) == 17" "$m/last_POST__api_2_0_lakeview_dashboards" >/dev/null'
  check "apply: published with embedded credentials" 'jq -e ".embed_credentials == true" "$m/last_POST__api_2_0_lakeview_dashboards_d0001_published" >/dev/null'
  check "apply: schedule 07:15 Asia/Kolkata" 'jq -e ".cron_schedule.quartz_cron_expression == \"0 15 7 * * ?\" and .cron_schedule.timezone_id == \"Asia/Kolkata\"" "$m/last_POST__api_2_0_lakeview_dashboards_d0001_schedules" >/dev/null'
  check "apply: alert GREATER_THAN 500 on cost_usd, no parameters in its query" 'jq -e ".evaluation.comparison_operator == \"GREATER_THAN\" and .evaluation.source.name == \"cost_usd\" and .evaluation.threshold.value.double_value == 500 and (.query_text | test(\":start_date|:end_date\") | not)" "$m/last_POST__api_2_0_alerts" >/dev/null'
  : > "$m/calls.log"
  out=$(run apply "${H[@]}" --approved-by "SAMPLE 2026-11-04" --alert-threshold 500 --alert-email finops@sample.example)
  check "second apply: PATCH with etag, no second schedule or alert" 'grep -q "^dbx PATCH /api/2.0/lakeview/dashboards/d0001$" "$m/calls.log" && ! grep -qE "^dbx POST .*/(schedules|alerts)$" "$m/calls.log" && jq -e ".etag == \"7\"" "$m/last_PATCH__api_2_0_lakeview_dashboards_d0001" >/dev/null'
  out=$(run apply "${H[@]}" --approved-by "SAMPLE 2026-11-04" --individual-permissions)
  check "--individual-permissions publishes without embedded credentials" 'jq -e ".embed_credentials == false" "$m/last_POST__api_2_0_lakeview_dashboards_d0001_published" >/dev/null'
  out=$(run validate "${H[@]}")
  check "validate: every SQL file and the alert run" '[[ $(grep -c "  PASS " <<<"$out") == $(( $(ls "$PKG"/sql/*.sql | wc -l) + 1 )) && "$out" == *"validate passed"* ]]'
  check "validate: the alert query (run last) is sent without parameters" 'jq -e ".parameters == [] and .warehouse_id == \"wh0001\"" "$m/last_POST__api_2_0_sql_statements" >/dev/null'
  out=$(run status --host https://adb-1234567890123456.7.azuredatabricks.net)
  check "status: published and scheduled" '[[ "$out" == *"embed_credentials=true"* && "$out" == *"0 15 7 * * ?"* ]]'
  check "the Databricks token never appears in any output" '[[ "$all" != *"$token"* ]]'
  if (( fails == 0 )); then say "self-test passed"; else say "self-test: $fails failed"; exit 1; fi
}

# ---- main -----------------------------------------------------------------------------------------------------------

CMD="${1:-}"; shift || true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --host) HOST="${2:?workspace URL}"; shift 2 ;;
    --warehouse) WAREHOUSE="${2:?warehouse id or name}"; shift 2 ;;
    --folder) FOLDER="${2:?workspace folder}"; shift 2 ;;
    --name) NAME="${2:?dashboard name}"; shift 2 ;;
    --cron) CRON="${2:?quartz cron}"; shift 2 ;;
    --timezone) TZ_ID="${2:?Java time zone id}"; shift 2 ;;
    --individual-permissions) EMBED=false; shift ;;
    --alert-threshold) ALERT_THRESHOLD="${2:?USD}"; shift 2 ;;
    --alert-email) ALERT_EMAIL="${2:?mail}"; shift 2 ;;
    --approved-by) APPROVED_BY="${2:?name and date}"; shift 2 ;;
    *) die "unknown argument $1 (see the header of this script)" ;;
  esac
done
case "$CMD" in
  plan) plan_cmd ;;
  validate) validate_cmd ;;
  apply) apply_cmd ;;
  status) status_cmd ;;
  self-test) self_test ;;
  *) die "usage: $(basename "$0") plan|validate|apply|status|self-test [options] (see the header of this script)" ;;
esac
