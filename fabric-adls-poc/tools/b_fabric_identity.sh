#!/usr/bin/env bash
# Agent B — Fabric capacity/workspace/identity + RBAC + resource instance rule.
# Default: PLAN (read-only). --apply needs wave 1 approval. Run after Agent A's device IP rule.
# Usage: bash b_fabric_identity.sh --workspace <name> --container <c> [--capacity <armName>] [--create-capacity <name> --region <loc>] [--apply]
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
ST="$POC_DIR/tools/state.py"
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"][sys.argv[2]])' "$STATE_FILE" "$1"; }
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"; STG="$(env_get storageAccount)"; TENANT="$(env_get tenant)"
WS="" ; CONTAINER="" ; CAP="" ; NEWCAP="" ; REGION="" ; APPLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --workspace) WS="$2"; shift 2;; --container) CONTAINER="$2"; shift 2;;
    --capacity) CAP="$2"; shift 2;; --create-capacity) NEWCAP="$2"; shift 2;; --region) REGION="$2"; shift 2;;
    --apply) APPLY=1; shift;; *) die "unknown argument $1";;
  esac
done
[ -n "$WS" ] && [ -n "$CONTAINER" ] || die "--workspace and --container are required"
az account set --subscription "$SUB"
STG_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STG"
CONT_SCOPE="$STG_ID/blobServices/default/containers/$CONTAINER"
ME_UPN="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
ME_OID="$(az ad signed-in-user show --query id -o tsv)"
[ "$APPLY" = 1 ] && { require_approval 1; python3 "$ST" lock fabric-workspace B; python3 "$ST" lock fabric-capacity B; python3 "$ST" agent B running apply; }
trap '[ "$APPLY" = 1 ] && { python3 "$ST" unlock fabric-workspace B; python3 "$ST" unlock fabric-capacity B; python3 "$ST" unlock storage-network-rules B 2>/dev/null; } || true' EXIT
PLAN=()
change() { echo "CHANGE  $1"; PLAN+=("$1"); }
ok() { echo "OK      $1"; }
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }

echo "== Agent B plan: workspace '$WS', storage $STG/$CONTAINER"
# 1. capacity: an F SKU is required for trusted workspace access (FB1); Trial does not work.
if [ -n "$NEWCAP" ]; then
  CAP="$NEWCAP"
  r="$(az provider show -n Microsoft.Fabric --query registrationState -o tsv 2>/dev/null || echo NotRegistered)"
  if [ "$r" = Registered ]; then ok "Microsoft.Fabric provider registered"; else change "register Microsoft.Fabric resource provider (free)"; [ "$APPLY" = 1 ] && az provider register -n Microsoft.Fabric --wait; fi
  if az resource show -g "$RG" -n "$CAP" --resource-type Microsoft.Fabric/capacities >/dev/null 2>&1; then ok "capacity $CAP exists"; else
    change "create capacity $CAP F2 in $RG/$REGION, admin = you (billing starts; pause when idle, FB18/FB19)"
    if [ "$APPLY" = 1 ]; then
      az rest --method put --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Fabric/capacities/$CAP?api-version=2023-11-01" \
        --body "{\"location\":\"$REGION\",\"sku\":{\"name\":\"F2\",\"tier\":\"Fabric\"},\"properties\":{\"administration\":{\"members\":[\"$ME_UPN\"]}},\"tags\":{\"purpose\":\"fabric-adls-poc\"}}" -o none
      for i in $(seq 1 40); do s="$(az resource show -g "$RG" -n "$CAP" --resource-type Microsoft.Fabric/capacities --query properties.state -o tsv)"; [ "$s" = Active ] && break; sleep 15; done
      [ "$s" = Active ] || die "capacity state $s"
    fi
  fi
fi
[ -n "$CAP" ] || die "--capacity <existing F capacity> or --create-capacity <name> --region <loc> is required"
SKU="$(az resource list --resource-type Microsoft.Fabric/capacities --query "[?name=='$CAP'].sku.name | [0]" -o tsv)"
case "$SKU" in F*) ok "capacity $CAP is an F SKU ($SKU)";; "") [ "$APPLY" = 1 ] && die "capacity $CAP not found";; *) die "capacity $CAP is $SKU, not an F SKU";; esac
CAP_ID="$(fab /capacities 2>/dev/null | j "[c['id'] for c in d['value'] if c['displayName']=='$CAP'][0] if any(c['displayName']=='$CAP' for c in d['value']) else ''" || true)"

# 2. workspace on that capacity
WS_ID="$(fab /workspaces | j "[w['id'] for w in d['value'] if w['displayName']=='$WS'][0] if any(w['displayName']=='$WS' for w in d['value']) else ''")"
if [ -n "$WS_ID" ]; then ok "workspace $WS exists"; else
  change "create workspace $WS on $CAP"
  [ "$APPLY" = 1 ] && WS_ID="$(fab /workspaces POST --body "{\"displayName\":\"$WS\",\"capacityId\":\"$CAP_ID\"}" | j "d['id']")"
