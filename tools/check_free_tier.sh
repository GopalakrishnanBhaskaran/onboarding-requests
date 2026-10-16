#!/usr/bin/env bash
# Pre-deploy checks for the zero-cost pilot. Read-only: creates nothing.
#   tools/check_free_tier.sh <subscriptionId>
# Exit 0 = OK to proceed to what-if; 2 = STOP and ask Gokul.
set -euo pipefail
SUB="${1:?usage: check_free_tier.sh <subscriptionId>}"
az account set --subscription "$SUB"
echo "Subscription: $(az account show --query '{name:name,id:id,tenant:tenantId}' -o tsv)"

stop=0

echo "== Cosmos DB free tier (one per subscription) =="
FREE=$(az cosmosdb list --query "[?enableFreeTier].{name:name, rg:resourceGroup, location:location}" -o tsv)
OTHER=$(az cosmosdb list --query "[?enableFreeTier && resourceGroup!='rg-pmo-review-pilot'].name" -o tsv)
if [[ -n "$OTHER" ]]; then
  echo "STOP: a free-tier Cosmos DB account already exists outside the pilot:"; echo "$FREE"; stop=1
elif [[ -n "$FREE" ]]; then
  echo "OK: the free-tier account is the pilot's own (redeploy keeps it): $FREE"
else
  echo "OK: no free-tier Cosmos DB account in this subscription."
fi

echo "== Static Web Apps Free plan apps in subscription (Free quota per subscription, see VERIFIED_FACTS F7) =="
SWA_FREE=$(az staticwebapp list --query "length([?sku.name=='Free'])" -o tsv)
echo "Free-plan static web apps: $SWA_FREE"
if (( SWA_FREE >= 10 )); then echo "STOP: Free-plan quota reached."; stop=1; fi

echo "== Resource group =="
if [[ "$(az group exists --name rg-pmo-review-pilot)" == "true" ]]; then
  echo "NOTE: rg-pmo-review-pilot already exists. Contents:"
  az resource list -g rg-pmo-review-pilot --query "[].{type:type,name:name}" -o table
fi

echo "== Permissions (need Owner, or Contributor + Resource Policy Contributor) =="
ME=$(az ad signed-in-user show --query id -o tsv 2>/dev/null || true)
if [[ -n "$ME" ]]; then
  az role assignment list --assignee "$ME" --scope "/subscriptions/$SUB" --include-inherited --query "[].roleDefinitionName" -o tsv | sort -u
else
  echo "Could not read signed-in user (service principal?). Check roles manually."
fi

echo "== Billing currency (budget amount uses it; pilot assumes INR) =="
echo "Check in portal: Cost Management + Billing > Billing account > Properties > Currency."

(( stop == 0 )) && echo "RESULT: OK to run what-if." || { echo "RESULT: STOP — report to Gokul before continuing."; exit 2; }
