#!/usr/bin/env bash
# Agent D — VNet data gateway in a dedicated pvdbx- subnet.
# Default: PLAN. --apply needs wave 2 approval.
# Usage: bash d_vnet_gateway.sh --vnet <pvdbx-vnet> --subnet <new-subnet-name> --prefix <cidr /27 or larger> [--apply]
#        bash d_vnet_gateway.sh --existing <gateway display name>   # reuse a VNet data gateway: verify only, no Azure change
# The gateway connection itself is made by YOU in the Fabric UI with your organizational account
# (workspace identity is not supported on gateway connections, FB8; the REST API has no OAuth2 type, FB14).
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
ST="$POC_DIR/tools/state.py"
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"][sys.argv[2]])' "$STATE_FILE" "$1"; }
SUB="$(env_get subscription)"; CAPNAME="$(env_get fabricCapacity | cut -d' ' -f1)"
case "$SUB" in *'*'*|*UNVERIFIED*|"") SUB="$(stg_sub "$(env_get storageAccount)" || az account show --query id -o tsv)";; esac   # template values are masked
VNET="" ; SUBNET="" ; PREFIX="" ; VNET_RG="" ; APPLY=0 ; EXISTING=""
while [ $# -gt 0 ]; do case "$1" in --vnet) VNET="$2"; shift 2;; --vnet-rg) VNET_RG="$2"; shift 2;; --subnet) SUBNET="$2"; shift 2;; --prefix) PREFIX="$2"; shift 2;; --existing) EXISTING="$2"; shift 2;; --apply) APPLY=1; shift;; *) die "unknown $1";; esac; done
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }
if [ -n "$EXISTING" ]; then   # ---- reuse an existing VNet data gateway: read-only ----
  G="$(fab /gateways | python3 -c 'import json,sys;n=sys.argv[1];print(json.dumps(next((g for g in json.load(sys.stdin)["value"] if g.get("displayName")==n),{})))' "$EXISTING")"
  [ "$G" != "{}" ] || die "gateway '$EXISTING' not visible to you (fab /gateways)"
  GW_ID="$(echo "$G" | j "d['id']")"
  echo "== Agent D: reuse existing gateway $EXISTING (no Azure or Fabric change)"
  echo "$G" | python3 -c 'import json,sys;g=json.load(sys.stdin);v=g.get("virtualNetworkAzureResource") or {}
print("OK      type", g.get("type"), "| VNet", v.get("virtualNetworkName"), "/", v.get("subnetName"), "| members", g.get("numberOfMemberGateways"), "| sleep after", g.get("inactivityMinutesBeforeSleep"), "min")'
  GCAP="$(echo "$G" | j "(d.get('capacityId') or '').lower()")"
  GCAPN="$(fab /capacities | j "next((c['displayName']+' '+c['sku'] for c in d['value'] if c['id'].lower()=='$GCAP'),'not visible')")"
  echo "        runs on capacity: $GCAPN (POC workspace capacity: $CAPNAME)"
  ME="$(az ad signed-in-user show --query id -o tsv)"
  R="$(fab "/gateways/$GW_ID/roleAssignments" 2>/dev/null | j "next((r['role'] for r in d['value'] if r['principal']['id']=='$ME'),'through a group')" 2>/dev/null || echo 'not readable')"
  echo "        your role on the gateway: $R"
  echo "== connections on this gateway (you create the ADLS one in the UI, org account, FB8/FB14)"
  fab /connections | python3 -c 'import json,sys;gid=sys.argv[1].lower()
rows=[c for c in json.load(sys.stdin)["value"] if (c.get("gatewayId") or "").lower()==gid]
for c in rows: print("        ", c["id"], c.get("displayName"), (c.get("connectionDetails") or {}).get("type"), (c.get("connectionDetails") or {}).get("path"), (c.get("credentialDetails") or {}).get("credentialType"))
print("         (none yet)" if not rows else "")' "$GW_ID"
  python3 "$ST" set env.gatewayId "$GW_ID" >/dev/null
  { echo "mode=reuse-existing changes=0"; echo "$G"; } | save "D-existing.txt"
  python3 "$ST" agent D done "reused existing VNet data gateway $EXISTING; ADLS connection by owner in the UI" >/dev/null
  STGN="$(env_get storageAccount)"
  echo "Next (you, browser): Fabric > Settings (gear) > Manage connections and gateways > + New > Virtual network > gateway $EXISTING >"
  echo "  connection name poc-adls-gw, type Azure Data Lake Storage Gen2, URL https://$STGN.dfs.core.windows.net/fabric-poc, auth OAuth 2.0 > Edit credentials (you sign in) > Create."
  echo "Then re-run this command: the new connection id is listed above. Claude never types credentials."
  exit 0
