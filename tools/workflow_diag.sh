#!/usr/bin/env bash
# Read-only diagnosis of the v2 onboarding flow: worker state, settings, permissions, open requests and issues.
#   bash tools/workflow_diag.sh <subscriptionId>
# Prints no secrets: the Cosmos key is used in memory only; requests are shown as status lines (no justification).
set -uo pipefail
SUB="${1:?subscriptionId}"
RG=rg-pmo-review-pilot
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
az account set --subscription "$SUB"

echo "== Code: this checkout $(git -C "$ROOT" rev-parse --short HEAD) =="
for k in app worker; do
  f="$ROOT/.local/last-$k-deploy"
  if [[ -f "$f" ]]; then echo "  last $k deploy from this Cloud Shell: $(cat "$f")"; else echo "  last $k deploy from this Cloud Shell: unknown"; fi
done

W=$(az functionapp list -g "$RG" --query "[?starts_with(name,'func-pmo-worker-')].name | [0]" -o tsv)
echo "== Worker: ${W:-NOT FOUND} =="
if [[ -n "$W" ]]; then
  az functionapp show -g "$RG" -n "$W" --query "{state:state, runtime:properties.functionAppConfig.runtime, lastModifiedUtc:lastModifiedTimeUtc}" -o json
  echo "-- functions deployed (expect workflow-agent):"
  az functionapp function list -g "$RG" -n "$W" --query "[].name" -o tsv || echo "  (could not list functions)"
  echo "-- settings:"
  az functionapp config appsettings list -g "$RG" -n "$W" \
    --query "[?contains(['ALLOW_PAID_SERVICES','COSMOS_DATABASE','WORKFLOW_SCHEDULE','REVIEW_MODEL_PROVIDER','ROLE_READER','NOTIFY_SENDER','ADO_ORG_URL','ADO_PROJECT','TAP_MAX_LIFETIME_MINUTES','ENTRA_AUTO_ASSIGN','ENTRA_PROVISION_JOINERS','ENTRA_AUTO_CREATE_PACKAGES','AGENT_PROVISIONING','AGENT_ROLE_ASSIGNMENT','ROLE_ASSIGN_ALLOWED_ROLES','ROLE_ASSIGN_SCOPES','APP_ENVIRONMENT','NOTIFY_FROM','ACS_ENDPOINT','NOTIFY_REDIRECT_TO','AGENT_TENANT_ID','AGENT_TAP','AGENT_TAP_LIFETIME_MINUTES','AGENT_PRIVILEGED'], name)].{name:name, value:value}" -o table
  MI=$(az functionapp identity show -g "$RG" -n "$W" --query principalId -o tsv)
  echo "-- Graph application permissions (read: User.Read.All, GroupMember.Read.All, UserAuthenticationMethod.Read.All, AuditLog.Read.All; v2.1 provisioning adds User.Create, Group.Create, GroupMember.ReadWrite.All; agent TAP adds UserAuthMethod-TAP.ReadWrite.All):"
  HELD=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$MI/appRoleAssignments" --query "value[].appRoleId" -o tsv \
    | while read -r id; do az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?id=='$id'].value | [0]" -o tsv; done)
  echo "$HELD"
  # Retired Phase 2b/2c roles (same list as deploy_free.sh GRAPH_RETIRED_ROLES). The v2.1 create roles are approved (17 Oct 2026).
  if echo "$HELD" | grep -qxE "EntitlementManagement.ReadWrite.All|User.ReadWrite.All|LicenseAssignment.ReadWrite.All|UserAuthenticationMethod.ReadWrite.All|Group.ReadWrite.All|EntitlementManagement.Read.All|User.ReadBasic.All"; then
    echo "FLAG: retired Phase 2b/2c write permissions still held. After approval: tools/deploy_free.sh revoke-graph-writes $SUB --approved-by \"<name> <date>\""
  fi
  PROV=$(az functionapp config appsettings list -g "$RG" -n "$W" --query "[?name=='AGENT_PROVISIONING'].value | [0]" -o tsv)
  if echo "$HELD" | grep -qxE "User.Create|Group.Create|GroupMember.ReadWrite.All" && [[ "$(tr '[:upper:]' '[:lower:]' <<<"$PROV")" != "true" ]]; then
    echo "FLAG: v2.1 create permissions held while AGENT_PROVISIONING is off. Remove them (approval): tools/deploy_free.sh provisioning $SUB off --approved-by \"<name> <date>\""
  fi
  TAPON=$(az functionapp config appsettings list -g "$RG" -n "$W" --query "[?name=='AGENT_TAP'].value | [0]" -o tsv)
  if echo "$HELD" | grep -qx "UserAuthMethod-TAP.ReadWrite.All" && [[ "$(tr '[:upper:]' '[:lower:]' <<<"$TAPON")" != "true" ]]; then
    echo "FLAG: the TAP write permission is held while AGENT_TAP is off. Remove it (approval): tools/deploy_free.sh tap $SUB off --approved-by \"<name> <date>\""
  fi
  COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
  echo "-- Cosmos data-plane roles of the worker identity:"
  az cosmosdb sql role assignment list -g "$RG" -a "$COSMOS" --query "[?principalId=='$MI'].{role:roleDefinitionId, scope:scope}" -o table
