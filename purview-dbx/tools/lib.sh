# Shared helpers for purview-dbx tools. Source it; set TOOL=<name> first.
# bash 3.2 compatible (macOS /bin/bash): no associative arrays, no ${x,,}, no mapfile.

PVD_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PVD_LOGS="$PVD_ROOT/logs"
PVD_EVIDENCE="$PVD_ROOT/evidence"
mkdir -p "$PVD_LOGS" "$PVD_EVIDENCE"
: "${TOOL:=tool}"

DBX_RESOURCE="2ff814a6-3304-4ab8-85cb-cd0e6f879c1d"   # Azure Databricks app id (F113)

state() { python3 "$PVD_ROOT/tools/state.py" "$@"; }

# Mask bearer tokens, Databricks PATs, SAS signatures and passwords before anything is logged.
mask() {
  sed -E \
    -e 's/eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]+/******/g' \
    -e 's/dapi[0-9a-f]{32}(-[0-9]+)?/******/g' \
    -e 's/(sig=)[^&" ]+/\1******/g' \
    -e 's/("?([Pp]assword|[Ss]ecret|[Tt]oken|accessToken|registrationKey|authKey)"?[[:space:]]*[:=][[:space:]]*"?)[^", ]+/\1******/g'
}

log() {
  printf '%s [%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$TOOL" "$*" | mask | tee -a "$PVD_LOGS/$TOOL.log" >&2
}

die() {
  log "ERROR: $*"
  state error "${AGENT:-$TOOL}" "$TOOL: $*" >/dev/null 2>&1 || true
  exit 1
}

need() { command -v "$1" >/dev/null 2>&1 || die "$1 not found on PATH"; }

# Change tools run as a dry run unless --approved-by "<name> <date>" is given.
# Usage: parse_approval "$@"; then check $APPROVED_BY.
APPROVED_BY=""
parse_approval() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --approved-by) APPROVED_BY="${2:-}"; shift ;;
    esac
    shift
  done
  if [ -n "$APPROVED_BY" ]; then
    case "$APPROVED_BY" in
      *" "20[0-9][0-9]-[0-1][0-9]-[0-3][0-9]*|*" "[0-3][0-9]" "*" "20[0-9][0-9]*) : ;;
      *) die "--approved-by needs \"<name> <date>\", e.g. \"Gokul 2026-11-02\"" ;;
    esac
  fi
}

record_approval() { # $1 = wave or change name
  python3 - "$PVD_ROOT/tools/state.py" "$1" "$APPROVED_BY" <<'PY'
import json, subprocess, sys, time
st, change, by = sys.argv[1:4]
cur = json.loads(subprocess.check_output([sys.executable, st, "get", "approvals"]) or b"[]") or []
cur.append({"change": change, "approved_by": by, "at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())})
subprocess.check_call([sys.executable, st, "set", "approvals", json.dumps(cur)])
PY
}

# Azure Resource Graph through ARM REST (no extension needed). $1 = KQL. Prints the data array.
arg_query() {
  local body
  body=$(python3 -c 'import json,sys;print(json.dumps({"query":sys.argv[1],"options":{"resultFormat":"objectArray"}}))' "$1")
  az rest --method post \
    --url "https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01" \
    --body "$body" --query data -o json
}

# Databricks REST with an Entra token from the Azure CLI. The token never reaches argv or the log.
# Usage: status=$(dbx_call <GET|POST|PATCH|PUT|DELETE> <https://host/path> <out-file> [json-body])
#   writes the response body to <out-file>, prints the HTTP status (000 = no connection).
dbx_call() {
  local method="$1" url="$2" out="$3" body="${4:-}" tok status
  tok=$(az account get-access-token --resource "$DBX_RESOURCE" --query accessToken -o tsv 2>/dev/null) || die "no Databricks token (az login first)"
  if [ -n "$body" ]; then
    status=$(curl -sS --max-time 60 -o "$out" -w '%{http_code}' -X "$method" "$url" \
      -H 'Content-Type: application/json' --data "$body" \
      --config - <<<"header = \"Authorization: Bearer $tok\"" 2>/dev/null) || status="000"
  else
    status=$(curl -sS --max-time 60 -o "$out" -w '%{http_code}' -X "$method" "$url" \
      --config - <<<"header = \"Authorization: Bearer $tok\"" 2>/dev/null) || status="000"
  fi
  tok=""
  log "$method ${url#https://} -> $status"
  echo "$status"
}