fi
[ -n "$VNET" ] && [ -n "$SUBNET" ] && [ -n "$PREFIX" ] || die "--vnet, --subnet and --prefix are required"
case "$SUBNET" in [Gg]ateway[Ss]ubnet|AzureBastionSubnet) die "reserved subnet name (FB12)";; esac
az account set --subscription "$SUB"
[ -n "$VNET_RG" ] || VNET_RG="$(az network vnet list --query "[?name=='$VNET'].resourceGroup | [0]" -o tsv)"
LOC="$(az network vnet show -g "$VNET_RG" -n "$VNET" --query location -o tsv)"
[ "$APPLY" = 1 ] && { require_approval 2; python3 "$ST" lock vnet D; python3 "$ST" agent D running apply; }
trap '[ "$APPLY" = 1 ] && python3 "$ST" unlock vnet D || true' EXIT
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }
N=0; chg() { echo "CHANGE  $1"; N=$((N+1)); }
echo "== Agent D plan: $VNET ($LOC) subnet $SUBNET $PREFIX, capacity $CAPNAME"
# region check (FB12 list, 2026-09-29)
echo "australiaeast australiasoutheast brazilsouth canadacentral canadaeast centralindia centralus eastasia eastus eastus2 francecentral germanywestcentral japaneast koreacentral indonesiacentral israelcentral italynorth malaysiawest mexicocentral newzealandnorth northcentralus northeurope norwayeast polandcentral qatarcentral spaincentral southafricanorth southcentralus southeastasia swedencentral switzerlandnorth taiwannorth taiwannorthwest uaenorth uksouth ukwest westcentralus westeurope westus westus2 westus3" | tr ' ' '\n' | grep -qx "$LOC" \
  && echo "OK      region $LOC supports VNet data gateways (FB12)" || die "region $LOC is not on the VNet data gateway list (FB12)"
reg="$(az provider show -n Microsoft.PowerPlatform --query registrationState -o tsv 2>/dev/null || echo NotRegistered)"
[ "$reg" = Registered ] && echo "OK      Microsoft.PowerPlatform registered" || { chg "register Microsoft.PowerPlatform (subscription owner)"; [ "$APPLY" = 1 ] && az provider register -n Microsoft.PowerPlatform --wait; }
if az network vnet subnet show -g "$VNET_RG" --vnet-name "$VNET" -n "$SUBNET" >/dev/null 2>&1; then
  d="$(az network vnet subnet show -g "$VNET_RG" --vnet-name "$VNET" -n "$SUBNET" --query "delegations[].serviceName" -o tsv)"
  [ "$d" = "Microsoft.PowerPlatform/vnetaccesslinks" ] && echo "OK      subnet $SUBNET delegated" || { chg "delegate $SUBNET to Microsoft.PowerPlatform/vnetaccesslinks"; [ "$APPLY" = 1 ] && az network vnet subnet update -g "$VNET_RG" --vnet-name "$VNET" -n "$SUBNET" --delegations Microsoft.PowerPlatform/vnetaccesslinks -o none; }
else
  chg "create subnet $SUBNET $PREFIX delegated to Microsoft.PowerPlatform/vnetaccesslinks (no NSG change, IPv4 only)"
  if [ "$APPLY" = 1 ]; then az network vnet subnet create -g "$VNET_RG" --vnet-name "$VNET" -n "$SUBNET" --address-prefixes "$PREFIX" --delegations Microsoft.PowerPlatform/vnetaccesslinks -o none; fi
fi
CAP_ID="$(fab /capacities | j "[c['id'] for c in d['value'] if c['displayName']=='$CAPNAME'][0]")"
GW_NAME="vnetgw-fabric-adls-poc"
GW_ID="$(fab /gateways | j "[g['id'] for g in d['value'] if g.get('displayName')=='$GW_NAME'][0] if any(g.get('displayName')=='$GW_NAME' for g in d['value']) else ''")"
if [ -n "$GW_ID" ]; then echo "OK      gateway $GW_NAME exists"; else
  chg "create VNet data gateway $GW_NAME (1 member, auto-sleep after 30 min, runs on $CAPNAME CUs)"
  if [ "$APPLY" = 1 ]; then
    GW_ID="$(fab /gateways POST --body "{\"type\":\"VirtualNetwork\",\"displayName\":\"$GW_NAME\",\"capacityId\":\"$CAP_ID\",\"virtualNetworkAzureResource\":{\"subscriptionId\":\"$SUB\",\"resourceGroupName\":\"$VNET_RG\",\"virtualNetworkName\":\"$VNET\",\"subnetName\":\"$SUBNET\"},\"inactivityMinutesBeforeSleep\":30,\"numberOfMemberGateways\":1}" | j "d['id']")"
  fi
fi
[ -n "$GW_ID" ] && python3 "$ST" set env.gatewayId "$GW_ID" >/dev/null
{
  echo "mode=$([ "$APPLY" = 1 ] && echo apply || echo plan) changes=$N"
  az network vnet subnet show -g "$VNET_RG" --vnet-name "$VNET" -n "$SUBNET" --query "{name:name,prefix:addressPrefix,delegations:delegations[].serviceName}" -o json 2>/dev/null || echo '{"subnet":"not created"}'
  fab /gateways | python3 -c "import json,sys;d=json.load(sys.stdin);print(json.dumps([g for g in d['value'] if g.get('displayName')=='$GW_NAME'],indent=2))"
} | save "D-$([ "$APPLY" = 1 ] && echo apply || echo plan).txt"
[ "$APPLY" = 1 ] && python3 "$ST" agent D done "gateway created; connection pending owner (UI, org account)" >/dev/null
echo "== $N change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed')"
echo "Next (you, in the browser): Fabric > Settings > Manage connections and gateways > New > Virtual network > $GW_NAME > Azure Data Lake Storage Gen2,"
echo "  URL https://<account>.dfs.core.windows.net/<container>, authentication OAuth2 (organizational account) > Sign in. Claude never types credentials."
