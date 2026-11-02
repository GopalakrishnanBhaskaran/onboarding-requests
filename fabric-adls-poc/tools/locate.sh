#!/usr/bin/env bash
# READ-ONLY: find the Purview / pvdbx resources across every subscription this sign-in can see.
# Usage: bash fabric-adls-poc/tools/locate.sh [name-fragment ...]   (default fragments: pvdbx purview)
source "$(dirname "$0")/lib.sh"
need az; need python3
FRAGS=("${@:-pvdbx purview}"); FRAGS=(${FRAGS[*]})
az account show >/dev/null 2>&1 || die "not signed in"
{
  echo "## signed in: tenant $(az account show --query tenantId -o tsv) user type $(az account show --query user.type -o tsv)"
  echo "## tenants this account can see"
  az account tenant list --query "[].{tenant:tenantId,domain:defaultDomain,name:displayName}" -o tsv 2>/dev/null || echo "(tenant list not available)"
  echo "## subscriptions (all states)"
  az account list --all --query "[].[name,id,state,tenantId]" -o tsv
  for s in $(az account list --all --query "[?state=='Enabled'].id" -o tsv); do
    echo
    echo "### subscription $(az account list --all --query "[?id=='$s'].name | [0]" -o tsv) ($s)"
    q="[?"; first=1
    for f in "${FRAGS[@]}"; do
      lo="$(echo "$f" | tr '[:upper:]' '[:lower:]')"; up="$(echo "$f" | tr '[:lower:]' '[:upper:]')"
      [ $first = 1 ] || q+=" || "; first=0
      q+="contains(name,'$lo') || contains(name,'$up') || contains(resourceGroup,'$lo') || contains(resourceGroup,'$up')"
    done
    q+=" || type=='Microsoft.Storage/storageAccounts' || type=='Microsoft.Purview/accounts' || type=='Microsoft.Fabric/capacities' || type=='Microsoft.Network/virtualNetworks' || type=='Microsoft.Network/privateDnsZones' || type=='Microsoft.Network/privateEndpoints' || type=='Microsoft.Databricks/workspaces'].[type,name,resourceGroup,location]"
    az resource list --subscription "$s" --query "$q" -o tsv 2>&1 | sort | sed 's/^/  /' || echo "  (cannot list resources)"
    echo "  storage accounts (name hns publicNetworkAccess):"
    az storage account list --subscription "$s" --query "[].[name,isHnsEnabled,publicNetworkAccess]" -o tsv 2>&1 | sed 's/^/    /'
    echo "  resource groups: $(az group list --subscription "$s" --query "[].name" -o tsv 2>/dev/null | tr '\n' ' ')"
  done
} > "$RAW_DIR/00-locate.txt" 2>&1
mask < "$RAW_DIR/00-locate.txt" > "$EVIDENCE_DIR/00-locate.txt"
echo "=================== PASTE EVERYTHING BELOW THIS LINE BACK TO CLAUDE ==================="
cat "$EVIDENCE_DIR/00-locate.txt"
echo "=================== END ==================="
