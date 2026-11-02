#!/usr/bin/env bash
# READ-ONLY feasibility check for the Fabric side: who you are, Fabric API access, F-capacity quota/SKUs in the region,
# resource providers, existing capacities/workspaces/tenant settings. Changes nothing.
# Usage: bash fabric-adls-poc/tools/fabric_check.sh [region]   (default centralindia)
source "$(dirname "$0")/lib.sh"
set +e; trap - ERR
REGION="${1:-centralindia}"
AZ_BIN="$(command -v az)"; az() { timeout 90 "$AZ_BIN" --only-show-errors "$@"; }
SUB="$(az account show --query id -o tsv)"
exec 3>&2
{
  echo "## identity"
  az ad signed-in-user show --query "{upn:userPrincipalName,userType:userType,mail:mail}" -o tsv 2>&1 | sed -E 's/^[^#\t]+#EXT#/*** (external MSA account, #EXT#)/'
  az role assignment list --assignee "$(az ad signed-in-user show --query id -o tsv 2>/dev/null)" --all --query "[].{role:roleDefinitionName,scope:scope}" -o tsv 2>&1 | sed -E 's#/subscriptions/[^/]+#/subscriptions/<sub>#' | head -15
  echo "## Entra directory roles of the signed-in user"
  az rest --method get --url "https://graph.microsoft.com/v1.0/me/memberOf/microsoft.graph.directoryRole?\$select=displayName" --query "value[].displayName" -o tsv 2>&1 | head -10
  echo "## resource providers"
  for p in Microsoft.Fabric Microsoft.PowerPlatform Microsoft.Network Microsoft.Storage; do echo "$p $(az provider show -n $p --query registrationState -o tsv 2>&1)"; done
  echo "## Fabric SKUs offered to this subscription in $REGION"
  az rest --method get --url "https://management.azure.com/subscriptions/$SUB/providers/Microsoft.Fabric/skus?api-version=2023-11-01" \
    --query "value[?contains(locations,'$REGION') || contains(locations,'Central India')].name" -o tsv 2>&1 | tr '\n' ' '; echo
  echo "## Fabric capacity quota (usages) in $REGION"
  az rest --method get --url "https://management.azure.com/subscriptions/$SUB/providers/Microsoft.Fabric/locations/$REGION/usages?api-version=2023-11-01" \
    --query "value[].{name:name.value,current:currentValue,limit:limit,unit:unit}" -o tsv 2>&1 | head -5
  echo "## subscription offer (credits / pay-as-you-go)"
  az rest --method get --url "https://management.azure.com/subscriptions/$SUB?api-version=2022-12-01" --query "{state:state,quotaId:subscriptionPolicies.quotaId,spendingLimit:subscriptionPolicies.spendingLimit}" -o tsv 2>&1
  echo "## Fabric API (token for api.fabric.microsoft.com)"
  r="$(az rest --method get --resource https://api.fabric.microsoft.com --url https://api.fabric.microsoft.com/v1/workspaces 2>&1)"
  if echo "$r" | grep -q '"value"'; then
    echo "Fabric API: OK"
    echo "$r" | python3 -c 'import json,sys;d=json.load(sys.stdin);[print("workspace:",w["displayName"],w.get("type"),"capacity" if w.get("capacityId") else "shared") for w in d["value"]]'
  else echo "Fabric API: FAILED $(echo "$r" | head -c 400)"; fi
  echo "## Fabric capacities visible to you"
  az rest --method get --resource https://api.fabric.microsoft.com --url https://api.fabric.microsoft.com/v1/capacities 2>&1 | python3 -c 'import json,sys
try:
    d=json.load(sys.stdin); [print("capacity:",c["displayName"],c["sku"],c["region"],c["state"]) for c in d["value"]] or print("none")
except Exception as e: print("not readable")'
  echo "## Fabric tenant settings (needs the Fabric administrator role)"
  az rest --method get --resource https://api.fabric.microsoft.com --url https://api.fabric.microsoft.com/v1/admin/tenantsettings 2>&1 | python3 -c 'import json,sys
try:
    d=json.load(sys.stdin)
    for s in d["tenantSettings"]:
        n=(s.get("settingName","")+" "+s.get("title","")).lower()
        if any(k in n for k in ("identity","private","service principal","serviceprincipal","create fabric","fabric items","workspace","gateway","trial","onelake")):
            print("setting:", s.get("settingName"), "=", s.get("enabled"), "|", s.get("title"))
except Exception: print("not readable (you are not a Fabric administrator, or Fabric is not set up for this account)")'
} > "$RAW_DIR/00-fabric-check.txt" 2>&1
mask < "$RAW_DIR/00-fabric-check.txt" > "$EVIDENCE_DIR/00-fabric-check.txt"
echo "=================== PASTE EVERYTHING BELOW THIS LINE BACK TO CLAUDE ==================="
cat "$EVIDENCE_DIR/00-fabric-check.txt"
echo "=================== END ==================="
