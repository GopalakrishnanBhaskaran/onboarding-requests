#!/usr/bin/env bash
# Step 1 discovery: READ-ONLY. Changes nothing in Azure, Entra or Fabric.
# Usage: bash fabric-adls-poc/tools/discover.sh [--sub <id>] [--storage <name>] [--prefix pvdbx-] [--device-ip <ip>] [--workspace-id <id>]
# Raw JSON -> .local/fabric-poc/ (not in git); masked copies -> fabric-adls-poc/evidence/01-*.
source "$(dirname "$0")/lib.sh"
need az; need python3; need curl
az_preflight

SUB="" ; STG="" ; PREFIX="pvdbx-" ; DEVICE_IP="" ; WSID=""
while [ $# -gt 0 ]; do
  case "$1" in
    --sub) SUB="$2"; shift 2;;
    --storage) STG="$2"; shift 2;;
    --prefix) PREFIX="$2"; shift 2;;
    --device-ip) DEVICE_IP="$2"; shift 2;;
    --workspace-id) WSID="$2"; shift 2;;   # only this workspace gets the per-workspace detail calls (large tenants)   # your device's public IP (in Cloud Shell, the shell's own IP is NOT your device)
    *) die "unknown argument $1";;
  esac
done

az account show >/dev/null 2>&1 || die "not signed in: run 'az login' yourself first (Claude never signs in for you)"
TEN0="$(az account show --query tenantId -o tsv)"
SUBS="$(az account list --query "[?tenantId=='$TEN0' && state=='Enabled'].id" -o tsv)"   # every subscription of the signed-in tenant
if [ -z "$SUB" ] && [ -n "$STG" ]; then   # the storage account decides the subscription
  SUB="$(stg_sub "$STG" || true)"
  [ -n "$SUB" ] && log "storage $STG is in subscription $(echo "$SUB" | mask)" || die "storage account $STG not found in any subscription of this tenant ($(echo "$TEN0" | mask)). Signed in to the right directory?"
fi
# across <az args...>: run a read-only list in every subscription of the tenant, merge into one JSON array, add "sub"
across() {
  local s
  for s in $SUBS; do az "$@" --subscription "$s" -o json 2>/dev/null | python3 -c 'import json,sys
s=sys.argv[1]
try: d=json.load(sys.stdin)
except Exception: d=[]
for x in d or []: print(json.dumps(dict(x,sub=s)))' "$s" || true; done | python3 -c 'import json,sys;print(json.dumps([json.loads(l) for l in sys.stdin if l.strip()],indent=2))'
}
if [ -z "$SUB" ]; then  # find the subscription that holds the $PREFIX VNets
  for s in $(az account list --query "[?state=='Enabled'].id" -o tsv); do
    n="$(az network vnet list --subscription "$s" --query "length([?starts_with(name,'$PREFIX')])" -o tsv 2>/dev/null || echo 0)"
    [ "${n:-0}" != 0 ] && { SUB="$s"; log "found $n $PREFIX VNet(s) in subscription $(echo "$s" | mask)"; break; }
  done
fi
[ -n "$SUB" ] && az account set --subscription "$SUB"
if [ -z "$DEVICE_IP" ]; then
  if [ -n "${ACC_CLOUD:-}${AZUREPS_HOST_ENVIRONMENT:-}" ]; then DEVICE_IP="unknown"; log "Cloud Shell: pass --device-ip <your IP from https://api.ipify.org> to check your firewall rule"
  else DEVICE_IP="$(device_ip || echo unknown)"; fi
fi
SUB="$(az account show --query id -o tsv)"
TENANT="$(az account show --query tenantId -o tsv)"

log "1/12 account + tenant"
az account show -o json | save 01-account.json
az account list --query "[].{name:name,id:id,tenantId:tenantId,state:state,isDefault:isDefault}" -o json | save 01-subscriptions.json