fi

COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
echo "== Requests (latest 15), issues, notifications =="
PYTHON=$("$ROOT/tools/cosmos_python.sh")
COSMOS_CONNECTION_STRING=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings \
  --query "connectionStrings[0].connectionString" -o tsv 2>/dev/null) "$PYTHON" - <<'PY'
import os
from collections import Counter
from azure.cosmos import CosmosClient
db = CosmosClient.from_connection_string(os.environ["COSMOS_CONNECTION_STRING"]).get_database_client("pmoreview")
def q(container, sql):
    try:
        return list(db.get_container_client(container).query_items(sql, enable_cross_partition_query=True))
    except Exception as e:  # container missing before the v2 apply
        print(f"  {container}: not readable ({type(e).__name__})")
        return []
for p in q("projects", "SELECT c.projectId, c.domainIdSuffix, c.tenantName, c.tenantId, c.environments, c.contacts.security AS security FROM c"):
    print(f"  project {p['projectId']}: tenant {p.get('tenantName') or '-'} ({p.get('tenantId') or 'agent tenant'}), "
          f"environments {', '.join(p.get('environments') or []) or '-'}, domain-specific IDs end with {p.get('domainIdSuffix')}, "
          f"Security contacts {', '.join(p.get('security') or []) or '-'}")
reqs = q("requests", "SELECT TOP 15 c.id, c.projectId, c.domainId, c.resource, c.accessLevel, c.revision, c.flow.status, c.flow.waitingOn, c.flow.events, c.updatedAt FROM c ORDER BY c.updatedAt DESC")
for r in reqs:
    print(f"  {r['id'][:8]} {r.get('projectId')} {r.get('status') or 'legacy-v1'} waiting={r.get('waitingOn')} r{r.get('revision')} {r.get('domainId')} '{r.get('accessLevel')}' on '{r.get('resource')}' updated={r.get('updatedAt')}")
    if r.get("events"):
        last = r["events"][-1]
        print(f"      last step: {last.get('step')} by {last.get('actor')} at {last.get('at')}")
issues = q("issues", "SELECT c.requestId, c.kind, c.status, c.severity, c.step, c.detail, c.occurrences, c.lastSeenAt FROM c")
print("  open issues:", dict(Counter((i["kind"], i["severity"]) for i in issues if i.get("status") == "Open")) or "none")
for i in issues:
    if i.get("status") == "Open" and i.get("kind") == "NeedsDeveloper":
        print(f"  NEEDS DEVELOPER request={str(i.get('requestId'))[:8]} step={i.get('step')} x{i.get('occurrences')} last={i.get('lastSeenAt')}")
        print(f"      {i.get('detail')}")
notes = q("notifications", "SELECT c.status, c.kind, c.lastError, c.deliveredTo FROM c")
print("  notifications by status:", dict(Counter(n["status"] for n in notes)) or "none")
print("  mail errors:", dict(Counter(n.get("lastError") for n in notes if n.get("lastError"))) or "none")
print("  mailed to (test redirect shows one address):", dict(Counter(a for n in notes for a in (n.get("deliveredTo") or []))) or "none")
PY
