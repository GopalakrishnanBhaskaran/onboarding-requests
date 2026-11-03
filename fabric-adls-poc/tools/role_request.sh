#!/usr/bin/env bash
# Prints the exact role grants that let YOU do the storage-admin steps yourself (read-only; changes nothing).
# Give the output to a subscription Owner / User Access Administrator (an Entra Global Administrator has no
# Azure RBAC rights by default; they must first elevate access or ask a subscription Owner).
# Scope = the POC storage account only. Ask for them time-bound (PIM eligible / end date) for the POC.
# Facts: FB36 (built-in roles/storage 2026-07-01; delegate-role-assignments-overview 2026-03-30; custom-roles 2026-04-30).
source "$(dirname "$0")/lib.sh"
need az; need python3
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"; STG="$(env_get storageAccount)"
case "$SUB" in *'*'*|*UNVERIFIED*|"") SUB="$(stg_sub "$STG" || az account show --query id -o tsv)";; esac
RGSCOPE="/subscriptions/$SUB/resourceGroups/$RG"; SCOPE="$RGSCOPE/providers/Microsoft.Storage/storageAccounts/$STG"
ME="$(az ad signed-in-user show --query id -o tsv)"; UPN="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
DELEGATOR=db58b8e5-c6ad-4a2a-8342-4190687cbf4a; BLOBCONTRIB=ba92f5b4-2d11-453d-a403-e96b0029c9fe; RBACADMIN=f58310d9-a9f6-439a-9e8d-f62e7b41a168
cat <<EOF
=================== ROLE REQUEST for $UPN on $STG (scope: this storage account only) ===================
What it is for: Fabric POC steps on $STG that today need the storage admins
  1. resource instance rule (trusted workspace access)  -> Microsoft.Storage/storageAccounts/write
  2. approve the Fabric managed private endpoint         -> .../privateEndpointConnectionsApproval/action
  3. (optional) give the workspace identity 2 data roles -> Microsoft.Authorization/roleAssignments/write

--- Option A (recommended): custom role + constrained RBAC admin --------------------------------------
# A1. custom role: only account settings + private endpoint approval, NO account keys
cat > fabric-poc-storage-network.json <<'JSON'
{
  "Name": "Fabric POC storage network operator",
  "Description": "Add resource instance rules and approve private endpoint connections on one storage account. No account keys, no data access.",
  "Actions": [
    "Microsoft.Storage/storageAccounts/read",
    "Microsoft.Storage/storageAccounts/write",
    "Microsoft.Storage/storageAccounts/privateEndpointConnectionsApproval/action",
    "Microsoft.Storage/storageAccounts/privateEndpointConnections/read",
    "Microsoft.Storage/storageAccounts/privateEndpointConnections/write"
  ],
  "NotActions": [], "DataActions": [], "NotDataActions": [],
  "AssignableScopes": ["$RGSCOPE"]
}
JSON
az role definition create --role-definition @fabric-poc-storage-network.json     # needs roleDefinitions/write on the RG (Owner / User Access Administrator)
az role assignment create --assignee-object-id $ME --assignee-principal-type User --role "Fabric POC storage network operator" --scope $SCOPE
#   AssignableScopes must be a management group, subscription or resource group (custom-roles doc), so the role is defined
#   on the RG and assigned on the storage account only. Assigning a custom role at a single-resource scope: UNVERIFIED;
#   if Azure refuses it, assign at the RG ($RG; this also covers the other storage account in that RG).

# A2. (optional, for the identity roles) RBAC Administrator, limited to Storage Blob Delegator +
#     Storage Blob Data Contributor, assigned to service principals only
COND=\$(cat <<'X'
((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$DELEGATOR, $BLOBCONTRIB} AND @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'})) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {$DELEGATOR, $BLOBCONTRIB} AND @Resource[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase {'ServicePrincipal'}))
X
)
az role assignment create --assignee-object-id $ME --assignee-principal-type User --role $RBACADMIN --scope $SCOPE --condition "\$COND" --condition-version 2.0

--- Option B (simpler, built-in, broader) ---------------------------------------------------------------
az role assignment create --assignee-object-id $ME --assignee-principal-type User --role "Storage Account Contributor" --scope $SCOPE
#   note: Storage Account Contributor can read the account keys ("full access to storage account data";
#   shared key is enabled on this account) and change any account setting.
# plus A2 above for the identity roles (optional).

--- Rollback (end of POC) --------------------------------------------------------------------------------
az role assignment delete --assignee $ME --scope $SCOPE --role "Fabric POC storage network operator"   # or "Storage Account Contributor"
az role assignment delete --assignee $ME --scope $SCOPE --role $RBACADMIN
az role definition delete --name "Fabric POC storage network operator"
=================== END ===================
Any role with storageAccounts/write can also change the firewall; keep it scoped to $STG and time-bound.
After the grant (PIM: activate it), re-run access_check.sh: the three NO lines should turn YES.
EOF