fi
if [ -n "$WS_ID" ]; then
  WCAP="$(fab "/workspaces/$WS_ID" | j "d.get('capacityId','')")"
  if [ "$WCAP" = "$CAP_ID" ]; then ok "workspace on $CAP"; else
    change "assign workspace to $CAP"
    [ "$APPLY" = 1 ] && fab "/workspaces/$WS_ID/assignToCapacity" POST --body "{\"capacityId\":\"$CAP_ID\"}" -o none
  fi
  python3 "$ST" set env.fabricWorkspace "$WS" >/dev/null; python3 "$ST" set env.fabricWorkspaceId "$WS_ID" >/dev/null; python3 "$ST" set env.fabricCapacity "$CAP ($SKU)" >/dev/null
fi

# 3. workspace identity (+ Contributor in the workspace, FB3)
SP_ID=""
[ -n "$WS_ID" ] && SP_ID="$(fab "/workspaces/$WS_ID" | j "(d.get('workspaceIdentity') or {}).get('servicePrincipalId','')")"
if [ -n "$SP_ID" ]; then ok "workspace identity exists"; else
  change "create workspace identity"
  if [ "$APPLY" = 1 ]; then
    fab "/workspaces/$WS_ID/provisionIdentity" POST -o none || true
    for i in $(seq 1 30); do SP_ID="$(fab "/workspaces/$WS_ID" | j "(d.get('workspaceIdentity') or {}).get('servicePrincipalId','')")"; [ -n "$SP_ID" ] && break; sleep 10; done
    [ -n "$SP_ID" ] || die "workspace identity not provisioned (FB: if state Failed, wait 1 h, delete, wait 5 min, retry)"
  fi
fi
if [ -n "$SP_ID" ]; then
  if fab "/workspaces/$WS_ID/roleAssignments" | j "any(r['principal']['id']=='$SP_ID' and r['role'] in ('Admin','Member','Contributor') for r in d['value'])" | grep -q True; then ok "identity has a workspace role"; else
    change "workspace identity = Contributor in $WS"
    [ "$APPLY" = 1 ] && fab "/workspaces/$WS_ID/roleAssignments" POST --body "{\"principal\":{\"id\":\"$SP_ID\",\"type\":\"ServicePrincipal\"},\"role\":\"Contributor\"}" -o none
  fi
fi

# 4. RBAC, least privilege (FB3): Delegator at account + Data Contributor on the container
rbac() { # rbac <principalId> <type> <role> <scope> <label>
  if [ -z "$1" ]; then change "$5 (after the identity exists)"; return; fi
  n="$(az role assignment list --scope "$4" --query "[?principalId=='$1' && roleDefinitionName=='$3'] | length(@)" -o tsv 2>/dev/null || echo 0)"
  if [ "$n" != 0 ]; then ok "$5"; else change "$5"
    [ "$APPLY" = 1 ] && az role assignment create --assignee-object-id "$1" --assignee-principal-type "$2" --role "$3" --scope "$4" -o none
  fi
}
rbac "$SP_ID" ServicePrincipal "Storage Blob Delegator" "$STG_ID" "identity: Storage Blob Delegator @ account"
rbac "$SP_ID" ServicePrincipal "Storage Blob Data Contributor" "$CONT_SCOPE" "identity: Storage Blob Data Contributor @ container $CONTAINER"
# the owner needs a data role to upload test files and to browse in the portal with Entra auth
if az role assignment list --scope "$CONT_SCOPE" --include-inherited --query "[?principalId=='$ME_OID' && contains(roleDefinitionName,'Storage Blob Data')] | length(@)" -o tsv | grep -qv '^0$'; then ok "you: a Storage Blob Data role on $CONTAINER"; else
  rbac "$ME_OID" User "Storage Blob Data Contributor" "$CONT_SCOPE" "you: Storage Blob Data Contributor @ container $CONTAINER"
fi

# 5. resource instance rule for this workspace (FB4). CLI = same Storage RP update as the documented PowerShell cmdlet.
RID="/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/Fabric/providers/Microsoft.Fabric/workspaces/$WS_ID"
if [ -n "$WS_ID" ] && az storage account show -n "$STG" -g "$RG" --query "networkRuleSet.resourceAccessRules[].resourceId" -o tsv | grep -qix "$RID"; then ok "resource instance rule for the workspace"; else
  change "resource instance rule Microsoft.Fabric/workspaces/<workspace id> (tenant $(echo "$TENANT" | mask))"
  if [ "$APPLY" = 1 ]; then
    python3 "$ST" lock storage-network-rules B
    az storage account network-rule add -g "$RG" --account-name "$STG" --resource-id "$RID" --tenant-id "$TENANT" -o none
  fi
fi

{
  echo "mode=$([ "$APPLY" = 1 ] && echo apply || echo plan) changes=${#PLAN[@]}"; printf '%s\n' "${PLAN[@]:-none}"
  [ -n "$WS_ID" ] && fab "/workspaces/$WS_ID" || true
  [ -n "$SP_ID" ] && az role assignment list --all --query "[?principalId=='$SP_ID'].{role:roleDefinitionName,scope:scope}" -o json || true
  az storage account show -n "$STG" -g "$RG" --query "networkRuleSet.resourceAccessRules" -o json
} | save "B-$([ "$APPLY" = 1 ] && echo apply || echo plan).txt"
[ "$APPLY" = 1 ] && python3 "$ST" agent B done "identity, RBAC, resource rule in place" >/dev/null
echo "== ${#PLAN[@]} change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed')"
