#!/usr/bin/env bash
# PMO onboarding flow (v2) deployment, in explicit steps.
#
#   tools/deploy_free.sh whatif <subscriptionId> <params.json>
#       Read-only. Writes the what-if to .local/whatif.txt for Gokul to review.
#
#   tools/deploy_free.sh app <subscriptionId>
#       App-only redeploy (no infrastructure change): API + SPA. Covered by the original approval. The live app holds the
#       real tenants (tools/import_workbook.py); SAMPLE data is seeded only with SEED_SAMPLE=1.
#
#   tools/deploy_free.sh apply <subscriptionId> <params.json> --approved-by "<name> <date>"
#       ONLY after Gokul replied "approved" to that exact what-if. Creates resources, sets the Cosmos
#       connection string as a SWA app setting (never printed or written to disk), deploys the app,
#       seeds SAMPLE data. With enableHandoff + deployWorker also deploys the AI agent worker.
#
#   tools/deploy_free.sh status <subscriptionId>
#       Read-only. Shows where the deployment stands (use after a Cloud Shell disconnect).
#
#   tools/deploy_free.sh params <subscriptionId>
#       Rebuilds .local/params.json from the live deployment after a Cloud Shell reset.
#
#   tools/deploy_free.sh worker <subscriptionId>
#       Redeploy the AI agent worker code only.
#
#   tools/deploy_free.sh grant-graph <subscriptionId>
#       Give the worker's managed identity its Microsoft Graph READ permissions (User.Read.All, GroupMember.Read.All,
#       UserAuthenticationMethod.Read.All, AuditLog.Read.All). Idempotent. Needs Global Administrator or
#       Privileged Role Administrator.
#
#   tools/deploy_free.sh revoke-graph-writes <subscriptionId> [--approved-by "<name> <date>"]
#       Removes the retired Phase 2b/2c Graph permissions (access packages, ReadWrite.All roles) from the worker
#       identity. Security change: without --approved-by it only lists what it would remove.
#
#   tools/deploy_free.sh provisioning <subscriptionId> on <scope> [<roleIds> | --from-sheet <PROJECT>] [--approved-by "<name> <date>"]
#   tools/deploy_free.sh provisioning <subscriptionId> off [--approved-by "<name> <date>"]
#       v2.1 agent provisioning after both approvals. "on": Graph User.Create, Group.Create, GroupMember.ReadWrite.All;
#       Role Based Access Control Administrator on <scope> constrained to <roleIds> (default Reader) and user/group
#       principals; worker + SWA settings; creates the empty resource group <scope> if it is missing (free).
#       --from-sheet <PROJECT>: the roles are every Azure role on the project's mapping sheet that the agent can resolve at
#       <scope> (tools/sheet_roles.py; never a privileged administrator role), added to the roles already allowed.
#       "off": settings off, then (approved) removes those rights.
#       Security change: without --approved-by it only shows what it would do.
#
#   tools/deploy_free.sh tap <subscriptionId> on [--lifetime <minutes>] [--approved-by "<name> <date>"]
#   tools/deploy_free.sh tap <subscriptionId> off [--approved-by "<name> <date>"]
#       Agent TAP (requested by Gokul 22 Oct 2026): for an account it has just created after both approvals, the agent
#       mails a one-time Temporary Access Pass, the initial password and the MFA steps to the Enterprise ID mail, once.
#       "on": Graph application permission UserAuthMethod-TAP.ReadWrite.All + worker/SWA AGENT_TAP=true. Needs agent
#       provisioning and mail on. Shows the tenant's TAP policy. Security change: dry run without --approved-by.
#       "off": AGENT_TAP=false at once (the ID team issues passes again); removing the permission needs --approved-by.
#
#   tools/deploy_free.sh privileged <subscriptionId> on|off [--approved-by "<name> <date>"]
#       Agent privileged rows (requested by Gokul 22 Oct 2026): the agent also provisions and assigns mapping rows flagged
#       privileged (e.g. Data Engineer: Storage Blob Data Contributor, Key Vault Secrets Officer), after both approvals, with
#       the same checks; never Owner, Contributor, User Access Administrator, RBAC Administrator, Reservations Administrator
#       or Key Vault Data Access Administrator. Settings only (AGENT_PRIVILEGED); the roles still need the allow-list
#       (provisioning ... on <scope> <roleIds>). "on" is a security change: dry run without --approved-by. "off" any time.
#
#   tools/deploy_free.sh environment <subscriptionId> <pilot|dev|test|prod> [--redirect <mail>]
#       Settings only (worker + SWA): APP_ENVIRONMENT, NOTIFY_REDIRECT_TO (test environments: every mail goes to that
#       one address, naming the intended recipients), AGENT_TENANT_ID (tenant separation). prod refuses a redirect.
#
#   tools/deploy_free.sh email <subscriptionId> [--redirect <mail>] [--approved-by "<name> <date>"]
#       Mail through Azure Communication Services Email (managed identity, Azure-managed DoNotReply domain). New resources
#       (pay per mail, from credits) + one role for the worker on that resource: dry run (plan + what-if) without
#       --approved-by. Approved: policy stage A (allow the 3 types), wait for it, create the resources, switch the worker
#       to NOTIFY_SENDER=AcsEmail.
#
#   tools/deploy_free.sh databricks <subscriptionId> [--location <region>] [--approved-by "<name> <date>"]
#       One SERVERLESS Azure Databricks workspace (Premium; no managed resource group, VNet, NAT gateway or storage account;
#       pay per DBU only while serverless compute runs, F71-F76). New resource: dry run (plan + what-if) without
#       --approved-by. Approved: register the Microsoft.Databricks provider if needed, policy stage A (allow
#       Microsoft.Databricks/workspaces), wait for it, create the workspace. Then add the Entra groups to it with
#       tools/databricks_setup.py (dry run, then approval; automatic identity management keeps membership in sync).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
source "$(dirname "$0")/tool_path.sh" # az on the PATH (Mac: Homebrew), else how to install it
CMD="${1:?whatif|apply|app|status|params|worker|grant-graph|revoke-graph-writes|provisioning|tap|privileged|environment|email|databricks}"; SUB="${2:?subscriptionId}"; PARAMS="${3:-}"
DEPLOYMENT="pmo-review-pilot"
if [[ "$CMD" == "whatif" || "$CMD" == "apply" ]]; then
  [[ -n "$PARAMS" ]] || { echo "params.json required for $CMD"; exit 1; }
  LOCATION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['parameters']['location']['value'])" "$PARAMS")"
fi
mkdir -p "$ROOT/.local"
az account set --subscription "$SUB"
RG_DEFAULT="rg-pmo-review-pilot"

param_true() { [[ -n "$PARAMS" ]] && python3 -c "import json,sys;p=json.load(open(sys.argv[1]))['parameters'];sys.exit(0 if p.get(sys.argv[2],{}).get('value') else 1)" "$PARAMS" "$1"; }
handoff_enabled() { param_true enableHandoff; }
worker_enabled() { handoff_enabled && param_true deployWorker; }

# Stage B needs stage A's policy to be live: Azure checks new resources against the CURRENT assignment.
policy_allows_worker() {
  az policy assignment show -g "$RG_DEFAULT" -n pmo-pilot-allowed-types \
    --query "parameters.listOfResourceTypesAllowed.value" -o tsv 2>/dev/null | grep -qx "Microsoft.Web/sites"
}

worker_name() { az functionapp list -g "$RG_DEFAULT" --query "[?starts_with(name,'func-pmo-worker-')].name | [0]" -o tsv; }
worker_setting() { az functionapp config appsettings list -g "$RG_DEFAULT" -n "$1" --query "[?name=='$2'].value | [0]" -o tsv 2>/dev/null || true; }

# global.json needs the .NET 10 SDK (the worker targets net10.0; the API's net9.0 builds with it too).
# Azure Cloud Shell ships .NET 9 only, so install 10 into ~/.dotnet with Microsoft's script (user folder, no admin).
# Ephemeral Cloud Shell sessions lose it; this re-installs on the next run (about 1-2 minutes).
ensure_dotnet10() {
  if dotnet --list-sdks 2>/dev/null | grep -q '^10\.'; then return 0; fi
  if ! { [[ -x "$HOME/.dotnet/dotnet" ]] && "$HOME/.dotnet/dotnet" --list-sdks | grep -q '^10\.'; }; then
    echo "Installing .NET 10 SDK into ~/.dotnet ..."
    curl -sSL -o "$ROOT/.local/dotnet-install.sh" https://dot.net/v1/dotnet-install.sh
    bash "$ROOT/.local/dotnet-install.sh" --channel 10.0 --install-dir "$HOME/.dotnet"
  fi
  export DOTNET_ROOT="$HOME/.dotnet" PATH="$HOME/.dotnet:$PATH"
  echo ".NET SDKs: $(dotnet --list-sdks | tr '\n' ' ')"
}

