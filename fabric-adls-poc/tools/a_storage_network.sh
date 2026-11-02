#!/usr/bin/env bash
# Agent A — storage network. Default: PLAN (read-only). --apply needs wave 1 approval in state.json.
# Order is fixed so the owner is never locked out: device IP rule -> PEs + DNS -> default Deny.
# Usage: bash a_storage_network.sh --vnet <pvdbx-vnet> --pe-subnet <subnet> [--dns-rg <rg>] [--device-ip <ip>] [--apply]
source "$(dirname "$0")/lib.sh"
need az; need python3
ST="$POC_DIR/tools/state.py"
SUB="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"]["subscription"])' "$POC_DIR/state/state.json")"
RG="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"]["resourceGroup"])' "$POC_DIR/state/state.json")"
STG="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"]["storageAccount"])' "$POC_DIR/state/state.json")"
VNET="" ; SUBNET="" ; VNET_RG="" ; DNS_RG="" ; DEVICE_IP="" ; APPLY=0
while [ $# -gt 0 ]; do
  case "$1" in
    --vnet) VNET="$2"; shift 2;; --vnet-rg) VNET_RG="$2"; shift 2;;
    --pe-subnet) SUBNET="$2"; shift 2;; --dns-rg) DNS_RG="$2"; shift 2;;
    --device-ip) DEVICE_IP="$2"; shift 2;; --apply) APPLY=1; shift;;
    *) die "unknown argument $1";;
  esac
done
envq() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"].get(sys.argv[2],""))' "$POC_DIR/state/state.json" "$1"; }
[ -n "$VNET" ] || VNET="$(envq vnet)"; [ -n "$SUBNET" ] || SUBNET="$(envq peSubnet)"
[ -n "$VNET" ] && [ -n "$SUBNET" ] || die "--vnet and --pe-subnet are required (see evidence/01-vnets.json)"
case "$SUB$RG$STG" in *UNVERIFIED*) die "run discover.sh first";; esac
az account set --subscription "$SUB"
[ -n "$VNET_RG" ] || VNET_RG="$(az network vnet list --query "[?name=='$VNET'].resourceGroup | [0]" -o tsv)"
[ -n "$DNS_RG" ] || DNS_RG="$VNET_RG"
[ -n "$DEVICE_IP" ] || DEVICE_IP="$(device_ip)"
STG_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STG"
SUBNET_ID="$(az network vnet subnet show -g "$VNET_RG" --vnet-name "$VNET" -n "$SUBNET" --query id -o tsv)"
LOC="$(az storage account show -n "$STG" -g "$RG" --query location -o tsv)"
VNET_LOC="$(az network vnet show -g "$VNET_RG" -n "$VNET" --query location -o tsv)"
VNET_ID="$(az network vnet show -g "$VNET_RG" -n "$VNET" --query id -o tsv)"
[ "$APPLY" = 1 ] && { require_approval 1; python3 "$ST" lock storage-network-rules A; python3 "$ST" lock vnet A; python3 "$ST" agent A running "apply"; }
trap '[ "$APPLY" = 1 ] && { python3 "$ST" unlock storage-network-rules A; python3 "$ST" unlock vnet A; } || true' EXIT

PLAN=()
step() { # step <description> <check-cmd> <apply-cmd>
  if eval "$2" >/dev/null 2>&1; then echo "OK      $1"; else
    echo "CHANGE  $1"; PLAN+=("$1")
    if [ "$APPLY" = 1 ]; then eval "$3" || die "failed: $1"; eval "$2" >/dev/null 2>&1 || die "not in place after apply: $1"; echo "        done"; fi
  fi
}
ip_rule_present() { az storage account show -n "$STG" -g "$RG" --query "networkRuleSet.ipRules[].ipAddressOrRange" -o tsv | grep -qx "$DEVICE_IP"; }
pe_ok() { # pe_ok <groupId>: an Approved PE connection for this sub-resource from the chosen subnet or any existing one
  az network private-endpoint-connection list --id "$STG_ID" --query "[?properties.privateLinkServiceConnectionState.status=='Approved'].properties.privateEndpoint.id" -o tsv | while read -r peid; do
    az network private-endpoint show --ids "$peid" --query "privateLinkServiceConnections[0].groupIds[0]" -o tsv; done | grep -qx "$1"; }
