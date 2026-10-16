#!/usr/bin/env bash
# Read-only diagnosis of the Entra hand-off: worker state, settings, permissions and the latest hand-off records.
#   bash tools/handoff_diag.sh <subscriptionId>
# Prints no secrets (the Cosmos key is used in memory only, to read the 'handoffs' container).
set -uo pipefail
SUB="${1:?subscriptionId}"
RG=rg-pmo-review-pilot
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
az account set --subscription "$SUB"

W=$(az functionapp list -g "$RG" --query "[?starts_with(name,'func-pmo-worker-')].name | [0]" -o tsv)
echo "== Worker: ${W:-NOT FOUND} =="
[[ -n "$W" ]] || exit 1
az functionapp show -g "$RG" -n "$W" --query "{state:state, runtime:properties.functionAppConfig.runtime}" -o json
echo "-- functions deployed (expect handoff-sync):"
az functionapp function list -g "$RG" -n "$W" --query "[].name" -o tsv || echo "  (could not list functions)"
echo "-- settings:"
az functionapp config appsettings list -g "$RG" -n "$W" \
  --query "[?contains(['ALLOW_PAID_SERVICES','COSMOS_ENDPOINT','COSMOS_DATABASE','HANDOFF_SYNC_SCHEDULE','ENTRA_AUTO_ASSIGN','ENTRA_ALLOWED_CATALOG_ID','ENTRA_AUTO_ASSIGN_POLICY','AzureWebJobsStorage__accountName'], name)].{name:name, value:value}" -o table

MI=$(az functionapp identity show -g "$RG" -n "$W" --query principalId -o tsv)
echo "-- Graph application permissions of the worker identity:"
az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$MI/appRoleAssignments" --query "value[].appRoleId" -o tsv \
  | while read -r id; do az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?id=='$id'].value | [0]" -o tsv; done
COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
echo "-- Cosmos data-plane roles of the worker identity:"
az cosmosdb sql role assignment list -g "$RG" -a "$COSMOS" --query "[?principalId=='$MI'].{role:roleDefinitionId, scope:scope}" -o table

SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
echo "== App settings (hand-off) =="
az staticwebapp appsettings list -n "$SWA" -g "$RG" \
  --query "{ENTRA_HANDOFF_ENABLED:properties.ENTRA_HANDOFF_ENABLED, ENTRA_AUTO_ASSIGN:properties.ENTRA_AUTO_ASSIGN, REQUIRE_SEPARATE_REVIEWER:properties.REQUIRE_SEPARATE_REVIEWER}" -o table

echo "== Latest hand-off records =="
python3 -c "import azure.cosmos" 2>/dev/null || pip install --user -q -r "$ROOT/tools/requirements.txt"
COSMOS_CONNECTION_STRING=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings \
  --query "connectionStrings[0].connectionString" -o tsv 2>/dev/null) python3 - <<'PY'
import os
from azure.cosmos import CosmosClient
c = CosmosClient.from_connection_string(os.environ["COSMOS_CONNECTION_STRING"]).get_database_client("pmoreview").get_container_client("handoffs")
docs = list(c.query_items("SELECT * FROM c ORDER BY c.createdAt DESC OFFSET 0 LIMIT 5", enable_cross_partition_query=True))
if not docs:
    print("  none: no hand-off record exists (the Accepted decision did not create one)")
for d in docs:
    print(f"- {d['id']}  status={d.get('status')}  created={d.get('createdAt')}  lastChecked={d.get('lastCheckedAt')}  lastError={d.get('lastSyncError')}")
    print(f"    joiner={d.get('joinerUpn')}  package={d.get('accessPackageId')}  mapping={d.get('mappingId')} v{d.get('mappingVersion')}  period={d.get('requestedStart')} -> {d.get('requestedEnd')}")
    print(f"    entraRequest={d.get('entraRequestId')} ({d.get('entraRequestType')}, {d.get('entraRequestState')})  assignment={d.get('entraAssignmentState')}")
    if d.get("entraAssignmentState"):
        print(f"    access in Entra: {d.get('accessStart')} -> {d.get('accessEnd')}  endDateLaterThanReviewed={d.get('endDateExceedsRequest')}  preExisting={d.get('preExistingAssignment')}")
    if d.get("reason"):
        print(f"    reason: {d['reason']}")
    for e in d.get("history", []):
        print(f"    {e.get('at')}  {e.get('status')}: {e.get('note')}")
PY
echo
echo "Reading: lastChecked empty = the worker has not processed it (not running, or Cosmos access failing)."
echo "         lastError set = the worker ran and Entra/Graph refused; the category says why (workflows/08-entra-handoff.md)."