log "2/12 resource providers"
for p in Microsoft.Fabric Microsoft.PowerPlatform Microsoft.Network Microsoft.Storage; do
  printf '%s %s\n' "$p" "$(az provider show -n "$p" --query registrationState -o tsv 2>/dev/null || echo unknown)"
done | save 01-providers.txt

log "3/12 HNS storage accounts"
az storage account list --query "[?isHnsEnabled].{name:name,rg:resourceGroup,location:location,sku:sku.name,kind:kind,publicNetworkAccess:publicNetworkAccess,defaultAction:networkRuleSet.defaultAction,bypass:networkRuleSet.bypass,ipRules:length(networkRuleSet.ipRules),vnetRules:length(networkRuleSet.virtualNetworkRules),resourceRules:length(networkRuleSet.resourceAccessRules),pes:length(privateEndpointConnections),sharedKey:allowSharedKeyAccess}" -o json | save 01-storage-hns.json

if [ -z "$STG" ]; then
  # pick the HNS account that has private endpoints (the Purview / pvdbx- one), else the only one
  STG="$(python3 - "$RAW_DIR/01-storage-hns.json" <<'PY'
import json,sys
rows=json.load(open(sys.argv[1]))
pe=[r for r in rows if r.get("pes")]
pick=(pe or rows)
print(pick[0]["name"] if len(pick)==1 else "")
PY
)"
  [ -n "$STG" ] || { log "several or no HNS accounts: re-run with --storage <name>"; echo "== PASTE THIS BACK =="; mask < "$RAW_DIR/01-storage-hns.json"; exit 3; }
fi
RG="$(az storage account list --query "[?name=='$STG'].resourceGroup | [0]" -o tsv)"
STG_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STG"
log "    target storage: $STG (rg $RG)"

log "4/12 storage network settings (full)"
az storage account show -n "$STG" -g "$RG" --query "{name:name,location:location,isHnsEnabled:isHnsEnabled,publicNetworkAccess:publicNetworkAccess,networkRuleSet:networkRuleSet,allowSharedKeyAccess:allowSharedKeyAccess,allowBlobPublicAccess:allowBlobPublicAccess,minimumTlsVersion:minimumTlsVersion,defaultToOAuthAuthentication:defaultToOAuthAuthentication,primaryEndpoints:primaryEndpoints}" -o json | save 01-storage-network.json
az network private-endpoint-connection list --id "$STG_ID" -o json 2>/dev/null | save 01-storage-pe-connections.json || true

log "5/12 containers (control plane, not blocked by the firewall)"
az storage container-rm list --storage-account "$STG" -g "$RG" --query "[].{name:name,publicAccess:publicAccess,lastModified:lastModifiedTime}" -o json | save 01-containers.json

log "6/12 role assignments on the storage account (incl. inherited)"
az role assignment list --scope "$STG_ID" --include-inherited --query "[].{role:roleDefinitionName,principalType:principalType,principalName:principalName,principalId:principalId,scope:scope}" -o json | save 01-storage-rbac.json

log "7/12 $PREFIX virtual networks + subnets"
across network vnet list --query "[?starts_with(name,'$PREFIX')].{name:name,rg:resourceGroup,location:location,addressSpace:addressSpace.addressPrefixes,dns:dhcpOptions.dnsServers,peerings:length(virtualNetworkPeerings),subnets:subnets[].{name:name,prefix:addressPrefix,prefixes:addressPrefixes,delegations:delegations[].serviceName,peNetworkPolicies:privateEndpointNetworkPolicies,nsg:networkSecurityGroup.id,serviceEndpoints:serviceEndpoints[].service,ipConfigs:length(ipConfigurations || \`[]\`)}}" | save 01-vnets.json