# AI agent worker: build, zip, deploy (Flex Consumption one-deploy via Entra ID; basic auth is off).
deploy_worker() {
  local name; name=$(worker_name)
  [[ -n "$name" ]] || { echo "No worker in $RG_DEFAULT (enableHandoff/deployWorker=false?)."; return 1; }
  rm -rf "$ROOT/.local/worker" "$ROOT/.local/worker.zip"
  # Tenant separation: the agent reads and writes Entra only for projects in its own tenant.
  if [[ -z "$(worker_setting "$name" AGENT_TENANT_ID)" ]]; then
    az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" -o none --settings "AGENT_TENANT_ID=$(az account show --query tenantId -o tsv)"
    echo "Set AGENT_TENANT_ID on $name (the tenant of this subscription)."
  fi
  dotnet publish "$ROOT/src/worker/PmoReview.Worker" -c Release -o "$ROOT/.local/worker"
  (cd "$ROOT/.local/worker" && zip -qr "$ROOT/.local/worker.zip" .)
  # A just-created role assignment (Blob Data Owner on the package container) can take minutes to apply: retry.
  local i
  for i in 1 2 3 4; do
    if az functionapp deployment source config-zip -g "$RG_DEFAULT" -n "$name" --src "$ROOT/.local/worker.zip" -o none; then
      echo "Worker deployed: $name (commit $(git -C "$ROOT" rev-parse --short HEAD)). It runs every 5 minutes."
      echo "$(git -C "$ROOT" rev-parse --short HEAD) $(date -u +%FT%TZ)" > "$ROOT/.local/last-worker-deploy"
      return 0
    fi
    [[ $i -lt 4 ]] && { echo "Worker upload failed (attempt $i/4), likely role propagation. Retrying in 90 s..."; sleep 90; }
  done
  echo "Worker upload failed 4 times. Later run: tools/deploy_free.sh worker <subscriptionId>"
  return 1
}

# Graph application permissions for the worker identity: READ ONLY in v2.
GRAPH_READ_ROLES=(User.Read.All GroupMember.Read.All UserAuthenticationMethod.Read.All AuditLog.Read.All)
# Phase 2b/2c permissions the v2 agent must not hold (it never creates or assigns) and no longer needs.
GRAPH_RETIRED_ROLES=(EntitlementManagement.ReadWrite.All User.ReadWrite.All LicenseAssignment.ReadWrite.All UserAuthenticationMethod.ReadWrite.All Group.ReadWrite.All EntitlementManagement.Read.All User.ReadBasic.All)

graph_role_id() { az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?value=='$1'].id | [0]" -o tsv; }

list_graph_roles() {
  az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$1/appRoleAssignments" --query "value[].appRoleId" -o tsv \
    | while read -r id; do az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?id=='$id'].value | [0]" -o tsv; done
}

grant_graph() {
  local name mi graph perm role have
  name=$(worker_name); [[ -n "$name" ]] || { echo "No worker found."; return 1; }
  mi=$(az functionapp identity show -g "$RG_DEFAULT" -n "$name" --query principalId -o tsv)
  graph=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
  for perm in "${GRAPH_READ_ROLES[@]}"; do
    role=$(graph_role_id "$perm")
    [[ -n "$role" ]] || { echo "Could not find $perm on Microsoft Graph."; return 1; }
    have=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
      --query "length(value[?appRoleId=='$role'])" -o tsv)
    if [[ "$have" == "0" ]]; then
      az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
        --headers "Content-Type=application/json" \
        --body "{\"principalId\":\"$mi\",\"resourceId\":\"$graph\",\"appRoleId\":\"$role\"}" -o none
      echo "Granted $perm (application, read) to $name. Allow a few minutes to take effect."
    else
      echo "Already granted: $perm -> $name"
    fi
  done
  echo "Graph permissions now held by $name (expected read-only: ${GRAPH_READ_ROLES[*]}):"
  local held; held=$(list_graph_roles "$mi"); echo "$held"
  if echo "$held" | grep -qxE "$(IFS='|'; echo "${GRAPH_RETIRED_ROLES[*]}")"; then
    echo "WARNING: the worker still holds retired Phase 2b/2c permissions. After approval run:"
    echo "  tools/deploy_free.sh revoke-graph-writes <subscriptionId> --approved-by \"<name> <date>\""
  fi
}

# Security change: removes the retired Phase 2b/2c Graph permissions from the worker identity. Dry run without approval.
revoke_graph_writes() {
  local name mi perm role assignment
  name=$(worker_name); [[ -n "$name" ]] || { echo "No worker found."; return 1; }
  mi=$(az functionapp identity show -g "$RG_DEFAULT" -n "$name" --query principalId -o tsv)
  for perm in "${GRAPH_RETIRED_ROLES[@]}"; do
    role=$(graph_role_id "$perm")
    assignment=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
      --query "value[?appRoleId=='$role'].id | [0]" -o tsv)
    [[ -n "$assignment" ]] || { echo "Not held: $perm"; continue; }
    if [[ -z "${APPROVED_BY:-}" ]]; then
      echo "Would remove: $perm (dry run; pass --approved-by after explicit approval)"
    else
      az rest --method DELETE --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments/$assignment" -o none
      echo "Removed: $perm (approved by $APPROVED_BY)"
    fi
  done
  echo "Graph permissions now held by $name:"
  list_graph_roles "$mi"
}

# v2.1 agent provisioning (chosen 17 Oct 2026): least-privileged Graph CREATE roles (VERIFIED_FACTS F56-F58) and a
# constrained Azure role-assignment right (F59-F60). Security change: dry run unless --approved-by.
GRAPH_PROVISION_ROLES=(User.Create Group.Create GroupMember.ReadWrite.All)
READER_ROLE_ID=acdd72a7-3385-48ef-bd42-f606fba81ae7
RBAC_ADMIN_ROLE_ID=f58310d9-a9f6-439a-9e8d-f62e7b41a168
# Never on the agent's allow-list (BuiltInRoles.NeverByAgent, VERIFIED_FACTS F68): Owner, Contributor, Reservations
# Administrator, Role Based Access Control Administrator, User Access Administrator, Key Vault Data Access Administrator.
NEVER_BY_AGENT="8e3af657-a8ff-443c-a75c-2fe8c4bcb635 b24988ac-6180-42a0-ab88-20f7382dd24c a8889054-8d42-49c9-bc1c-52486c10e7cd f58310d9-a9f6-439a-9e8d-f62e7b41a168 18d7d88d-d35e-4fb5-a5c3-7773c20a72d9 8b54135c-b56d-4d72-a534-26097cfdc8d8"

