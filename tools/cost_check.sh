#!/usr/bin/env bash
# Lists every resource in the pilot RG, flags non-allowed types, and shows month-to-date actual cost.
#   tools/cost_check.sh [resourceGroup]       (default rg-pmo-review-pilot)
# Cost data lags 24-48 h: a zero on day 0 is not proof. Re-run after 24-48 h.
set -euo pipefail
RG="${1:-rg-pmo-review-pilot}"
SUB=$(az account show --query id -o tsv)
ALLOWED='^Microsoft\.(Web/staticSites|DocumentDB/databaseAccounts)$'
# Phase 2 (approved 16 Oct 2026): exactly one Flex plan, one Function App and one storage account (the AI agent worker).
PHASE2='^Microsoft\.(Web/serverFarms|Web/sites|Storage/storageAccounts)$'

echo "== Resources in $RG =="
az resource list -g "$RG" --query "[].{type:type, name:name, sku:sku.name, location:location}" -o table
TYPES=$(az resource list -g "$RG" --query "[].type" -o tsv)
BAD=$(echo "$TYPES" | grep -Ev "$ALLOWED" | grep -Eiv "$PHASE2" || true)
if [[ -n "$BAD" ]]; then echo "FLAG: non-allowed resource types:"; echo "$BAD"; else echo "OK: only allowed resource types."; fi
MORE=$(echo "$TYPES" | grep -Ei "$PHASE2" | sort -f | uniq -ci | awk '$1>1' || true)
if [[ -n "$MORE" ]]; then echo "FLAG: more than one Phase 2 resource of a type (approved: one each):"; echo "$MORE"; fi
for p in $(az functionapp plan list -g "$RG" --query "[?sku.name!='FC1'].name" -o tsv 2>/dev/null); do echo "FLAG: plan $p is not Flex Consumption (FC1)"; done

echo "== SKU / free-tier checks =="
for s in $(az staticwebapp list -g "$RG" --query "[].name" -o tsv); do
  echo "SWA $s sku: $(az staticwebapp show -n "$s" -g "$RG" --query sku.name -o tsv)"
done
for c in $(az cosmosdb list -g "$RG" --query "[].name" -o tsv); do
  echo "Cosmos $c enableFreeTier: $(az cosmosdb show -n "$c" -g "$RG" --query enableFreeTier -o tsv)"
  az cosmosdb sql database throughput show -a "$c" -g "$RG" -n pmoreview --query "{throughput:resource.throughput, autoscale:resource.autoscaleSettings}" -o json || true
done

echo "== Policy assignments on $RG =="
az policy assignment list -g "$RG" --query "[].{name:name, enforcement:enforcementMode, id:id}" -o table

echo "== Budgets on $RG =="
az consumption budget list --resource-group "$RG" --query "[].{name:name, amount:amount, spent:currentSpend.amount, unit:currentSpend.unit}" -o table 2>/dev/null || echo "(budget query not available for this account type)"

echo "== Month-to-date actual cost for $RG =="
if ! az rest --method post \
  --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.CostManagement/query?api-version=2023-11-01" \
  --body '{"type":"ActualCost","timeframe":"MonthToDate","dataset":{"granularity":"None","aggregation":{"totalCost":{"name":"Cost","function":"Sum"}},"grouping":[{"type":"Dimension","name":"ResourceType"}]}}' \
  --query "properties.rows" -o json; then
  echo "Cost query not available yet (common for new subscriptions: data can take 24-48 h, and some offer types"
  echo "do not support RG-scope queries). Use the budget 'spent' value above, and Cost analysis in the portal."
fi
echo "Checked at $(date -u +%Y-%m-%dT%H:%MZ). Record this output in docs/COSTS.md."
