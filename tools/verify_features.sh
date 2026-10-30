#!/usr/bin/env bash
# Feature checklist for the live pilot: PASS / WAIT / FAIL per feature, with the next step. Read-only, prints no secrets.
#   bash tools/verify_features.sh <subscriptionId>            (or: … | tee .local/verify.txt, then paste the file)
# Checks: code deployed, environment settings, mail on and mail delivered, agent provisioning and TAP rights, the PEST and
# Vestacy tenants from the workbooks (no SAMPLE data left), the test row, approvals, agent-created user/group/role,
# sign-in details mailed, end to end.
set -uo pipefail
source "$(dirname "$0")/tool_path.sh" # az on the PATH (Mac: Homebrew), else how to install it
SUB="${1:?subscriptionId}"
RG=rg-pmo-review-pilot
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$ROOT/.local"
az account set --subscription "$SUB"

stamp() { [[ -f "$ROOT/.local/last-$1-deploy" ]] && cut -d' ' -f1 "$ROOT/.local/last-$1-deploy" || true; }
# true when the code a component runs differs between its last deploy from this Cloud Shell and this checkout
changed() {
  local c="$1"; shift
  [[ -n "$c" ]] && git -C "$ROOT" cat-file -e "$c^{commit}" 2>/dev/null && git -C "$ROOT" diff --quiet "$c" HEAD -- "$@" && echo false || echo true
}

W=$(az functionapp list -g "$RG" --query "[?starts_with(name,'func-pmo-worker-')].name | [0]" -o tsv)
SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
MI=$([[ -n "$W" ]] && az functionapp identity show -g "$RG" -n "$W" --query principalId -o tsv || true)
ACS=$(az resource list -g "$RG" --resource-type Microsoft.Communication/communicationServices --query "[0].id" -o tsv 2>/dev/null || true)
COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)

export V_HEAD V_APP V_WORKER V_APP_CHANGED V_WORKER_CHANGED V_TENANT V_WORKER_SETTINGS V_SWA_SETTINGS V_GRAPH V_RBAC V_ACS V_ACS_ROLE V_CONTAINERS
V_HEAD=$(git -C "$ROOT" rev-parse --short HEAD)
V_APP=$(stamp app); V_WORKER=$(stamp worker)
V_APP_CHANGED=$(changed "$V_APP" src/core src/adapters src/api src/web staticwebapp.config.json)
V_WORKER_CHANGED=$(changed "$V_WORKER" src/core src/adapters src/worker)
V_TENANT=$(az account show --query tenantId -o tsv)
# Named settings only (never a connection string).
V_WORKER_SETTINGS=$([[ -n "$W" ]] && az functionapp config appsettings list -g "$RG" -n "$W" \
  --query "[?contains(['APP_ENVIRONMENT','NOTIFY_REDIRECT_TO','AGENT_TENANT_ID','NOTIFY_SENDER','ACS_ENDPOINT','NOTIFY_FROM','AGENT_PROVISIONING','AGENT_ROLE_ASSIGNMENT','ROLE_ASSIGN_ALLOWED_ROLES','ROLE_ASSIGN_SCOPES','AGENT_TAP','AGENT_TAP_LIFETIME_MINUTES','AGENT_PRIVILEGED','AGENT_OFFBOARDING','OFFBOARD_DELETE_USER'], name)].{name:name, value:value}" -o json || echo "[]")
V_SWA_SETTINGS=$([[ -n "$SWA" ]] && az staticwebapp appsettings list -n "$SWA" -g "$RG" \
  --query "properties.{APP_ENVIRONMENT:APP_ENVIRONMENT, NOTIFY_REDIRECT_TO:NOTIFY_REDIRECT_TO, NOTIFY_SENDER:NOTIFY_SENDER, AGENT_PROVISIONING:AGENT_PROVISIONING, AGENT_TAP:AGENT_TAP, AGENT_PRIVILEGED:AGENT_PRIVILEGED, EXTERNAL_UPLOAD_API_ENABLED:EXTERNAL_UPLOAD_API_ENABLED}" -o json 2>/dev/null || echo "{}")
V_GRAPH=$([[ -n "$MI" ]] && az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$MI/appRoleAssignments" --query "value[].appRoleId" -o tsv \
  | while read -r id; do az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?id=='$id'].value | [0]" -o tsv; done || true)
V_RBAC=$([[ -n "$MI" ]] && az role assignment list --assignee "$MI" --role f58310d9-a9f6-439a-9e8d-f62e7b41a168 --all --query "[].scope" -o tsv 2>/dev/null || true)
V_ACS=$([[ -n "$ACS" ]] && echo true || echo false)
V_ACS_ROLE=$([[ -n "$ACS" && -n "$MI" && "$(az role assignment list --assignee "$MI" --scope "$ACS" --query "length(@)" -o tsv 2>/dev/null)" -gt 0 ]] && echo true || echo false)
# v1.1 (P7 option 1): container names only.
V_CONTAINERS=$([[ -n "$COSMOS" ]] && az cosmosdb sql container list -g "$RG" -a "$COSMOS" -d pmoreview --query "[].name" -o tsv 2>/dev/null || true)

python3 - "$ROOT/.local/verify-ctx.json" <<'PY'
import json, os, sys
e = os.environ
lines = lambda k: [x.strip() for x in e.get(k, "").splitlines() if x.strip()]  # noqa: E731
ctx = {
    "head": e["V_HEAD"], "app_deployed": e.get("V_APP") or None, "worker_deployed": e.get("V_WORKER") or None,
    "app_code_changed": e["V_APP_CHANGED"] == "true", "worker_code_changed": e["V_WORKER_CHANGED"] == "true",
    "tenant_id": e["V_TENANT"],
    "worker_settings": {s["name"]: s.get("value") or "" for s in json.loads(e.get("V_WORKER_SETTINGS") or "[]")},
    "swa_settings": {k: v for k, v in (json.loads(e.get("V_SWA_SETTINGS") or "{}") or {}).items() if v is not None},
    "graph_roles": lines("V_GRAPH"), "rbac_admin_scopes": lines("V_RBAC"),
    "acs_exists": e["V_ACS"] == "true", "acs_role": e["V_ACS_ROLE"] == "true",
    "containers": lines("V_CONTAINERS"),
}
json.dump(ctx, open(sys.argv[1], "w"), indent=2)
PY

echo "== Feature checklist ($(date -u +%FT%TZ), checkout $V_HEAD) =="
PYTHON=$("$ROOT/tools/cosmos_python.sh")
COSMOS_CONNECTION_STRING=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings \
  --query "connectionStrings[0].connectionString" -o tsv 2>/dev/null) "$PYTHON" "$ROOT/tools/verify_features.py" "$ROOT/.local/verify-ctx.json"