# Refuses a role list with a privileged administrator role or anything that is not a role GUID.
check_role_ids() {
  local id never
  for id in ${1//,/ }; do
    [[ "$id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || { echo "Not a role definition id: $id"; return 1; }
    for never in $NEVER_BY_AGENT; do
      [[ "$(tr '[:upper:]' '[:lower:]' <<<"$id")" != "$never" ]] || { echo "Refusing: $id is a privileged administrator role; the agent never assigns it."; return 1; }
    done
  done
}

# Condition from Learn (F60): only these roles, only user or group principals, for both assign and remove.
rbac_condition() {
  local ids="$1" types="{'User', 'Group'}"
  echo "((!(ActionMatches{'Microsoft.Authorization/roleAssignments/write'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ids//,/, }} AND @Request[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase $types)) AND ((!(ActionMatches{'Microsoft.Authorization/roleAssignments/delete'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${ids//,/, }} AND @Resource[Microsoft.Authorization/roleAssignments:PrincipalType] ForAnyOfAnyValues:StringEqualsIgnoreCase $types))"
}

provisioning() {
  local mode="$1" scope="${2:-}" roles="${3:-$READER_ROLE_ID}" name mi graph perm role assignment swa rg_name rg_sub rg_loc rg_new
  name=$(worker_name); [[ -n "$name" ]] || { echo "No worker found."; return 1; }
  mi=$(az functionapp identity show -g "$RG_DEFAULT" -n "$name" --query principalId -o tsv)
  swa=$(az staticwebapp list -g "$RG_DEFAULT" --query "[0].name" -o tsv)
  graph=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
  if [[ "$mode" == "off" ]]; then
    # Switching off is always safe: the agent goes back to read-only behaviour (the teams do the work).
    az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" --settings AGENT_PROVISIONING=false AGENT_ROLE_ASSIGNMENT=false -o none
    az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" --setting-names AGENT_PROVISIONING=false AGENT_ROLE_ASSIGNMENT=false -o none
    echo "Provisioning switched off on $name and $swa."
    for perm in "${GRAPH_PROVISION_ROLES[@]}"; do
      role=$(graph_role_id "$perm")
      assignment=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
        --query "value[?appRoleId=='$role'].id | [0]" -o tsv)
      [[ -n "$assignment" ]] || { echo "Not held: $perm"; continue; }
      if [[ -z "${APPROVED_BY:-}" ]]; then echo "Would remove: $perm (dry run; pass --approved-by after explicit approval)"
      else az rest --method DELETE --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments/$assignment" -o none; echo "Removed: $perm"; fi
    done
    az role assignment list --assignee "$mi" --role "$RBAC_ADMIN_ROLE_ID" --all --query "[].{scope:scope, id:id}" -o tsv | while read -r at id; do
      if [[ -z "${APPROVED_BY:-}" ]]; then echo "Would remove: Role Based Access Control Administrator at $at (dry run)"
      else az role assignment delete --ids "$id" -o none; echo "Removed: Role Based Access Control Administrator at $at"; fi
    done
    return 0
  fi

  [[ "$scope" =~ ^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[A-Za-z0-9._()-]+$ ]] \
    || { echo "Scope must be a resource group: /subscriptions/<id>/resourceGroups/<name>"; return 1; }
  if [[ -n "${FROM_SHEET:-}" ]]; then
    local cosmos sheet_ids current
    cosmos=$(az cosmosdb list -g "$RG_DEFAULT" --query "[0].name" -o tsv)
    sheet_ids=$(COSMOS_CONNECTION_STRING="$(az cosmosdb keys list -g "$RG_DEFAULT" -n "$cosmos" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)" \
      "$("$ROOT/tools/cosmos_python.sh")" "$ROOT/tools/sheet_roles.py" "$FROM_SHEET" "$scope") \
      || { echo "No Azure role on the $FROM_SHEET mapping sheet that the agent may assign at $scope."; return 1; }
    current=$(worker_setting "$name" ROLE_ASSIGN_ALLOWED_ROLES)
    # Additive: the roles already approved stay, the sheet's roles are added.
    roles=$(printf '%s\n' ${current//,/ } ${sheet_ids//,/ } | tr '[:upper:]' '[:lower:]' | sort -u | paste -sd, -)
    echo "Allow-list from the $FROM_SHEET mapping sheet plus the roles already allowed: $roles"
  fi
  check_role_ids "$roles" || return 1
  rg_name="${scope##*/}"; rg_sub=$(cut -d/ -f3 <<<"$scope")
  rg_loc=$(az group show -n "$RG_DEFAULT" --query location -o tsv)
  rg_new=false
  [[ "$(az group exists -n "$rg_name" --subscription "$rg_sub")" == "true" ]] || rg_new=true
  echo "Plan for $name (managed identity $mi), expected cost 0:"
  if $rg_new; then echo "  Resource group: create $rg_name in $rg_loc, empty (free), tags pilot=pmo-review purpose=sample-access"; fi
  for perm in "${GRAPH_PROVISION_ROLES[@]}"; do echo "  Graph application permission: $perm"; done
  echo "  Azure: Role Based Access Control Administrator at $scope, condition: only roles {$roles}, only User/Group principals"
  local held_cond; held_cond=$(az role assignment list --assignee "$mi" --role "$RBAC_ADMIN_ROLE_ID" --scope "$scope" --query "[0].condition" -o tsv 2>/dev/null || true)
  if [[ -n "$held_cond" && "$held_cond" != "$(rbac_condition "$roles")" ]]; then
    echo "  (replaces the current condition at $scope, which allows other roles)"
  fi
  echo "  Worker settings: AGENT_PROVISIONING=true AGENT_ROLE_ASSIGNMENT=true ROLE_ASSIGN_ALLOWED_ROLES=$roles ROLE_ASSIGN_SCOPES=$scope"
  echo "  SWA settings: AGENT_PROVISIONING=true AGENT_ROLE_ASSIGNMENT=true"
  if [[ -z "${APPROVED_BY:-}" ]]; then
    echo "Dry run: nothing changed. After an explicit approval re-run with --approved-by \"<name> <date>\"."
    return 0
  fi
  if $rg_new; then
    az group create -n "$rg_name" -l "$rg_loc" --subscription "$rg_sub" --tags pilot=pmo-review purpose=sample-access -o none
    echo "Created resource group $rg_name (approved by $APPROVED_BY)"
  fi
  for perm in "${GRAPH_PROVISION_ROLES[@]}"; do
    role=$(graph_role_id "$perm")
    [[ -n "$role" ]] || { echo "Could not find $perm on Microsoft Graph."; return 1; }
    if [[ "$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" --query "length(value[?appRoleId=='$role'])" -o tsv)" == "0" ]]; then
      az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
        --headers "Content-Type=application/json" --body "{\"principalId\":\"$mi\",\"resourceId\":\"$graph\",\"appRoleId\":\"$role\"}" -o none
      echo "Granted $perm (approved by $APPROVED_BY)"
    else
      echo "Already granted: $perm"
    fi
  done
  local existing_id existing_cond
  # Two reads: `-o tsv` prints each element of a list on its own line, so "[0].[id, condition]" would mix them.
  existing_id=$(az role assignment list --assignee "$mi" --role "$RBAC_ADMIN_ROLE_ID" --scope "$scope" --query "[0].id" -o tsv)
  existing_cond=$(az role assignment list --assignee "$mi" --role "$RBAC_ADMIN_ROLE_ID" --scope "$scope" --query "[0].condition" -o tsv)
  if [[ -n "$existing_id" && "$existing_cond" != "$(rbac_condition "$roles")" ]]; then
    # Another role list (e.g. Reader -> Reader + Storage Blob Data Reader for a response document's roles): replace it.
    az role assignment delete --ids "$existing_id" -o none
    echo "Removed the old condition at $scope (other role list)"
    existing_id=""
  fi
  if [[ -z "$existing_id" ]]; then
    if ! az role assignment create --assignee-object-id "$mi" --assignee-principal-type ServicePrincipal --role "$RBAC_ADMIN_ROLE_ID" \
      --scope "$scope" --condition "$(rbac_condition "$roles")" --condition-version 2.0 -o none; then
      echo "FAILED to assign Role Based Access Control Administrator at $scope: the agent has no role-assignment right there now"
      echo "(its role step waits, then goes to the role team). Re-run this command; paste the error if it fails again."
      return 1
    fi
    echo "Assigned Role Based Access Control Administrator (constrained to roles {$roles}) at $scope"
  else
    echo "Already assigned: Role Based Access Control Administrator at $scope with the same roles"
  fi
  az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" -o none --settings AGENT_PROVISIONING=true AGENT_ROLE_ASSIGNMENT=true \
    "ROLE_ASSIGN_ALLOWED_ROLES=$roles" "ROLE_ASSIGN_SCOPES=$scope"
  az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" --setting-names AGENT_PROVISIONING=true AGENT_ROLE_ASSIGNMENT=true -o none
  echo "Provisioning on. Graph permissions now held by $name:"
  list_graph_roles "$mi"
  echo "Note: new Graph permissions can take up to ~24 h to reach the worker's managed identity token (VERIFIED_FACTS F61)."
  echo "Until then the agent's writes are refused and it retries every run; the request page says it is waiting for the permission."
}

# Agent TAP (requested by Gokul 22 Oct 2026, approval-gated): least-privileged Graph TAP permission (VERIFIED_FACTS F66).
# Tenant-wide by nature: the identity could create a TAP for any user; the code does it only for a user it created
# itself in the same request, after both approvals, and mails it only to that request's Enterprise ID mail.
GRAPH_TAP_ROLE=UserAuthMethod-TAP.ReadWrite.All

tap_policy() {
  az rest --method GET --url "https://graph.microsoft.com/v1.0/policies/authenticationMethodsPolicy/authenticationMethodConfigurations/TemporaryAccessPass" \
    --query "{state:state, defaultLifetime:defaultLifetimeInMinutes, maxLifetime:maxLifetimeInMinutes, oneTimeOnly:isUsableOnce, length:defaultLength, forGroups:includeTargets[].id}" -o json 2>/dev/null || true
}

agent_tap() {
  local mode="$1" lifetime="${2:-480}" name mi swa graph role assignment policy max sender prov
  name=$(worker_name); [[ -n "$name" ]] || { echo "No worker found."; return 1; }
  mi=$(az functionapp identity show -g "$RG_DEFAULT" -n "$name" --query principalId -o tsv)
  swa=$(az staticwebapp list -g "$RG_DEFAULT" --query "[0].name" -o tsv)
  role=$(graph_role_id "$GRAPH_TAP_ROLE")
  [[ -n "$role" ]] || { echo "Could not find $GRAPH_TAP_ROLE on Microsoft Graph."; return 1; }
  assignment=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" --query "value[?appRoleId=='$role'].id | [0]" -o tsv)
  if [[ "$mode" == "off" ]]; then
    az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" --settings AGENT_TAP=false -o none
    az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" --setting-names AGENT_TAP=false -o none
    echo "Agent TAP switched off on $name and $swa: the ID team issues every Temporary Access Pass again."
    [[ -n "$assignment" ]] || { echo "Not held: $GRAPH_TAP_ROLE"; return 0; }
    if [[ -z "${APPROVED_BY:-}" ]]; then echo "Would remove: $GRAPH_TAP_ROLE (dry run; pass --approved-by after explicit approval)"
    else az rest --method DELETE --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments/$assignment" -o none; echo "Removed: $GRAPH_TAP_ROLE"; fi
    return 0
  fi

  [[ "$lifetime" =~ ^[0-9]+$ ]] || { echo "--lifetime must be minutes"; return 1; }
  prov=$(worker_setting "$name" AGENT_PROVISIONING); sender=$(worker_setting "$name" NOTIFY_SENDER)
  echo "Checks:"
  echo "  Agent provisioning: ${prov:-false} (must be true: the agent mails passes only for accounts it creates)"
  echo "  Mail sender: ${sender:-Outbox} (must be AcsEmail or GraphMail: with Outbox the agent never issues a pass)"
  echo "  Test redirect: $(worker_setting "$name" NOTIFY_REDIRECT_TO || true) (empty = mail goes to the joiner's Enterprise ID mail)"
  policy=$(tap_policy)
  if [[ -n "$policy" ]]; then
    echo "  Tenant TAP policy: $(tr -d '\n ' <<<"$policy")"
    max=$(python3 -c "import json,sys;print(json.loads(sys.argv[1]).get('maxLifetime') or '')" "$policy")
    [[ "$(python3 -c "import json,sys;print(json.loads(sys.argv[1]).get('state'))" "$policy")" == "enabled" ]] \
      || echo "  WARNING: the TAP method is not enabled in the tenant: Entra refuses the pass and the ID team gets the task."
    if [[ -n "$max" && "$lifetime" -gt "$max" ]]; then echo "  Lifetime $lifetime > policy maximum $max: using $max."; lifetime=$max; fi
  else
    echo "  Tenant TAP policy: could not read it (needs Policy.Read.All for you). Check Entra > Authentication methods > Temporary Access Pass."
  fi
  [[ "$lifetime" -ge 60 ]] || { echo "Lifetime must be at least 60 minutes (the joiner reads it from mail)."; return 1; }
  echo "Plan for $name (managed identity $mi), expected cost 0:"
  echo "  Graph application permission: $GRAPH_TAP_ROLE ${assignment:+(already held)}"
  echo "  Worker settings: AGENT_TAP=true AGENT_TAP_LIFETIME_MINUTES=$lifetime; SWA: AGENT_TAP=true"
  echo "  Risk: the permission is tenant-wide (any user's TAP). The code issues one only for a user the agent created in the same"
  echo "  request after both approvals, mails it once to that request's Enterprise ID mail and keeps no copy."
  if [[ -z "${APPROVED_BY:-}" ]]; then
    echo "Dry run: nothing changed. After an explicit approval re-run with --approved-by \"<name> <date>\"."
    return 0
  fi
  [[ "$prov" == "true" ]] || { echo "Switch agent provisioning on first (tools/deploy_free.sh provisioning ...)."; return 1; }
  [[ "$sender" == "AcsEmail" || "$sender" == "GraphMail" ]] || { echo "Switch mail on first (tools/deploy_free.sh email ...)."; return 1; }
  graph=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
  if [[ -z "$assignment" ]]; then
    az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
      --headers "Content-Type=application/json" --body "{\"principalId\":\"$mi\",\"resourceId\":\"$graph\",\"appRoleId\":\"$role\"}" -o none
    echo "Granted $GRAPH_TAP_ROLE (approved by $APPROVED_BY)"
  else
    echo "Already granted: $GRAPH_TAP_ROLE"
  fi
  az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" -o none --settings AGENT_TAP=true "AGENT_TAP_LIFETIME_MINUTES=$lifetime"
  az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" --setting-names AGENT_TAP=true -o none
  echo "Agent TAP on. Note: a new Graph permission can take up to ~24 h to reach the worker's token (F61). Until then the agent"
  echo "waits before creating an account (so the password and the pass go out together); the request page says so."
}

# Agent privileged rows (requested by Gokul 22 Oct 2026, approval-gated): a setting only. What the agent may assign stays
# the allow-list + the RBAC condition (provisioning ... on); privileged administrator roles are refused in code and here.
agent_privileged() {
  local mode="$1" name swa prov roles id rname
  name=$(worker_name); [[ -n "$name" ]] || { echo "No worker found."; return 1; }
  swa=$(az staticwebapp list -g "$RG_DEFAULT" --query "[0].name" -o tsv)
  if [[ "$mode" == "off" ]]; then
    az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" --settings AGENT_PRIVILEGED=false -o none
    az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" --setting-names AGENT_PRIVILEGED=false -o none
    echo "Agent privileged rows switched off on $name and $swa: rows flagged privileged go to the ID and role assignment teams again."
    return 0
  fi
  [[ "$mode" == "on" ]] || { echo "privileged on|off"; return 1; }
  prov=$(worker_setting "$name" AGENT_PROVISIONING); roles=$(worker_setting "$name" ROLE_ASSIGN_ALLOWED_ROLES)
  echo "Checks:"
  echo "  Agent provisioning: ${prov:-false} (must be true)"
  echo "  Roles the agent may assign (ROLE_ASSIGN_ALLOWED_ROLES, same as the RBAC condition) at $(worker_setting "$name" ROLE_ASSIGN_SCOPES):"
  for id in ${roles//,/ }; do
    rname=$(az role definition list --name "$id" --query "[0].roleName" -o tsv 2>/dev/null || true)
    echo "    $id ${rname:-(name not found)}"
  done
  echo "  A privileged row whose roles are not all in that list: the agent creates the user, group and membership; the"
  echo "  role assignment team assigns its roles. To let the agent assign them too, add them with:"
  echo "    tools/deploy_free.sh provisioning <subscriptionId> on <scope> --from-sheet <PROJECT>   (approval; reads the roles from the mapping sheet)"
  echo "Plan for $name and $swa, expected cost 0, no permission change:"
  echo "  Settings: AGENT_PRIVILEGED=true (worker + SWA)"
  echo "  Effect: after the Owner and then Security approved, the agent also provisions rows flagged privileged (same checks,"
  echo "  same allow-list). Never: Owner, Contributor, User Access Administrator, Role Based Access Control Administrator,"
  echo "  Reservations Administrator, Key Vault Data Access Administrator (always people)."
  echo "  Open tasks the agent left to a team only because the row was privileged, and that nobody has picked up, go back to the agent."
  if [[ -z "${APPROVED_BY:-}" ]]; then
    echo "Dry run: nothing changed. After an explicit approval re-run with --approved-by \"<name> <date>\"."
    return 0
  fi
  [[ "$prov" == "true" ]] || { echo "Switch agent provisioning on first (tools/deploy_free.sh provisioning ...)."; return 1; }
  az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" --settings AGENT_PRIVILEGED=true -o none
  az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" --setting-names AGENT_PRIVILEGED=true -o none
  echo "Agent privileged rows on (approved by $APPROVED_BY). Takes effect on the next agent run (every 5 minutes)."
}

# Per-environment settings (no resource or permission change). A test environment may send every mail to one address.
set_environment() {
  local env="$1" redirect="${2:-}" name swa tenant
  [[ "$env" =~ ^(pilot|dev|test|prod)$ ]] || { echo "Environment must be pilot, dev, test or prod."; return 1; }
  if [[ "$env" == "prod" && -n "$redirect" ]]; then echo "Refusing: production mails the real recipients (no --redirect)."; return 1; fi
  if [[ -n "$redirect" && ! "$redirect" =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]]; then echo "--redirect must be one mail address."; return 1; fi
  name=$(worker_name); swa=$(az staticwebapp list -g "$RG_DEFAULT" --query "[0].name" -o tsv)
  tenant=$(az account show --query tenantId -o tsv)
  [[ -n "$name" ]] && az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" -o none \
    --settings "APP_ENVIRONMENT=$env" "NOTIFY_REDIRECT_TO=$redirect" "AGENT_TENANT_ID=$tenant"
  az staticwebapp appsettings set -n "$swa" -g "$RG_DEFAULT" -o none \
    --setting-names "APP_ENVIRONMENT=$env" "NOTIFY_REDIRECT_TO=$redirect" "AGENT_TENANT_ID=$tenant"
  echo "Environment $env on ${name:-no worker} and $swa; mail redirect: ${redirect:-none (real recipients)}; agent tenant $tenant."
}

policy_allows_email() {
  az policy assignment show -g "$RG_DEFAULT" -n pmo-pilot-allowed-types \
    --query "parameters.listOfResourceTypesAllowed.value" -o tsv 2>/dev/null | grep -qx "Microsoft.Communication/communicationServices"
}

# Mail through Azure Communication Services Email (VERIFIED_FACTS F64). New resources: approval-gated, dry run first.
email_setup() {
  local redirect="${1:-}" name mi role_id role_name out endpoint from env
  name=$(worker_name); [[ -n "$name" ]] || { echo "No worker found."; return 1; }
  mi=$(az functionapp identity show -g "$RG_DEFAULT" -n "$name" --query principalId -o tsv)
  # Prefer a mail-specific built-in role when the tenant lists it; otherwise Contributor on that one resource only.
  role_id=$(az role definition list --name "Communication and Email Service Owner" --query "[0].name" -o tsv 2>/dev/null || true)
  role_name="Communication and Email Service Owner"
  [[ -n "$role_id" ]] || { role_id=b24988ac-6180-42a0-ab88-20f7382dd24c; role_name="Contributor (on the Communication Services resource only)"; }
  env=$(worker_setting "$name" APP_ENVIRONMENT); env=${env:-pilot}
  [[ -f "$ROOT/.local/params.json" ]] || { echo "Run tools/deploy_free.sh params <subscriptionId> first."; return 1; }
  python3 - "$ROOT/.local/params.json" "$ROOT/.local/params-email.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1])); d["parameters"]["enableEmail"] = {"value": True}
json.dump(d, open(sys.argv[2], "w"), indent=2)
PY
  echo "Plan (mail), expected cost: pay per mail from Azure credits (illustrative USD 0.00025 per mail + 0.00012 per MB, F64; ~₹0 at pilot volume):"
  echo "  Policy (stage A): allow Microsoft.Communication/communicationServices, emailServices, emailServices/domains"
  echo "  Create: Email Communication Service ecs-pmo-<suffix> (global, data location India) + domain AzureManagedDomain (DoNotReply@<guid>.azurecomm.net)"
  echo "  Create: Communication Services acs-pmo-<suffix> (global, data location India), linked to that domain"
  echo "  Role: $role_name for the worker identity $mi at the Communication Services resource scope only"
  echo "  Worker settings: NOTIFY_SENDER=AcsEmail ACS_ENDPOINT=<endpoint> NOTIFY_FROM=DoNotReply@<domain> APP_ENVIRONMENT=$env NOTIFY_REDIRECT_TO=${redirect:-<unchanged>}"
  if [[ -z "${APPROVED_BY:-}" ]]; then
    echo "== what-if (policy change) =="
    az deployment sub what-if --name "$DEPLOYMENT" --location centralindia --template-file "$ROOT/infra/main.bicep" \
      --parameters "@$ROOT/.local/params-email.json" --result-format ResourceIdOnly | tee "$ROOT/.local/whatif-email.txt" || true
    echo "== what-if (mail resources) =="
    az deployment group what-if -g "$RG_DEFAULT" --template-file "$ROOT/infra/modules/email.bicep" \
      --parameters "workerPrincipalId=$mi" "senderRoleDefinitionId=$role_id" "tags={\"workload\":\"pmo-review-pilot\"}" \
      --result-format ResourceIdOnly | tee -a "$ROOT/.local/whatif-email.txt" || true
    echo "Dry run: nothing changed. After an explicit approval re-run with --approved-by \"<name> <date>\"."
    return 0
  fi
  if ! policy_allows_email; then
    az deployment sub create --name "$DEPLOYMENT" --location centralindia --template-file "$ROOT/infra/main.bicep" --parameters "@$ROOT/.local/params-email.json" -o none
    echo "Policy updated (approved by $APPROVED_BY). Waiting for it to take effect (up to 30 minutes)..."
    local i; for i in $(seq 1 30); do policy_allows_email && break; sleep 60; done
    sleep 120 # the assignment reads as updated before the deny evaluation follows
  fi
  out=$(az deployment group create -g "$RG_DEFAULT" -n pmo-review-email --template-file "$ROOT/infra/modules/email.bicep" \
    --parameters "workerPrincipalId=$mi" "senderRoleDefinitionId=$role_id" "tags={\"workload\":\"pmo-review-pilot\"}" --query properties.outputs -o json)
  endpoint=$(python3 -c "import json,sys;print(json.load(sys.stdin)['acsEndpoint']['value'])" <<<"$out")
  from=$(python3 -c "import json,sys;print(json.load(sys.stdin)['senderAddress']['value'])" <<<"$out")
  local settings=("NOTIFY_SENDER=AcsEmail" "ACS_ENDPOINT=$endpoint" "NOTIFY_FROM=$from")
  [[ -n "$redirect" ]] && settings+=("NOTIFY_REDIRECT_TO=$redirect")
  az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" -o none --settings "${settings[@]}"
  cp "$ROOT/.local/params-email.json" "$ROOT/.local/params.json"
  echo "Mail on (approved by $APPROVED_BY): $endpoint from $from. The worker mails pending notifications every 5 minutes."
  echo "New role assignments can take a few minutes; a refused send is retried 3 times, then listed under Issues and reports."
  echo "Then run: tools/deploy_free.sh app <subscriptionId> (the website shows 'mailed')."
}

# A main.bicep deployment replaces the worker's whole app-settings list (siteConfig.appSettings), and what-if cannot show
# app settings. Settings set later with the CLI and not in worker.bicep would be dropped: list them, snapshot, restore.
template_setting_names() { grep -oE "\{ name: '[A-Za-z0-9_]+', value:" "$ROOT/infra/modules/worker.bicep" | sed -E "s/\{ name: '([^']+)'.*/\1/" | sort -u; }
settings_outside_template() { # stdin: live app settings JSON; prints the names the template does not set
  python3 -c "import json,sys;t=set(open(sys.argv[1]).read().split());print(' '.join(sorted(s['name'] for s in json.load(sys.stdin) if s['name'] not in t)))" <(template_setting_names)
}
# Re-apply what a main.bicep deployment dropped or changed on the worker (not the retired switches the template forces off).
restore_worker_settings() {
  local name="$1" before="$2" after line restore=()
  after=$(az functionapp config appsettings list -g "$RG_DEFAULT" -n "$name" -o json)
  while IFS= read -r line; do [[ -n "$line" ]] && restore+=("$line"); done < <(python3 -c "
import json, sys
before, after = json.loads(sys.argv[1]), {s['name']: s.get('value') for s in json.loads(sys.argv[2])}
retired = {'ENTRA_AUTO_ASSIGN', 'ENTRA_PROVISION_JOINERS', 'ENTRA_AUTO_CREATE_PACKAGES'}
for s in before:
    if s['name'] not in retired and after.get(s['name']) != s.get('value'): print(f\"{s['name']}={s.get('value') or ''}\")" "$before" "$after")
  if [[ ${#restore[@]} -eq 0 ]]; then echo "Worker app settings unchanged by the deployment."; return 0; fi
  az functionapp config appsettings set -g "$RG_DEFAULT" -n "$name" -o none --settings "${restore[@]}"
  echo "Worker app settings restored to their values before the deployment: $(printf '%s\n' "${restore[@]}" | cut -d= -f1 | tr '\n' ' ')"
}

policy_allows_databricks() {
  az policy assignment show -g "$RG_DEFAULT" -n pmo-pilot-allowed-types \
    --query "parameters.listOfResourceTypesAllowed.value" -o tsv 2>/dev/null | grep -qx "Microsoft.Databricks/workspaces"
}

# One serverless Azure Databricks workspace (VERIFIED_FACTS F71-F76). New resource: approval-gated, dry run first.
databricks_setup() {
  local location="${1:-centralindia}" provider existing out
  [[ -f "$ROOT/.local/params.json" ]] || { echo "Run tools/deploy_free.sh params <subscriptionId> first."; return 1; }
  python3 - "$ROOT/.local/params.json" "$ROOT/.local/params-databricks.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1])); d["parameters"]["enableDatabricks"] = {"value": True}
json.dump(d, open(sys.argv[2], "w"), indent=2)
PY
  provider=$(az provider show -n Microsoft.Databricks --query registrationState -o tsv 2>/dev/null || echo NotRegistered)
  existing=$(az resource list -g "$RG_DEFAULT" --resource-type Microsoft.Databricks/workspaces --query "[].name" -o tsv)
  if [[ -n "$existing" ]]; then echo "A workspace already exists in $RG_DEFAULT: $existing (one only, CLAUDE.md rule 1)."; fi
  local tags="{\"workload\":\"pmo-review-pilot\",\"costProfile\":\"databricks-serverless-pay-per-dbu\"}"
  echo "Plan (Databricks), expected cost: nothing while idle; DBUs (Premium, serverless) only while a notebook, job or SQL"
  echo "warehouse runs, billed on the workspace resource inside the ₹2,000 budget (F76). Stop compute after each test."
  echo "  Provider: Microsoft.Databricks is $provider on the subscription$([[ "$provider" == Registered ]] || echo " (will be registered, free)")"
  echo "  Policy (stage A): allow Microsoft.Databricks/workspaces in $RG_DEFAULT"
  echo "  Create: Azure Databricks workspace dbw-pmo-<suffix> ($location, Premium, computeMode Serverless, public network access)"
  echo "  Not created: managed resource group, VNet, NAT gateway, public IP, storage account, access connector (serverless, F71)"
  echo "  Then: python3 tools/databricks_setup.py --account-id <id> --group <Entra group>... (dry run, then --approved-by)"
  local worker before=""
  worker=$(worker_name)
  if [[ -n "$worker" ]]; then
    before=$(az functionapp config appsettings list -g "$RG_DEFAULT" -n "$worker" -o json)
    echo "  Worker app settings outside the template (the policy deployment drops them; the approved run restores them): $(settings_outside_template <<<"$before" | sed 's/^$/none/')"
  fi
  if [[ -z "${APPROVED_BY:-}" ]]; then
    # Only real changes: unchanged resources (NoChange) and the mail resources deployed outside main.bicep (Ignore) are left out.
    echo "== what-if (policy change; expect 1 to modify: pmo-pilot-allowed-types) =="
    az deployment sub what-if --name "$DEPLOYMENT" --location centralindia --template-file "$ROOT/infra/main.bicep" \
      --parameters "@$ROOT/.local/params-databricks.json" --result-format FullResourcePayloads --exclude-change-types NoChange Ignore \
      | tee "$ROOT/.local/whatif-databricks.txt" || true
    echo "== workspace =="
    if policy_allows_databricks; then
      az deployment group what-if -g "$RG_DEFAULT" --template-file "$ROOT/infra/modules/databricks.bicep" \
        --parameters "location=$location" "tags=$tags" --result-format FullResourcePayloads --exclude-change-types Ignore \
        | tee -a "$ROOT/.local/whatif-databricks.txt" || true
    else
      # Azure checks a new resource against the CURRENT policy: before stage A its what-if can only say RequestDisallowedByPolicy.
      echo "What-if not possible yet: the live policy does not allow Microsoft.Databricks/workspaces (that is stage A)."
      echo "The approved run re-checks it after the policy change and creates only if it is exactly this one workspace:"
      az bicep build --file "$ROOT/infra/modules/databricks.bicep" --stdout 2>/dev/null | python3 -c "
import json, sys
r = json.load(sys.stdin)['resources']; r = r if isinstance(r, list) else list(r.values())
for x in r: print('  +', x['type'], x['apiVersion'], json.dumps({'sku': x.get('sku'), 'properties': x.get('properties')}))
print('  resources in the template:', len(r))" | tee -a "$ROOT/.local/whatif-databricks.txt" || echo "  (az bicep build not available: see infra/modules/databricks.bicep)"
    fi
    echo "Dry run: nothing changed. After an explicit approval re-run with --approved-by \"<name> <date>\"."
    return 0
  fi
  if [[ "$provider" != "Registered" ]]; then
    az provider register -n Microsoft.Databricks --wait -o none
    echo "Provider Microsoft.Databricks registered (approved by $APPROVED_BY)."
  fi
  if ! policy_allows_databricks; then
    az deployment sub create --name "$DEPLOYMENT" --location centralindia --template-file "$ROOT/infra/main.bicep" --parameters "@$ROOT/.local/params-databricks.json" -o none
    [[ -n "$worker" ]] && restore_worker_settings "$worker" "$before"
    echo "Policy updated (approved by $APPROVED_BY). Waiting for it to take effect (up to 30 minutes)..."
    local i; for i in $(seq 1 30); do policy_allows_databricks && break; sleep 60; done
    sleep 120 # the assignment reads as updated before the deny evaluation follows
  fi
  cp "$ROOT/.local/params-databricks.json" "$ROOT/.local/params.json" # later deployments keep the type allowed
  # The workspace what-if now runs against the updated policy: create only if it changes exactly the one workspace.
  az deployment group what-if -g "$RG_DEFAULT" --template-file "$ROOT/infra/modules/databricks.bicep" \
    --parameters "location=$location" "tags=$tags" --no-pretty-print -o json > "$ROOT/.local/whatif-databricks-workspace.json" \
    || { echo "STOP: workspace what-if failed (policy not effective yet?). Re-run the same command in a few minutes."; return 1; }
  python3 - "$ROOT/.local/whatif-databricks-workspace.json" <<'PY' || { echo "STOP: nothing created. Send .local/whatif-databricks-workspace.json for review."; return 1; }
import json, sys
changes = [c for c in json.load(open(sys.argv[1])).get("changes") or [] if c.get("changeType") not in ("Ignore", "NoChange")]
for c in changes: print(f"  what-if: {c['changeType']} {c['resourceId']}")
ok = len(changes) <= 1 and all(c["changeType"] in ("Create", "Modify") and "/providers/Microsoft.Databricks/workspaces/" in c["resourceId"] for c in changes)
sys.exit(0 if ok else 1)
PY
  out=$(az deployment group create -g "$RG_DEFAULT" -n pmo-review-databricks --template-file "$ROOT/infra/modules/databricks.bicep" \
    --parameters "location=$location" "tags=$tags" --query properties.outputs -o json)
  echo "$out" > "$ROOT/.local/databricks.json"
  python3 -c "import json,sys;o=json.load(sys.stdin);print('Databricks workspace on (approved by '+sys.argv[1]+'):',o['workspaceName']['value'],'https://'+o['workspaceUrl']['value'],'(workspace id',str(o['workspaceId']['value'])+')')" "$APPROVED_BY" <<<"$out"
  echo "Next: open the workspace URL once as the deploying admin (becomes workspace admin), then sign in to"
  echo "https://accounts.azuredatabricks.net as an Entra Global Administrator (first account admin) and copy the account id."
  echo "Then: python3 tools/databricks_setup.py --account-id <id> --group <Entra group> (dry run) → … --approved-by \"<name> <date>\""
}

# API + SPA build and SWA upload. The API runtime comes from staticwebapp.config.json (single source of truth;
# InfraConsistencyTests checks it matches the API project's TargetFramework).
deploy_app() {
  local rg="$1" swa="$2" runtime api_lang api_ver cli_lang token
  runtime=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['platform']['apiRuntime'])" "$ROOT/staticwebapp.config.json")
  api_lang="${runtime%%:*}"; api_ver="${runtime##*:}"
  # The config file spells it "dotnet-isolated" (CLI schema); the deploy client only accepts
  # "dotnetisolated" (no dash) for --api-language, else "Function language info isn't provided" (F21).
  cli_lang="${api_lang//-/}"
  echo "API runtime: $api_lang $api_ver (CLI flag: $cli_lang) -> $swa"
  rm -rf "$ROOT/.local/api"
  dotnet publish "$ROOT/src/api/PmoReview.Api" -c Release -o "$ROOT/.local/api"
  npm --prefix "$ROOT/src/web" ci
  npm --prefix "$ROOT/src/web" run build
  # Ship the config with the app (routes, auth, headers, platform.apiRuntime). signed-out.html comes from public/.
  cp "$ROOT/staticwebapp.config.json" "$ROOT/src/web/dist/staticwebapp.config.json"
  token=$(az staticwebapp secrets list -n "$swa" -g "$rg" --query properties.apiKey -o tsv)
  (cd "$ROOT/src/web" && SWA_CLI_DEPLOYMENT_TOKEN="$token" npx swa deploy dist \
    --api-location "$ROOT/.local/api" --api-language "$cli_lang" --api-version "$api_ver" \
    --swa-config-location "$ROOT/src/web/dist" --env production)
}

# v2 flow app settings on the SWA (Phase 2b/2c hand-off settings are retired and removed).
app_settings_v2() {
  local rg="$1" swa="$2"
  local w p r
  w=$(worker_name)
  p=$([[ -n "$w" ]] && az functionapp config appsettings list -g "$RG_DEFAULT" -n "$w" --query "[?name=='AGENT_PROVISIONING'].value | [0]" -o tsv || true)
  r=$([[ -n "$w" ]] && az functionapp config appsettings list -g "$RG_DEFAULT" -n "$w" --query "[?name=='AGENT_ROLE_ASSIGNMENT'].value | [0]" -o tsv || true)
  # The website shows the same environment, mail mode and tenant as the worker that sends and provisions.
  local env sender redirect tenant tap priv
  env=$([[ -n "$w" ]] && worker_setting "$w" APP_ENVIRONMENT || true)
  tap=$([[ -n "$w" ]] && worker_setting "$w" AGENT_TAP || true)
  priv=$([[ -n "$w" ]] && worker_setting "$w" AGENT_PRIVILEGED || true)
  sender=$([[ -n "$w" ]] && worker_setting "$w" NOTIFY_SENDER || true)
  redirect=$([[ -n "$w" ]] && worker_setting "$w" NOTIFY_REDIRECT_TO || true)
  tenant=$([[ -n "$w" ]] && worker_setting "$w" AGENT_TENANT_ID || true)
  az staticwebapp appsettings set -n "$swa" -g "$rg" --setting-names "TAP_MAX_LIFETIME_MINUTES=60" \
    "APP_URL=https://$(az staticwebapp show -n "$swa" -g "$rg" --query defaultHostname -o tsv)" \
    "AGENT_PROVISIONING=${p:-false}" "AGENT_ROLE_ASSIGNMENT=${r:-false}" "AGENT_TAP=${tap:-false}" "AGENT_PRIVILEGED=${priv:-false}" \
    "APP_ENVIRONMENT=${env:-pilot}" "NOTIFY_SENDER=${sender:-Outbox}" "NOTIFY_REDIRECT_TO=${redirect:-}" "AGENT_TENANT_ID=${tenant:-$(az account show --query tenantId -o tsv)}" -o none
  az staticwebapp appsettings delete -n "$swa" -g "$rg" \
    --setting-names ENTRA_HANDOFF_ENABLED ENTRA_AUTO_ASSIGN REQUIRE_SEPARATE_REVIEWER ENTRA_AUTO_CREATE_PACKAGES -o none 2>/dev/null || true
}

[[ "$CMD" == "apply" || "$CMD" == "app" || "$CMD" == "worker" ]] && ensure_dotnet10

case "$CMD" in
  whatif)
    "$ROOT/tools/check_free_tier.sh" "$SUB"
    if worker_enabled && ! policy_allows_worker; then
      echo "STOP: deployWorker=true but the live policy does not allow the worker types yet."
      echo "Run stage A first (deployWorker=false), apply it, wait ~30 minutes, then run stage B."
      exit 2
    fi
    az deployment sub what-if --name "$DEPLOYMENT" --location "$LOCATION" \
      --template-file "$ROOT/infra/main.bicep" --parameters "@$PARAMS" \
      --result-format FullResourcePayloads | tee "$ROOT/.local/whatif.txt"
    echo
    echo "Expected resources: resource group, policy assignment, budget, Cosmos DB account (free tier) + 1 database"
    echo "  + 13 containers (8 used by v2: projects, requests, reviewRuns, userAccess, mappingSheet, responseDocument,"
    echo "  notifications, issues; 5 legacy v1 kept untouched), Static Web App (Free)."
    echo "Expected cost: ₹0 while within free limits (SWA Free plan; Cosmos free tier 1000 RU/s + 25 GB; policy and budgets are free)."
    if handoff_enabled && ! worker_enabled; then
      echo "STAGE A (enableHandoff=true, deployWorker=false): policy allows the worker types, budget ₹2,000 with 50/80/100% alerts. Creates nothing paid."
    fi
    if worker_enabled; then
      echo "STAGE B / v2 worker (paid from credits): Flex Consumption plan + Function App (system identity) + storage account"
      echo "  (Standard_LRS, no shared keys) + Storage Blob Data Owner + Cosmos data roles scoped to requests, reviewRuns, mappingSheet,"
      echo "  responseDocument, notifications, issues (+ read on projects). App settings: WORKFLOW_SCHEDULE, NOTIFY_SENDER,"
      echo "  ROLE_READER, ADO_*, TAP_MAX_LIFETIME_MINUTES; retired ENTRA_AUTO_ASSIGN / ENTRA_PROVISION_JOINERS forced false."
      echo "  Expected cost: roughly ₹50-300/month (unchanged). After apply: grant-graph (read-only), then revoke-graph-writes after approval."
      WLOC=$(python3 -c "import json,sys;p=json.load(open(sys.argv[1]))['parameters'];print(p.get('workerLocation',p['location'])['value'])" "$PARAMS")
      echo "== Flex Consumption availability for $WLOC =="
      az functionapp list-flexconsumption-locations --query "[].name" -o tsv | tr -d ' ' | grep -ix "$WLOC" \
        || echo "WARNING: $WLOC is not listed for Flex Consumption. Pick another region for the worker before approving."
      az functionapp list-flexconsumption-runtimes -l "$WLOC" --runtime dotnet-isolated -o table || true
    fi
    echo "Send .local/whatif.txt to Gokul and wait for 'approved'."
    ;;
  apply)
    [[ "${4:-}" == "--approved-by" && -n "${5:-}" ]] || { echo "Refusing: pass --approved-by \"<name> <date>\" after explicit approval."; exit 2; }
    echo "Approval recorded: $5" | tee "$ROOT/.local/approval.txt"
    "$ROOT/tools/check_free_tier.sh" "$SUB"

    az deployment sub create --name "$DEPLOYMENT" --location "$LOCATION" \
      --template-file "$ROOT/infra/main.bicep" --parameters "@$PARAMS" -o none
    OUT=$(az deployment sub show --name "$DEPLOYMENT" --query properties.outputs -o json)
    RG=$(echo "$OUT" | python3 -c "import json,sys;print(json.load(sys.stdin)['resourceGroupId']['value'].split('/')[-1])")
    SWA=$(echo "$OUT" | python3 -c "import json,sys;print(json.load(sys.stdin)['staticWebAppName']['value'])")
    COSMOS=$(echo "$OUT" | python3 -c "import json,sys;print(json.load(sys.stdin)['cosmosAccountName']['value'])")
    echo "$OUT" > "$ROOT/.local/deployment-outputs.json"

    PYTHON=$("$ROOT/tools/cosmos_python.sh")
    # Secret handling: piped straight from Cosmos into SWA app settings. Never echoed, never on disk.
    CS=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
    az staticwebapp appsettings set -n "$SWA" -g "$RG" --setting-names \
      "COSMOS_CONNECTION_STRING=$CS" "COSMOS_DATABASE=pmoreview" "STORAGE_MODE=Cosmos" \
      "REVIEW_MODEL_PROVIDER=RuleOnly" "REVIEW_DISPATCHER=Inline" "ALLOW_PAID_SERVICES=false" \
      "ALLOWED_IDENTITY_PROVIDERS=aad" "PROCESSING_BUDGET_SECONDS=30" "STUCK_RUN_MINUTES=5" -o none
    app_settings_v2 "$RG" "$SWA"
    deploy_app "$RG" "$SWA"
    [[ "${SEED_SAMPLE:-0}" == "1" ]] && COSMOS_CONNECTION_STRING="$CS" "$PYTHON" "$ROOT/tools/seed_sample_data.py"
    unset CS
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    echo "$(git -C "$ROOT" rev-parse --short HEAD) $(date -u +%FT%TZ)" > "$ROOT/.local/last-app-deploy"
    echo "Next: import each tenant's workbooks; each user signs in once, reads userId from /.auth/me, then is registered:"
    echo "  COSMOS_CONNECTION_STRING=... python3 tools/import_workbook.py --tenant PEST --project PEST ... --register-user <userId>=Requester,Owner --apply"
    if worker_enabled; then
      deploy_worker || true
      grant_graph || echo "Graph read permissions NOT granted (needs Global Administrator). Run: tools/deploy_free.sh grant-graph $SUB"
    elif handoff_enabled; then
      echo "Stage A applied. Wait ~30 minutes for the policy change, set deployWorker=true, then run whatif again (stage B)."
    fi
    "$ROOT/tools/cost_check.sh" "$RG"
    ;;
  status)
    RG=$RG_DEFAULT
    ok() { if eval "$2" >/dev/null 2>&1; then echo "  [OK]   $1"; else echo "  [--]   $1"; fi; }
    echo "== Last deployment =="
    az deployment sub show --name "$DEPLOYMENT" --query "{state:properties.provisioningState, finished:properties.timestamp}" -o tsv 2>/dev/null || echo "  none"
    echo "== Stage A =="
    ok "policy allows the worker types" policy_allows_worker
    BUDGET=$(az consumption budget list --resource-group "$RG" --query "[?name=='pmo-pilot-zero-cost'].amount | [0]" -o tsv 2>/dev/null)
    echo "  budget amount: ${BUDGET:-not found} (stage A sets 2000)"
    COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
    for c in mappingSheet responseDocument notifications issues; do
      ok "Cosmos container '$c' exists (v2)" "az cosmosdb sql container show -g $RG -a $COSMOS -d pmoreview -n $c"
    done
    echo "== Databricks (optional, deploy_free.sh databricks) =="
    ok "policy allows Microsoft.Databricks/workspaces" policy_allows_databricks
    echo "  workspace: $(az resource list -g "$RG" --resource-type Microsoft.Databricks/workspaces --query "[0].name" -o tsv 2>/dev/null || true)"
    echo "== Worker =="
    W=$(worker_name); echo "  worker: ${W:-not deployed}"
    SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
    if [[ -n "$W" ]]; then
      for k in WORKFLOW_SCHEDULE NOTIFY_SENDER NOTIFY_FROM NOTIFY_REDIRECT_TO APP_ENVIRONMENT AGENT_TENANT_ID AGENT_PROVISIONING AGENT_ROLE_ASSIGNMENT AGENT_TAP AGENT_TAP_LIFETIME_MINUTES AGENT_PRIVILEGED ROLE_ASSIGN_ALLOWED_ROLES ROLE_READER ADO_ORG_URL ENTRA_AUTO_ASSIGN ENTRA_PROVISION_JOINERS; do
        V=$(az functionapp config appsettings list -g "$RG" -n "$W" --query "[?name=='$k'].value | [0]" -o tsv 2>/dev/null)
        echo "  $k: ${V:-not set}"
      done
    fi
    echo "== App =="
    HOST=$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)
    echo "  https://$HOST  (v2 code if 'New request' shows 'Enterprise ID' and the 7-step flow)"
    ;;
  params)
    # Rebuild .local/params.json after a Cloud Shell reset: live budget email/start date, stages A+B on (worker),
    # v2 defaults (notifications kept in the app, no ADO, no role reader). Edit the file to turn those on.
    B=$(az consumption budget show --budget-name pmo-pilot-zero-cost --resource-group "$RG_DEFAULT" -o json 2>/dev/null)
    HOST=$(az staticwebapp list -g "$RG_DEFAULT" --query "[0].defaultHostname" -o tsv 2>/dev/null)
    W=$(worker_name 2>/dev/null || true)
    LIVE=$([[ -n "$W" ]] && az functionapp config appsettings list -g "$RG_DEFAULT" -n "$W" -o json 2>/dev/null || echo "[]")
    DBX=$(policy_allows_databricks && echo true || echo false)
    python3 - "$B" "$ROOT/.local/params.json" "${HOST:-}" "$LIVE" "$DBX" <<'PY'
import json, sys
b, out, host = json.loads(sys.argv[1]), sys.argv[2], sys.argv[3]
live = {s["name"]: s.get("value") or "" for s in json.loads(sys.argv[4] or "[]")}
v = lambda x: {"value": x}
p = {"resourceGroupName": v("rg-pmo-review-pilot"), "location": v("centralindia"), "swaLocation": v("eastasia"),
     "budgetEmail": v(next(iter(b["notifications"].values()))["contactEmails"][0]), "budgetAmount": v(2000),
     "budgetStartDate": v(b["timePeriod"]["startDate"][:10]), "enableHandoff": v(True), "deployWorker": v(True),
     "workerLocation": v("centralindia"), "workerRuntimeVersion": v("10.0"),
     "roleReader": v("None"), "appUrl": v(f"https://{host}" if host else ""),
     # v2.1 provisioning as it is live (approved separately); off when never switched on.
     "agentProvisioning": v(live.get("AGENT_PROVISIONING", "").lower() == "true"),
     "agentRoleAssignment": v(live.get("AGENT_ROLE_ASSIGNMENT", "").lower() == "true"),
     "roleAssignAllowedRoles": v(live.get("ROLE_ASSIGN_ALLOWED_ROLES", "")), "roleAssignScopes": v(live.get("ROLE_ASSIGN_SCOPES", "")),
     # Agent TAP as it is live (deploy_free.sh tap, approved separately).
     "agentTap": v(live.get("AGENT_TAP", "").lower() == "true"),
     "agentTapLifetimeMinutes": v(int(live.get("AGENT_TAP_LIFETIME_MINUTES") or 480)),
     # Agent privileged rows as they are live (deploy_free.sh privileged, approved separately).
     "agentPrivileged": v(live.get("AGENT_PRIVILEGED", "").lower() == "true"),
     # Mail and environment as they are live (deploy_free.sh email / environment).
     "notifySender": v(live.get("NOTIFY_SENDER") or "Outbox"), "notifyFrom": v(live.get("NOTIFY_FROM", "")),
     "acsEndpoint": v(live.get("ACS_ENDPOINT", "")), "enableEmail": v(live.get("NOTIFY_SENDER") == "AcsEmail"),
     "appEnvironment": v(live.get("APP_ENVIRONMENT") or "pilot"), "notifyRedirectTo": v(live.get("NOTIFY_REDIRECT_TO", "")),
     # Databricks policy type as it is live (deploy_free.sh databricks, approved separately).
     "enableDatabricks": v(sys.argv[5] == "true")}
json.dump({"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
           "contentVersion": "1.0.0.0", "parameters": p}, open(out, "w"), indent=2)
print("wrote", out, "| worker on, notifications", p["notifySender"]["value"], "| environment", p["appEnvironment"]["value"], "| ADO off, role reader None")
PY
    ;;
  worker)
    deploy_worker
    ;;
  grant-graph)
    grant_graph
    ;;
  revoke-graph-writes)
    if [[ "${3:-}" == "--approved-by" && -n "${4:-}" ]]; then
      APPROVED_BY="$4"
      echo "Approval recorded: $4" | tee -a "$ROOT/.local/approval.txt"
    fi
    revoke_graph_writes
    ;;
  provisioning)
    MODE="${3:?on|off}"; shift 3
    ARGS=()
    while [[ $# -gt 0 ]]; do
      if [[ "$1" == "--approved-by" && -n "${2:-}" ]]; then
        APPROVED_BY="$2"; echo "Approval recorded (provisioning $MODE): $2" | tee -a "$ROOT/.local/approval.txt"; shift 2
      elif [[ "$1" == "--from-sheet" && -n "${2:-}" ]]; then
        FROM_SHEET="$2"; shift 2
      else
        ARGS+=("$1"); shift
      fi
    done
    provisioning "$MODE" ${ARGS[@]+"${ARGS[@]}"}
    ;;
  tap)
    MODE="${3:?on|off}"; shift 3; LIFETIME=480
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --lifetime) LIFETIME="${2:?minutes}"; shift 2 ;;
        --approved-by) APPROVED_BY="${2:?name and date}"; echo "Approval recorded (tap $MODE): $2" | tee -a "$ROOT/.local/approval.txt"; shift 2 ;;
        *) echo "unknown argument $1"; exit 1 ;;
      esac
    done
    agent_tap "$MODE" "$LIFETIME"
    ;;
  privileged)
    MODE="${3:?on|off}"
    if [[ "${4:-}" == "--approved-by" && -n "${5:-}" ]]; then
      APPROVED_BY="$5"; echo "Approval recorded (privileged $MODE): $5" | tee -a "$ROOT/.local/approval.txt"
    fi
    agent_privileged "$MODE"
    ;;
  environment)
    ENVNAME="${3:?pilot|dev|test|prod}"; REDIRECT=""
    [[ "${4:-}" == "--redirect" ]] && REDIRECT="${5:?mail address}"
    set_environment "$ENVNAME" "$REDIRECT"
    ;;
  email)
    shift 2; REDIRECT=""
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --redirect) REDIRECT="${2:?mail address}"; shift 2 ;;
        --approved-by) APPROVED_BY="${2:?name and date}"; echo "Approval recorded (email): $2" | tee -a "$ROOT/.local/approval.txt"; shift 2 ;;
        *) echo "unknown argument $1"; exit 1 ;;
      esac
    done
    email_setup "$REDIRECT"
    ;;
  databricks)
    shift 2; DBX_LOCATION="centralindia"
    while [[ $# -gt 0 ]]; do
      case "$1" in
        --location) DBX_LOCATION="${2:?region}"; shift 2 ;;
        --approved-by) APPROVED_BY="${2:?name and date}"; echo "Approval recorded (databricks): $2" | tee -a "$ROOT/.local/approval.txt"; shift 2 ;;
        *) echo "unknown argument $1"; exit 1 ;;
      esac
    done
    databricks_setup "$DBX_LOCATION"
    ;;
  app)
    # Redeploy the app only (no infrastructure change). Use after a failed upload or a code change.
    RG="rg-pmo-review-pilot"
    SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
    COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
    PYTHON=$("$ROOT/tools/cosmos_python.sh") # before the upload: a missing package fails fast, not after it
    app_settings_v2 "$RG" "$SWA"
    deploy_app "$RG" "$SWA"
    if [[ "${SEED_SAMPLE:-0}" == "1" ]]; then
      CS=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
      COSMOS_CONNECTION_STRING="$CS" "$PYTHON" "$ROOT/tools/seed_sample_data.py"
      unset CS
    fi
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    echo "$(git -C "$ROOT" rev-parse --short HEAD) $(date -u +%FT%TZ)" > "$ROOT/.local/last-app-deploy"
    "$ROOT/tools/cost_check.sh" "$RG"
    ;;
  *) echo "usage: deploy_free.sh whatif|apply|app|status|params|worker|grant-graph|revoke-graph-writes|provisioning|tap|privileged|environment|email|databricks ..."; exit 1 ;;
esac
