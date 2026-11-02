#!/usr/bin/env bash
# READ-ONLY handoff for other teams (e.g. Purview) that want to reuse the storage private endpoints and DNS.
# Prints full resource ids on screen (they are needed to configure a scan); the evidence copy is masked.
# Usage: bash fabric-adls-poc/tools/pe_details.sh
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
envq() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"].get(sys.argv[2],""))' "$STATE_FILE" "$1"; }
RG="$(envq resourceGroup)"; STG="$(envq storageAccount)"; VNET="$(envq vnet)"; SUBNET="$(envq peSubnet)"
case "$RG$STG" in *UNVERIFIED*|"") die "run w1_build_env.sh and a_storage_network.sh first";; esac
SUB="$(az account show --query id -o tsv)"; TENANT="$(az account show --query tenantId -o tsv)"
STG_ID="$(az storage account show -g "$RG" -n "$STG" --query id -o tsv)"
{
  echo "## Storage (ADLS Gen2) behind private endpoints: handoff, $(date -u +%FT%TZ)"
  echo "tenant:            $TENANT"
  echo "subscription:      $SUB"
  echo "resource group:    $RG   region: $(az storage account show -g "$RG" -n "$STG" --query location -o tsv)"
  echo "storage account:   $STG"
  echo "storage id:        $STG_ID"
  echo "endpoints:         https://$STG.blob.core.windows.net  https://$STG.dfs.core.windows.net"
  echo "firewall:          $(az storage account show -g "$RG" -n "$STG" --query "join(' ', [publicNetworkAccess, 'default=', networkRuleSet.defaultAction, 'bypass=', networkRuleSet.bypass])" -o tsv)"
  echo "ip rules:          $(az storage account show -g "$RG" -n "$STG" --query "length(networkRuleSet.ipRules)" -o tsv) (owner device only)"
  echo "resource rules:    $(az storage account show -g "$RG" -n "$STG" --query "networkRuleSet.resourceAccessRules[].resourceId" -o tsv | sed -E 's#.*/providers/##' | tr '\n' ' ')"
  echo "auth:              Entra ID only (shared key disabled)"
  echo
  echo "## VNet"
  az network vnet show -g "$RG" -n "$VNET" --query "{vnet:name,id:id,addressSpace:addressSpace.addressPrefixes,subnets:subnets[].{name:name,prefix:addressPrefix,delegations:delegations[].serviceName}}" -o json
  echo
  echo "## Private endpoints on the storage account (reusable by any client inside, or peered with, this VNet)"
  for peid in $(az network private-endpoint-connection list --id "$STG_ID" --query "[].properties.privateEndpoint.id" -o tsv); do
    az network private-endpoint show --ids "$peid" --query "{name:name,id:id,subnet:subnet.id,groupId:privateLinkServiceConnections[0].groupIds[0] || manualPrivateLinkServiceConnections[0].groupIds[0],status:privateLinkServiceConnections[0].privateLinkServiceConnectionState.status || manualPrivateLinkServiceConnections[0].privateLinkServiceConnectionState.status,nicId:networkInterfaces[0].id,dns:customDnsConfigs}" -o json 2>/dev/null \
      || echo "{\"id\":\"$peid\",\"note\":\"managed private endpoint (e.g. Fabric/Purview managed VNet), not in this subscription\"}"
  done
  echo
  echo "## Private DNS zones (link them to any VNet that must resolve the private IPs)"
  for z in privatelink.blob.core.windows.net privatelink.dfs.core.windows.net; do
    echo "- zone $z  id: $(az network private-dns zone show -g "$RG" -n "$z" --query id -o tsv)"
    echo "  A $STG -> $(az network private-dns record-set a show -g "$RG" -z "$z" -n "$STG" --query "aRecords[].ipv4Address" -o tsv | tr '\n' ' ')"
    echo "  linked VNets: $(az network private-dns link vnet list -g "$RG" -z "$z" --query "[].virtualNetwork.id" -o tsv | sed -E 's#.*/virtualNetworks/##' | tr '\n' ' ')"
  done
} > "$RAW_DIR/PE-details.txt" 2>&1
mask < "$RAW_DIR/PE-details.txt" > "$EVIDENCE_DIR/PE-details.txt"
cat "$RAW_DIR/PE-details.txt"
echo
echo "(full values above are for your Purview team; the masked copy is evidence/PE-details.txt)"