zone_ok() { az network private-dns zone show -g "$DNS_RG" -n "privatelink.$1.core.windows.net" >/dev/null; }
link_ok() { az network private-dns link vnet list -g "$DNS_RG" -z "privatelink.$1.core.windows.net" --query "[?virtualNetwork.id=='$VNET_ID'] | length(@)" -o tsv | grep -qv '^0$'; }
record_ok() { az network private-dns record-set a show -g "$DNS_RG" -z "privatelink.$1.core.windows.net" -n "$STG" >/dev/null; }
pe_name() { echo "pe-$STG-$1"; }

echo "== Agent A plan for $STG (rg $RG, $LOC); VNet $VNET/$SUBNET ($VNET_LOC); DNS rg $DNS_RG"
echo "   device IP: $(echo "$DEVICE_IP" | mask) (bare IP rule; /32 is not accepted, FB15)"
# 1. never lock out: device IP first
step "device IP rule on the storage firewall" ip_rule_present \
  "az storage account network-rule add -g '$RG' --account-name '$STG' --ip-address '$DEVICE_IP' -o none"
# 2. private endpoints + DNS (reuse existing Approved PEs, e.g. from the Purview setup)
for g in blob dfs; do
  step "private endpoint ($g) Approved" "pe_ok $g" \
    "az network private-endpoint create -g '$VNET_RG' -n '$(pe_name $g)' --location '$VNET_LOC' --subnet '$SUBNET_ID' --private-connection-resource-id '$STG_ID' --group-id $g --connection-name '$(pe_name $g)-conn' -o none"
  step "private DNS zone privatelink.$g.core.windows.net" "zone_ok $g" \
    "az network private-dns zone create -g '$DNS_RG' -n 'privatelink.$g.core.windows.net' -o none"
  step "zone privatelink.$g linked to $VNET" "link_ok $g" \
    "az network private-dns link vnet create -g '$DNS_RG' -z 'privatelink.$g.core.windows.net' -n 'link-$VNET' -v '$VNET_ID' -e false -o none"
  step "A record $STG in privatelink.$g" "record_ok $g" \
    "az network private-endpoint dns-zone-group create -g '$VNET_RG' --endpoint-name '$(pe_name $g)' -n default --private-dns-zone '/subscriptions/$SUB/resourceGroups/$DNS_RG/providers/Microsoft.Network/privateDnsZones/privatelink.$g.core.windows.net' --zone-name $g -o none"
done
# 3. restrict public access only after the IP rule exists (selected networks, default Deny). Bypass is left as it is.
step "public network access = Enabled from selected networks (default Deny)" \
  "[ \"\$(az storage account show -n '$STG' -g '$RG' --query '[publicNetworkAccess,networkRuleSet.defaultAction]' -o tsv | tr '\n' ' ')\" = 'Enabled Deny ' ]" \
  "ip_rule_present && az storage account update -g '$RG' -n '$STG' --public-network-access Enabled --default-action Deny -o none"
echo "   bypass (trusted Azure services) stays: $(az storage account show -n "$STG" -g "$RG" --query networkRuleSet.bypass -o tsv) (FB5: Fabric uses the resource instance rule instead)"

{
  echo "mode=$([ "$APPLY" = 1 ] && echo apply || echo plan) changes=${#PLAN[@]}"
  printf '%s\n' "${PLAN[@]:-none}"
  az storage account show -n "$STG" -g "$RG" --query "{publicNetworkAccess:publicNetworkAccess,networkRuleSet:networkRuleSet}" -o json
  az network private-endpoint-connection list --id "$STG_ID" --query "[].{pe:properties.privateEndpoint.id,status:properties.privateLinkServiceConnectionState.status}" -o json
} | save "A-$([ "$APPLY" = 1 ] && echo apply || echo plan).txt"
[ "$APPLY" = 1 ] && python3 "$ST" agent A done "network in place" >/dev/null
echo "== ${#PLAN[@]} change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed')"