log "8/12 private endpoints in every subscription: those on $STG or in a $PREFIX subnet"
across network private-endpoint list --query "[?contains(to_string(privateLinkServiceConnections[0].privateLinkServiceId || manualPrivateLinkServiceConnections[0].privateLinkServiceId), '$STG') || contains(to_string(subnet.id), '/virtualNetworks/$PREFIX')].{name:name,rg:resourceGroup,location:location,subnet:subnet.id,target:privateLinkServiceConnections[0].privateLinkServiceId,groupIds:privateLinkServiceConnections[0].groupIds,status:privateLinkServiceConnections[0].privateLinkServiceConnectionState.status,manual:manualPrivateLinkServiceConnections[0].privateLinkServiceId,ips:customDnsConfigs[].ipAddresses[0],fqdns:customDnsConfigs[].fqdn}" | save 01-private-endpoints.json

log "9/12 private DNS zones + VNet links"
across network private-dns zone list --query "[?starts_with(name,'privatelink.blob.') || starts_with(name,'privatelink.dfs.')].{name:name,rg:resourceGroup,records:numberOfRecordSets,links:numberOfVirtualNetworkLinks}" | save 01-dns-zones.json
python3 - "$RAW_DIR/01-dns-zones.json" <<'PY' > "$RAW_DIR/.zones.tsv"
import json,sys
for z in json.load(open(sys.argv[1])):
    if z["name"].startswith(("privatelink.blob.","privatelink.dfs.")):
        print(z["sub"], z["rg"], z["name"])
PY
{
  while read -r zsub zrg zname; do
    echo "## $zname ($zrg, subscription $(echo "$zsub" | mask))"
    az network private-dns link vnet list --subscription "$zsub" -g "$zrg" -z "$zname" --query "[].{link:name,vnet:virtualNetwork.id,registration:registrationEnabled,state:virtualNetworkLinkState}" -o json || true
    az network private-dns record-set a list --subscription "$zsub" -g "$zrg" -z "$zname" --query "[].{name:name,ips:aRecords[].ipv4Address}" -o json || true
  done < "$RAW_DIR/.zones.tsv"
} | save 01-dns-links-records.txt

log "10/12 Fabric capacities (ARM)"
across resource list --resource-type Microsoft.Fabric/capacities --query "[].{name:name,rg:resourceGroup,location:location,sku:sku.name,id:id}" | save 01-fabric-capacities-arm.json
for cid in $(python3 -c 'import json,sys;[print(c["id"]) for c in json.load(open(sys.argv[1]))]' "$RAW_DIR/01-fabric-capacities-arm.json"); do
  az resource show --ids "$cid" --query "{name:name,sku:sku,state:properties.state,provisioningState:properties.provisioningState,admins:properties.administration.members}" -o json
done | save 01-fabric-capacities-state.json

