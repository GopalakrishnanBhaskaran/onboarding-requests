#!/usr/bin/env bash
# PMO onboarding flow (v2) deployment, in explicit steps.
#
#   tools/deploy_free.sh whatif <subscriptionId> <params.json>
#       Read-only. Writes the what-if to .local/whatif.txt for Gokul to review.
#
#   tools/deploy_free.sh app <subscriptionId>
#       App-only redeploy (no infrastructure change): API + SPA + SAMPLE seed. Covered by the original approval.
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
#       v2: the AI agent never creates or assigns. Removes the retired Phase 2b/2c Graph permissions from the worker
#       identity. Security change: without --approved-by it only lists what it would remove.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CMD="${1:?whatif|apply|app|status|params|worker|grant-graph|revoke-graph-writes}"; SUB="${2:?subscriptionId}"; PARAMS="${3:-}"
DEPLOYMENT="pmo-review-pilot"
if [[ "$CMD" == "whatif" || "$CMD" == "apply" ]]; then
  [[ -n "$PARAMS" ]] || { echo "params.json required for $CMD"; exit 1; }
  LOCATION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['parameters']['location']['value'])" "$PARAMS")"
fi
mkdir -p "$ROOT/.local"
az account set --subscription "$SUB"
RG_DEFAULT=rg-pmo-review-pilot

param_true() { [[ -n "$PARAMS" ]] && python3 -c "import json,sys;p=json.load(open(sys.argv[1]))['parameters'];sys.exit(0 if p.get(sys.argv[2],{}).get('value') else 1)" "$PARAMS" "$1"; }
handoff_enabled() { param_true enableHandoff; }
worker_enabled() { handoff_enabled && param_true deployWorker; }

# Stage B needs stage A's policy to be live: Azure checks new resources against the CURRENT assignment.
policy_allows_worker() {
  az policy assignment show -g "$RG_DEFAULT" -n pmo-pilot-allowed-types \
    --query "parameters.listOfResourceTypesAllowed.value" -o tsv 2>/dev/null | grep -qx "Microsoft.Web/sites"
}

worker_name() { az functionapp list -g "$RG_DEFAULT" --query "[?starts_with(name,'func-pmo-worker-')].name | [0]" -o tsv; }

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
  dotnet publish "$ROOT/src/worker/PmoReview.Worker" -c Release -o "$ROOT/.local/worker"
  (cd "$ROOT/.local/worker" && zip -qr "$ROOT/.local/worker.zip" .)
  # A just-created role assignment (Blob Data Owner on the package container) can take minutes to apply: retry.
  local i
  for i in 1 2 3 4; do
    if az functionapp deployment source config-zip -g "$RG_DEFAULT" -n "$name" --src "$ROOT/.local/worker.zip" -o none; then
      echo "Worker deployed: $name (commit $(git -C "$ROOT" rev-parse --short HEAD)). It runs every 5 minutes."
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
  az staticwebapp appsettings set -n "$swa" -g "$rg" --setting-names "TAP_MAX_LIFETIME_MINUTES=60" \
    "APP_URL=https://$(az staticwebapp show -n "$swa" -g "$rg" --query defaultHostname -o tsv)" -o none
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

    # Secret handling: piped straight from Cosmos into SWA app settings. Never echoed, never on disk.
    CS=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
    az staticwebapp appsettings set -n "$SWA" -g "$RG" --setting-names \
      "COSMOS_CONNECTION_STRING=$CS" "COSMOS_DATABASE=pmoreview" "STORAGE_MODE=Cosmos" \
      "REVIEW_MODEL_PROVIDER=RuleOnly" "REVIEW_DISPATCHER=Inline" "ALLOW_PAID_SERVICES=false" \
      "ALLOWED_IDENTITY_PROVIDERS=aad" "PROCESSING_BUDGET_SECONDS=30" "STUCK_RUN_MINUTES=5" -o none
    app_settings_v2 "$RG" "$SWA"
    deploy_app "$RG" "$SWA"
    COSMOS_CONNECTION_STRING="$CS" python3 "$ROOT/tools/seed_sample_data.py"
    unset CS
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    echo "Next: each test user signs in once, reads userId from /.auth/me, then:"
    echo "  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py --register-user <userId> --project SAMPLE-ALPHA --roles Requester,Owner"
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
    echo "== Worker =="
    W=$(worker_name); echo "  worker: ${W:-not deployed}"
    SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
    if [[ -n "$W" ]]; then
      for k in WORKFLOW_SCHEDULE NOTIFY_SENDER ROLE_READER ADO_ORG_URL ENTRA_AUTO_ASSIGN ENTRA_PROVISION_JOINERS; do
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
    python3 - "$B" "$ROOT/.local/params.json" "${HOST:-}" <<'PY'
import json, sys
b, out, host = json.loads(sys.argv[1]), sys.argv[2], sys.argv[3]
v = lambda x: {"value": x}
p = {"resourceGroupName": v("rg-pmo-review-pilot"), "location": v("centralindia"), "swaLocation": v("eastasia"),
     "budgetEmail": v(next(iter(b["notifications"].values()))["contactEmails"][0]), "budgetAmount": v(2000),
     "budgetStartDate": v(b["timePeriod"]["startDate"][:10]), "enableHandoff": v(True), "deployWorker": v(True),
     "workerLocation": v("centralindia"), "workerRuntimeVersion": v("10.0"),
     "notifySender": v("Outbox"), "roleReader": v("None"), "appUrl": v(f"https://{host}" if host else "")}
json.dump({"$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
           "contentVersion": "1.0.0.0", "parameters": p}, open(out, "w"), indent=2)
print("wrote", out, "| worker on, notifications Outbox, ADO off, role reader None")
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
  app)
    # Redeploy the app only (no infrastructure change). Use after a failed upload or a code change.
    RG=rg-pmo-review-pilot
    SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
    COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
    app_settings_v2 "$RG" "$SWA"
    deploy_app "$RG" "$SWA"
    CS=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
    COSMOS_CONNECTION_STRING="$CS" python3 "$ROOT/tools/seed_sample_data.py"
    unset CS
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    "$ROOT/tools/cost_check.sh" "$RG"
    ;;
  *) echo "usage: deploy_free.sh whatif|apply|app|status|params|worker|grant-graph|revoke-graph-writes ..."; exit 1 ;;
esac
