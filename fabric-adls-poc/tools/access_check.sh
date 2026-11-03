#!/usr/bin/env bash
# Read-only: can YOUR account make each POC change on the target storage account, and in Fabric?
# Usage: bash access_check.sh [--workspace-id <id>]
# Uses ARM "permissions" (your effective actions/dataActions at the storage scope, all roles and groups included)
# and the Fabric API. Changes nothing. Evidence copy is masked.
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
WSID=""; while [ $# -gt 0 ]; do case "$1" in --workspace-id) WSID="$2"; shift 2;; *) die "unknown argument $1";; esac; done
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"; STG="$(env_get storageAccount)"
case "$SUB" in *'*'*|*UNVERIFIED*|"") SUB="$(stg_sub "$STG" || az account show --query id -o tsv)";; esac
case "$RG" in *UNVERIFIED*|"") die "run discover.sh first";; esac
az account set --subscription "$SUB"
STG_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STG"
OUT="$RAW_DIR/access_check.txt"
{
echo "== Your effective permissions on $STG (all roles, inherited and through groups)"
az rest --method get --url "https://management.azure.com$STG_ID/providers/Microsoft.Authorization/permissions?api-version=2022-04-01" -o json | python3 -c '
import json,sys,fnmatch
perms=json.load(sys.stdin).get("value",[])
def has(action,data=False):
    a=action.lower(); ok=False
    for p in perms:
        inc=p.get("dataActions" if data else "actions",[]) or []; exc=p.get("notDataActions" if data else "notActions",[]) or []
        if any(fnmatch.fnmatch(a,x.lower()) for x in inc) and not any(fnmatch.fnmatch(a,x.lower()) for x in exc): ok=True
    return ok
need=[
 ("assign roles (workspace identity -> data role)", "Microsoft.Authorization/roleAssignments/write", False, "Agent B RBAC"),
 ("add the resource instance rule (account write)", "Microsoft.Storage/storageAccounts/write", False, "Agent B rule"),
 ("approve a private endpoint connection", "Microsoft.Storage/storageAccounts/privateEndpointConnectionsApproval/action", False, "Agent C approval"),
 ("create the POC container", "Microsoft.Storage/storageAccounts/blobServices/containers/write", False, "Agent B container"),
 ("read blobs (data plane)", "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/read", True, "tests"),
 ("write blobs (data plane)", "Microsoft.Storage/storageAccounts/blobServices/containers/blobs/write", True, "tests"),
]
for label,action,data,use in need:
    mark="YES" if has(action,data) else "NO "
    print(f"{mark}     {label:<48} ({use})")
'
echo "== Fabric"
W="$(fab /workspaces 2>/dev/null || echo '{"value":[]}')"
echo "$W" > "$RAW_DIR/.ac_ws.json"
# who owns the Fabric managed private endpoints already Approved on this storage account (name = <workspaceId>.<mpe name>)
for pe in $(az network private-endpoint-connection list --id "$STG_ID" --query "[?properties.privateLinkServiceConnectionState.status=='Approved'].properties.privateEndpoint.id" -o tsv | sed 's#.*/##' | grep '\.mpe-' || true); do
  wid="${pe%%.*}"
  name="$(python3 -c 'import json,sys;print(next((w["displayName"] for w in json.load(open(sys.argv[1]))["value"] if w["id"].lower()==sys.argv[2].lower()),"a workspace you cannot see"))' "$RAW_DIR/.ac_ws.json" "$wid")"
  echo "        existing Fabric MPE ${pe#*.} belongs to: $name"
done
if [ -n "$WSID" ]; then
  r="$(fab "/workspaces/$WSID/roleAssignments" 2>/dev/null | python3 -c "import json,sys;me=sys.argv[1];d=json.load(sys.stdin);print(next((x['role'] for x in d['value'] if x['principal']['id']==me),'not direct (maybe through a group)'))" "$(az ad signed-in-user show --query id -o tsv)" 2>/dev/null || echo 'not readable (needs Member/Admin)')"
  echo "        your role in the POC workspace: $r"
fi
# existing VNet data gateways: can you manage them / create connections on them?
fab /gateways 2>/dev/null | python3 -c 'import json,sys
for g in json.load(sys.stdin).get("value",[]):
    if g.get("type")=="VirtualNetwork": print(g["id"], g.get("displayName",""))' | while read -r gid gname; do
  if fab "/gateways/$gid/roleAssignments" >/dev/null 2>&1; then echo "YES     gateway $gname: you can read its role assignments (you are its admin)";
  else echo "NO      gateway $gname: not its admin (to add a connection you need Connection Creator or Admin on it)"; fi
done
} 2>&1 | tee "$OUT"
save "ACCESS-check.txt" < "$OUT" >/dev/null
echo "Paste this output back."