log "11/12 Fabric REST (your Fabric role decides what is visible)"
{ fab /capacities || echo '{"error":"capacities not readable"}'; } | save 01-fabric-capacities-api.json
{ fab /workspaces || echo '{"error":"workspaces not readable"}'; } | save 01-fabric-workspaces.json
{ fab /gateways || echo '{"error":"gateways not readable"}'; } | save 01-fabric-gateways.json
# tenant settings need Fabric administrator; keep only the ones this POC depends on
{ fab /admin/tenantsettings 2>/dev/null | python3 2>/dev/null -c '
import json,sys
keep=("ServicePrincipalAccess","AllowServicePrincipalsUseReadAdminAPIs","FabricGAWorkloads","UsersCanCreateFabricItems","WorkspaceIdentity","ManagedPrivateEndpoint","AzurePrivateLink","BlockPublicInternetAccess","Gateway","VNet","Trusted","Identity","CreateWorkspace","Trial")
d=json.load(sys.stdin)
rows=[{"name":s.get("settingName"),"title":s.get("title"),"enabled":s.get("enabled"),"groups":len(s.get("enabledSecurityGroups") or [])} for s in d.get("tenantSettings",[]) if any(k.lower() in (s.get("settingName","")+s.get("title","")).lower() for k in keep)]
print(json.dumps(rows,indent=2))' || echo '{"error":"tenant settings need the Fabric administrator role"}'; } | save 01-fabric-tenant-settings.json
python3 - "$RAW_DIR/01-fabric-workspaces.json" "$WSID" <<'PY' > "$RAW_DIR/.ws.txt" || true
import json,sys
try:
    for w in json.load(open(sys.argv[1])).get("value",[]):
        if w.get("type")=="Workspace" and (not sys.argv[2] or w["id"].lower()==sys.argv[2].lower()):
            print(w["id"], w.get("capacityId",""), w["displayName"].replace(" ","_"))
except Exception:
    pass
PY
NWS="$(wc -l < "$RAW_DIR/.ws.txt")"
if [ -z "$WSID" ] && [ "$NWS" -gt 15 ]; then head -15 "$RAW_DIR/.ws.txt" > "$RAW_DIR/.ws15" && mv "$RAW_DIR/.ws15" "$RAW_DIR/.ws.txt"; log "    $NWS workspaces visible; details for the first 15 only (pass --workspace-id <id> for yours)"; fi
log "    workspace details: $(wc -l < "$RAW_DIR/.ws.txt") workspace(s)"
{
  while read -r wid cap wname; do
    echo "## $wname capacity=${cap:-none}"
    fab "/workspaces/$wid" || true
    fab "/workspaces/$wid/managedPrivateEndpoints" 2>/dev/null || echo '{"managedPrivateEndpoints":"not readable"}'
  done < "$RAW_DIR/.ws.txt"
} | save 01-fabric-workspace-details.txt

log "12/12 device IP + DNS from this machine"
{
  echo "deviceIp=$DEVICE_IP"
  echo "shellIp=$(device_ip || echo unknown)"
  echo "ipRulesOnStorage=$(az storage account show -n "$STG" -g "$RG" --query "networkRuleSet.ipRules[].ipAddressOrRange" -o tsv | tr '\n' ' ')"
  for ep in blob dfs; do
    python3 -c 'import socket,sys
h=sys.argv[1]
try:
    cn,al,ips=socket.gethostbyname_ex(h); print(f"dns {h} -> {ips} via {al[-3:]}")
except Exception as e: print(f"dns {h} -> error {e}")' "$STG.$ep.core.windows.net"
  done
} > "$RAW_DIR/01-device.txt"
# The device IP is compared in raw form only, then masked in evidence.
python3 - "$RAW_DIR/01-device.txt" <<'PY' >> "$RAW_DIR/01-device.txt"
import sys
t=open(sys.argv[1]).read()
ip=[l.split("=",1)[1] for l in t.splitlines() if l.startswith("deviceIp=")][0].strip()
rules=[l.split("=",1)[1] for l in t.splitlines() if l.startswith("ipRulesOnStorage=")][0].split()
print("deviceIpAlreadyAllowed=" + ("yes" if ip in rules else "no"))
PY
mask < "$RAW_DIR/01-device.txt" > "$EVIDENCE_DIR/01-device.txt"

python3 "$POC_DIR/tools/state.py" set env.tenant "$TENANT" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.subscription "$SUB" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.resourceGroup "$RG" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.storageAccount "$STG" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.resourceGroup "$RG" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.region "$(az storage account show -n "$STG" -g "$RG" --query location -o tsv)" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.subscription "$SUB" >/dev/null
python3 "$POC_DIR/tools/state.py" set env.tenant "$TENANT" >/dev/null
python3 "$POC_DIR/tools/summarize_discovery.py" "$RAW_DIR" "$STG" "$PREFIX" | mask > "$EVIDENCE_DIR/01-summary.txt"
log "done. Masked evidence: fabric-adls-poc/evidence/01-*  Raw: $RAW_DIR"
echo "=================== PASTE EVERYTHING BELOW THIS LINE BACK TO CLAUDE ==================="
cat "$EVIDENCE_DIR/01-summary.txt"
echo "=================== END ==================="
