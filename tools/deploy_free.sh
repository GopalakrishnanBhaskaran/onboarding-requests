#!/usr/bin/env bash
# Zero-cost pilot deployment, in two explicit steps.
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
#       seeds SAMPLE data. With enableHandoff=true (Phase 2) also deploys the hand-off sync worker.
#
#   tools/deploy_free.sh status <subscriptionId>
#       Read-only. Shows where the deployment stands (use after a Cloud Shell disconnect).
#
#   tools/deploy_free.sh worker <subscriptionId>
#       Phase 2: redeploy the hand-off sync worker code only.
#
#   tools/deploy_free.sh grant-graph <subscriptionId>
#       Phase 2: give the worker's managed identity Microsoft Graph EntitlementManagement.Read.All (read-only).
#       Needs an Entra role that can grant app permissions (Global Administrator or Privileged Role Administrator).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CMD="${1:?whatif|apply|app}"; SUB="${2:?subscriptionId}"; PARAMS="${3:-}"
DEPLOYMENT="pmo-review-pilot"
if [[ "$CMD" != "app" && "$CMD" != "worker" && "$CMD" != "grant-graph" && "$CMD" != "status" ]]; then
  [[ -n "$PARAMS" ]] || { echo "params.json required for $CMD"; exit 1; }
  LOCATION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['parameters']['location']['value'])" "$PARAMS")"
fi
mkdir -p "$ROOT/.local"
az account set --subscription "$SUB"
RG_DEFAULT=rg-pmo-review-pilot

param_true() { [[ -n "$PARAMS" ]] && python3 -c "import json,sys;p=json.load(open(sys.argv[1]))['parameters'];sys.exit(0 if p.get(sys.argv[2],{}).get('value') else 1)" "$PARAMS" "$1"; }
handoff_enabled() { param_true enableHandoff; }
worker_enabled() { handoff_enabled && param_true deployWorker; }
auto_assign_enabled() { worker_enabled && param_true enableAutoAssign; }

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

