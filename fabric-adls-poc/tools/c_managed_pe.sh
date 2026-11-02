#!/usr/bin/env bash
# Agent C — Fabric managed private endpoint(s) to the storage account, approved on the storage side.
# Default: PLAN. --apply needs wave 2 approval. Usage: bash c_managed_pe.sh [--subresources dfs,blob] [--apply]
source "$(dirname "$0")/lib.sh"
need az; need python3
ST="$POC_DIR/tools/state.py"
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"][sys.argv[2]])' "$POC_DIR/state/state.json" "$1"; }
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"; STG="$(env_get storageAccount)"; WS_ID="$(env_get fabricWorkspaceId)"
SUBRES="dfs" ; APPLY=0
while [ $# -gt 0 ]; do case "$1" in --subresources) SUBRES="$2"; shift 2;; --apply) APPLY=1; shift;; *) die "unknown $1";; esac; done
case "$WS_ID" in *UNVERIFIED*|"") die "run Agent B first (workspace id unknown)";; esac
az account set --subscription "$SUB"
STG_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STG"
[ "$APPLY" = 1 ] && { require_approval 2; python3 "$ST" lock fabric-workspace C; python3 "$ST" lock storage-account C; python3 "$ST" agent C running apply; }
trap '[ "$APPLY" = 1 ] && { python3 "$ST" unlock fabric-workspace C; python3 "$ST" unlock storage-account C; } || true' EXIT
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }
reg="$(az provider show -n Microsoft.Network --query registrationState -o tsv)"
[ "$reg" = Registered ] && echo "OK      Microsoft.Network registered (FB10)" || { echo "CHANGE  register Microsoft.Network"; [ "$APPLY" = 1 ] && az provider register -n Microsoft.Network --wait; }
N=0
for g in ${SUBRES//,/ }; do
  name="mpe-$STG-$g"
  st="$(fab "/workspaces/$WS_ID/managedPrivateEndpoints" | j "[(m.get('provisioningState'),(m.get('connectionState') or {}).get('status')) for m in d.get('value',[]) if m['name']=='$name']")"
  if [ "$st" = "[]" ]; then
    echo "CHANGE  create managed PE $name ($g)"; N=$((N+1))
    if [ "$APPLY" = 1 ]; then
      fab "/workspaces/$WS_ID/managedPrivateEndpoints" POST --body "{\"name\":\"$name\",\"targetPrivateLinkResourceId\":\"$STG_ID\",\"targetSubresourceType\":\"$g\",\"requestMessage\":\"Fabric POC $g access (fabric-adls-poc)\"}" -o none
    fi
  else echo "OK      managed PE $name exists $st"; fi
  if [ "$APPLY" = 1 ]; then
    # wait for the Pending connection on the storage side, then approve it (Fabric-managed PE ids live in Microsoft's subscription)
    for i in $(seq 1 40); do
      cid="$(az network private-endpoint-connection list --id "$STG_ID" --query "[?properties.privateLinkServiceConnectionState.status=='Pending' && contains(properties.privateLinkServiceConnectionState.description,'fabric-adls-poc') && contains(properties.groupIds[0],'$g')].id | [0]" -o tsv 2>/dev/null)"
      [ -z "$cid" ] && cid="$(az network private-endpoint-connection list --id "$STG_ID" --query "[?properties.privateLinkServiceConnectionState.status=='Pending' && contains(properties.privateLinkServiceConnectionState.description,'fabric-adls-poc')].id | [0]" -o tsv 2>/dev/null)"
      [ -n "$cid" ] && break
      app="$(fab "/workspaces/$WS_ID/managedPrivateEndpoints" | j "[(m.get('connectionState') or {}).get('status') for m in d.get('value',[]) if m['name']=='$name']")"
      [ "$app" = "['Approved']" ] && break
      sleep 15
    done
    if [ -n "${cid:-}" ]; then
      az network private-endpoint-connection approve --id "$cid" --description "Approved for fabric-adls-poc ($g) by owner" -o none
      echo "        approved on storage side"
    fi
    for i in $(seq 1 40); do
      app="$(fab "/workspaces/$WS_ID/managedPrivateEndpoints" | j "[(m.get('provisioningState'),(m.get('connectionState') or {}).get('status')) for m in d.get('value',[]) if m['name']=='$name']")"
      echo "$app" | grep -q "Approved" && break; sleep 15
    done
    echo "        Fabric side: $app"
    echo "$app" | grep -q Approved || die "managed PE $name not Approved"
  fi
done
{
  echo "mode=$([ "$APPLY" = 1 ] && echo apply || echo plan) changes=$N"
  fab "/workspaces/$WS_ID/managedPrivateEndpoints"
  az network private-endpoint-connection list --id "$STG_ID" --query "[].{name:name,status:properties.privateLinkServiceConnectionState.status,description:properties.privateLinkServiceConnectionState.description,group:properties.groupIds}" -o json
} | save "C-$([ "$APPLY" = 1 ] && echo apply || echo plan).txt"
[ "$APPLY" = 1 ] && python3 "$ST" agent C done "managed PE approved" >/dev/null
echo "== $N change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed')"
