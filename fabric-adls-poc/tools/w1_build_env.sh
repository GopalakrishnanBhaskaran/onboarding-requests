#!/usr/bin/env bash
# Wave 1 (base): a NEW, separate POC set, because no pvdbx- network exists in this subscription (locate.sh, 2026-11-02).
# Default: PLAN (read-only). --apply needs wave 1 approval in state.json.
# Creates only in rg-pvdbx-fabric-poc (not the PMO pilot RG): resource group, RG budget, VNet + PE subnet, HNS storage + container.
# The storage account is created LOCKED (public access from selected networks, default Deny, no trusted-services bypass,
# no shared keys); Agent A then adds your IP, the private endpoints and DNS.
# Usage: bash w1_build_env.sh [--region centralindia] [--budget 1000] [--apply]
source "$(dirname "$0")/lib.sh"
need az; need python3
ST="$POC_DIR/tools/state.py"
REGION="centralindia"; BUDGET=1000; APPLY=0
RG="rg-pvdbx-fabric-poc"; VNET="pvdbx-vnet-fabricpoc"; SPACE="10.60.0.0/24"; PE_SUBNET="snet-pe"; PE_PREFIX="10.60.0.0/27"
CONTAINER="poc"; FOLDER="input"
while [ $# -gt 0 ]; do case "$1" in --region) REGION="$2"; shift 2;; --budget) BUDGET="$2"; shift 2;; --apply) APPLY=1; shift;; *) die "unknown $1";; esac; done
SUB="$(az account show --query id -o tsv)"; TENANT="$(az account show --query tenantId -o tsv)"
ME_MAIL="$(az ad signed-in-user show --query "mail || userPrincipalName" -o tsv)"
# storage name: stpvdbxfab + 6 chars derived from the subscription id (stable across re-runs, globally unique enough)
STG="stpvdbxfab$(printf '%s' "$SUB" | sha256sum | cut -c1-6)"
[ "$APPLY" = 1 ] && { require_approval 1; python3 "$ST" lock storage-account A; python3 "$ST" lock vnet A; python3 "$ST" agent A running "build base"; }
trap '[ "$APPLY" = 1 ] && { python3 "$ST" unlock storage-account A; python3 "$ST" unlock vnet A; } || true' EXIT
N=0
item() { # item <label> <exists-check> <create-cmd>
  if eval "$2" >/dev/null 2>&1; then echo "OK      $1"; else echo "CHANGE  $1"; N=$((N+1))
    if [ "$APPLY" = 1 ]; then eval "$3" || die "failed: $1"; echo "        done"; fi; fi
}
echo "== Wave 1 base plan (subscription $(echo "$SUB" | mask), region $REGION)"
item "resource group $RG (tag purpose=fabric-adls-poc)" "az group show -n $RG" \
  "az group create -n $RG -l $REGION --tags purpose=fabric-adls-poc owner=pmo-poc -o none"
item "budget bud-$RG: INR $BUDGET/month, alerts 50/80/100% to you" \
  "az rest --method get --url https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Consumption/budgets/bud-$RG?api-version=2023-11-01" \
  "az rest --method put --url https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Consumption/budgets/bud-$RG?api-version=2023-11-01 --body '{\"properties\":{\"category\":\"Cost\",\"amount\":$BUDGET,\"timeGrain\":\"Monthly\",\"timePeriod\":{\"startDate\":\"$(date -u +%Y-%m-01)T00:00:00Z\",\"endDate\":\"$(date -u -d '+1 year' +%Y-%m-01)T00:00:00Z\"},\"notifications\":{\"a50\":{\"enabled\":true,\"operator\":\"GreaterThan\",\"threshold\":50,\"thresholdType\":\"Actual\",\"contactEmails\":[\"$ME_MAIL\"]},\"a80\":{\"enabled\":true,\"operator\":\"GreaterThan\",\"threshold\":80,\"thresholdType\":\"Actual\",\"contactEmails\":[\"$ME_MAIL\"]},\"a100\":{\"enabled\":true,\"operator\":\"GreaterThan\",\"threshold\":100,\"thresholdType\":\"Actual\",\"contactEmails\":[\"$ME_MAIL\"]}}}}' -o none"
item "VNet $VNET $SPACE with subnet $PE_SUBNET $PE_PREFIX (free)" "az network vnet subnet show -g $RG --vnet-name $VNET -n $PE_SUBNET" \
  "az network vnet create -g $RG -n $VNET -l $REGION --address-prefixes $SPACE --subnet-name $PE_SUBNET --subnet-prefixes $PE_PREFIX -o none"
item "storage $STG: ADLS Gen2 (HNS), Standard_LRS Hot, TLS1.2, no shared key, no blob public access, public access = selected networks, default Deny, bypass None" \
  "az storage account show -g $RG -n $STG" \
  "az storage account create -g $RG -n $STG -l $REGION --sku Standard_LRS --kind StorageV2 --access-tier Hot --hns true --min-tls-version TLS1_2 --allow-shared-key-access false --allow-blob-public-access false --public-network-access Enabled --default-action Deny --bypass None --tags purpose=fabric-adls-poc -o none"
item "container $CONTAINER (control plane; not affected by the firewall)" "az storage container-rm show --storage-account $STG -g $RG -n $CONTAINER" \
  "az storage container-rm create --storage-account $STG -g $RG -n $CONTAINER --public-access off -o none"
if [ "$APPLY" = 1 ]; then
  for k in "resourceGroup=$RG" "storageAccount=$STG" "container=$CONTAINER" "testFolder=$FOLDER" "region=$REGION" "subscription=$SUB" "tenant=$TENANT" "vnet=$VNET" "peSubnet=$PE_SUBNET"; do
    python3 "$ST" set "env.${k%%=*}" "${k#*=}" >/dev/null; done
  az storage account show -g "$RG" -n "$STG" --query "{name:name,hns:isHnsEnabled,publicNetworkAccess:publicNetworkAccess,networkRuleSet:networkRuleSet,sharedKey:allowSharedKeyAccess,tls:minimumTlsVersion}" -o json | save W1-base-storage-after.json
  python3 "$ST" agent A running "base built; next a_storage_network.sh" >/dev/null
fi
echo "== $N change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed')"
echo "Cost: VNet 0; storage about INR 1.90/GB-month (test data is a few KB); budget 0. Paid items come with Agent A (2 PEs about INR 0.95/h each,"
echo "2 private DNS zones INR 47.61/zone/month) and Agent B (F2 about INR 38/h while running; pause when idle)."