# Phase 2 worker: build, zip, deploy (Flex Consumption one-deploy via Entra ID; basic auth is off).
deploy_worker() {
  local name; name=$(worker_name)
  [[ -n "$name" ]] || { echo "No hand-off worker in $RG_DEFAULT (enableHandoff=false?)."; return 1; }
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

# Graph application permissions for the worker identity; idempotent. Read-only unless the worker runs in
# auto-assign mode (app setting ENTRA_AUTO_ASSIGN=true), which adds the write + user lookup it needs.
grant_graph() {
  local name mi graph auto perm role have roles
  name=$(worker_name); [[ -n "$name" ]] || { echo "No hand-off worker found."; return 1; }
  mi=$(az functionapp identity show -g "$RG_DEFAULT" -n "$name" --query principalId -o tsv)
  graph=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query id -o tsv)
  auto=$(az functionapp config appsettings list -g "$RG_DEFAULT" -n "$name" --query "[?name=='ENTRA_AUTO_ASSIGN'].value | [0]" -o tsv 2>/dev/null)
  roles=(EntitlementManagement.Read.All)
  [[ "${auto,,}" == "true" ]] && roles+=(EntitlementManagement.ReadWrite.All User.ReadBasic.All)
  for perm in "${roles[@]}"; do
    role=$(az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?value=='$perm'].id | [0]" -o tsv)
    [[ -n "$role" ]] || { echo "Could not find $perm on Microsoft Graph."; return 1; }
    have=$(az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
      --query "length(value[?appRoleId=='$role'])" -o tsv)
    if [[ "$have" == "0" ]]; then
      az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" \
        --headers "Content-Type=application/json" \
        --body "{\"principalId\":\"$mi\",\"resourceId\":\"$graph\",\"appRoleId\":\"$role\"}" -o none
      echo "Granted $perm (application) to $name. Allow a few minutes to take effect."
    else
      echo "Already granted: $perm -> $name"
    fi
  done
  echo "Graph permissions now held by $name (expected: ${roles[*]}):"
  az rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals/$mi/appRoleAssignments" --query "value[].appRoleId" -o tsv \
    | while read -r id; do az ad sp show --id 00000003-0000-0000-c000-000000000000 --query "appRoles[?id=='$id'].value | [0]" -o tsv; done
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
    echo "Expected resources: resource group, policy assignment, budget, Cosmos DB account (free tier) + 1 database + 8 containers, Static Web App (Free)."
    echo "Expected cost: ₹0 while within free limits (SWA Free plan; Cosmos free tier 1000 RU/s + 25 GB; policy and budgets are free)."
    if handoff_enabled && ! worker_enabled; then
      echo "Phase 2 STAGE A (enableHandoff=true, deployWorker=false): policy allows the worker types, budget ₹2,000 with 50/80/100% alerts,"
      echo "  + 'handoffs' container (shared free-tier throughput), tag update. Creates nothing paid. Expected cost: ₹0."
    fi
    if worker_enabled; then
      echo "Phase 2 STAGE B (deployWorker=true, paid from credits): + Flex Consumption plan + Function App (system identity) + storage account"
      echo "  (Standard_LRS, no shared keys) + Storage Blob Data Owner role + Cosmos data role on the 'handoffs' container."
      echo "  Expected cost: roughly ₹50-300/month (storage + Flex executions; Flex has a monthly free grant). Budget: ₹2,000 with 50/80/100% alerts."
    fi
    if auto_assign_enabled; then
      CAT=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['parameters'].get('entraCatalogId',{}).get('value',''))" "$PARAMS")
      [[ "$CAT" =~ ^[0-9a-fA-F-]{36}$ ]] || { echo "STOP: enableAutoAssign=true needs entraCatalogId (run: bash tools/setup_entra.sh package ...)."; exit 2; }
      echo "AUTO-ASSIGN (enableAutoAssign=true): worker app settings ENTRA_AUTO_ASSIGN=true, ENTRA_ALLOWED_CATALOG_ID=$CAT. No new resources, cost unchanged."
      echo "  After apply, grant-graph adds EntitlementManagement.ReadWrite.All + User.ReadBasic.All to the worker identity;"
      echo "  the app sets REQUIRE_SEPARATE_REVIEWER=true (a second Reviewer's acceptance is the access decision)."
    fi
    if handoff_enabled; then
      WLOC=$(python3 -c "import json,sys;p=json.load(open(sys.argv[1]))['parameters'];print(p.get('workerLocation',p['location'])['value'])" "$PARAMS")
      LOCATION_CHECK="$WLOC"
      echo "== Flex Consumption availability for $WLOC =="
      az functionapp list-flexconsumption-locations --query "[].name" -o tsv | tr -d ' ' | grep -ix "$LOCATION_CHECK" \
        || echo "WARNING: $LOCATION_CHECK is not listed for Flex Consumption. Pick another region for the worker before approving."
      az functionapp list-flexconsumption-runtimes -l "$LOCATION_CHECK" --runtime dotnet-isolated -o table || true
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
    if auto_assign_enabled; then
      # Auto-assign: a SECOND Reviewer's acceptance is the access decision; the worker files the Entra assignment.
      az staticwebapp appsettings set -n "$SWA" -g "$RG" --setting-names "ENTRA_HANDOFF_ENABLED=true" "ENTRA_AUTO_ASSIGN=true" "REQUIRE_SEPARATE_REVIEWER=true" -o none
    elif worker_enabled; then
      # Link mode: the joiner requests in My Access and Entra's approver decides. One tester, so separate-reviewer is off.
      az staticwebapp appsettings set -n "$SWA" -g "$RG" --setting-names "ENTRA_HANDOFF_ENABLED=true" "ENTRA_AUTO_ASSIGN=false" "REQUIRE_SEPARATE_REVIEWER=false" -o none
    fi

    # Build + deploy (SWA CLI). The API runtime comes from staticwebapp.config.json (single source of truth;
    # InfraConsistencyTests checks it matches the API project's TargetFramework).
    RUNTIME=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['platform']['apiRuntime'])" "$ROOT/staticwebapp.config.json")
    API_LANG="${RUNTIME%%:*}"; API_VER="${RUNTIME##*:}"
    # The config file spells it "dotnet-isolated" (CLI schema); the deploy client only accepts
    # "dotnetisolated" (no dash) for --api-language, else "Function language info isn't provided" (F21).
    CLI_LANG="${API_LANG//-/}"
    echo "API runtime: $API_LANG $API_VER (CLI flag: $CLI_LANG)"
    dotnet publish "$ROOT/src/api/PmoReview.Api" -c Release -o "$ROOT/.local/api"
    npm --prefix "$ROOT/src/web" ci
    npm --prefix "$ROOT/src/web" run build
    # Ship the config with the app (routes, auth, headers, platform.apiRuntime).
    cp "$ROOT/staticwebapp.config.json" "$ROOT/src/web/dist/staticwebapp.config.json"
    TOKEN=$(az staticwebapp secrets list -n "$SWA" -g "$RG" --query properties.apiKey -o tsv)
    (cd "$ROOT/src/web" && SWA_CLI_DEPLOYMENT_TOKEN="$TOKEN" npx swa deploy dist \
      --api-location "$ROOT/.local/api" --api-language "$CLI_LANG" --api-version "$API_VER" \
      --swa-config-location "$ROOT/src/web/dist" --env production)

    COSMOS_CONNECTION_STRING="$CS" python3 "$ROOT/tools/seed_sample_data.py"
    unset CS TOKEN
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    echo "Next: each test user signs in once, reads userId from /.auth/me, then:"
    echo "  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py --register-user <userId> --project SAMPLE-ALPHA --roles PMO"
    if worker_enabled; then
      deploy_worker || true
      grant_graph || echo "Graph permission NOT granted (needs Global Administrator). Run: tools/deploy_free.sh grant-graph $SUB"
    elif handoff_enabled; then
      echo "Phase 2 stage A applied. Wait ~30 minutes for the policy change, set deployWorker=true, then run whatif again (stage B)."
    fi
    "$ROOT/tools/cost_check.sh" "$RG"
    ;;
  status)
    RG=$RG_DEFAULT
    ok() { if eval "$2" >/dev/null 2>&1; then echo "  [OK]   $1"; else echo "  [--]   $1"; fi; }
    echo "== Last deployment =="
    az deployment sub show --name "$DEPLOYMENT" --query "{state:properties.provisioningState, finished:properties.timestamp}" -o tsv 2>/dev/null || echo "  none"
    echo "== Phase 2 stage A =="
    ok "policy allows the worker types" policy_allows_worker
    BUDGET=$(az consumption budget list --resource-group "$RG" --query "[?name=='pmo-pilot-zero-cost'].amount | [0]" -o tsv 2>/dev/null)
    echo "  budget amount: ${BUDGET:-not found} (stage A sets 2000)"
    COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
    ok "Cosmos container 'handoffs' exists" "az cosmosdb sql container show -g $RG -a $COSMOS -d pmoreview -n handoffs"
    echo "== Phase 2 stage B =="
    W=$(worker_name); echo "  worker: ${W:-not deployed}"
    SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
    FLAG=$(az staticwebapp appsettings list -n "$SWA" -g "$RG" --query "properties.ENTRA_HANDOFF_ENABLED" -o tsv 2>/dev/null)
    echo "  ENTRA_HANDOFF_ENABLED on the app: ${FLAG:-not set (off)}"
    AUTO=$(az staticwebapp appsettings list -n "$SWA" -g "$RG" --query "properties.ENTRA_AUTO_ASSIGN" -o tsv 2>/dev/null)
    echo "  ENTRA_AUTO_ASSIGN on the app: ${AUTO:-not set (off)}"
    if [[ -n "$W" ]]; then
      WAUTO=$(az functionapp config appsettings list -g "$RG" -n "$W" --query "[?name=='ENTRA_AUTO_ASSIGN'].value | [0]" -o tsv 2>/dev/null)
      echo "  ENTRA_AUTO_ASSIGN on the worker: ${WAUTO:-not set (off)}"
    fi
    echo "== App =="
    HOST=$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)
    echo "  https://$HOST  (new code if 'New request' shows a 'New joiner sign-in name' field)"
    ;;
  worker)
    deploy_worker
    ;;
  grant-graph)
    grant_graph
    ;;
  app)
    # Redeploy the app only (no infrastructure change). Use after a failed upload or a code change.
    RG=rg-pmo-review-pilot
    SWA=$(az staticwebapp list -g "$RG" --query "[0].name" -o tsv)
    COSMOS=$(az cosmosdb list -g "$RG" --query "[0].name" -o tsv)
    RUNTIME=$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['platform']['apiRuntime'])" "$ROOT/staticwebapp.config.json")
    API_LANG="${RUNTIME%%:*}"; API_VER="${RUNTIME##*:}"
    # The config file spells it "dotnet-isolated" (CLI schema); the deploy client only accepts
    # "dotnetisolated" (no dash) for --api-language, else "Function language info isn't provided" (F21).
    CLI_LANG="${API_LANG//-/}"
    echo "API runtime: $API_LANG $API_VER (CLI flag: $CLI_LANG) -> $SWA"
    rm -rf "$ROOT/.local/api"
    dotnet publish "$ROOT/src/api/PmoReview.Api" -c Release -o "$ROOT/.local/api"
    npm --prefix "$ROOT/src/web" ci
    npm --prefix "$ROOT/src/web" run build
    # Ship the config with the app (routes, auth, headers, platform.apiRuntime).
    cp "$ROOT/staticwebapp.config.json" "$ROOT/src/web/dist/staticwebapp.config.json"
    TOKEN=$(az staticwebapp secrets list -n "$SWA" -g "$RG" --query properties.apiKey -o tsv)
    (cd "$ROOT/src/web" && SWA_CLI_DEPLOYMENT_TOKEN="$TOKEN" npx swa deploy dist \
      --api-location "$ROOT/.local/api" --api-language "$CLI_LANG" --api-version "$API_VER" \
      --swa-config-location "$ROOT/src/web/dist" --env production)
    unset TOKEN
    CS=$(az cosmosdb keys list -g "$RG" -n "$COSMOS" --type connection-strings --query "connectionStrings[0].connectionString" -o tsv)
    COSMOS_CONNECTION_STRING="$CS" python3 "$ROOT/tools/seed_sample_data.py"
    unset CS
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    "$ROOT/tools/cost_check.sh" "$RG"
    ;;
  *) echo "usage: deploy_free.sh whatif|apply|app|status|worker|grant-graph ..."; exit 1 ;;
esac
