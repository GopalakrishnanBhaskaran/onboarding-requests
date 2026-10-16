#!/usr/bin/env bash
# Zero-cost pilot deployment, in two explicit steps.
#
#   tools/deploy_free.sh whatif <subscriptionId> <params.json>
#       Read-only. Writes the what-if to .local/whatif.txt for Gokul to review.
#
#   tools/deploy_free.sh apply <subscriptionId> <params.json> --approved-by "<name> <date>"
#       ONLY after Gokul replied "approved" to that exact what-if. Creates resources, sets the Cosmos
#       connection string as a SWA app setting (never printed or written to disk), deploys the app,
#       seeds SAMPLE data.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CMD="${1:?whatif|apply}"; SUB="${2:?subscriptionId}"; PARAMS="${3:?params.json}"
DEPLOYMENT="pmo-review-pilot"
LOCATION="$(python3 -c "import json,sys;print(json.load(open(sys.argv[1]))['parameters']['location']['value'])" "$PARAMS")"
mkdir -p "$ROOT/.local"
az account set --subscription "$SUB"

case "$CMD" in
  whatif)
    "$ROOT/tools/check_free_tier.sh" "$SUB"
    az deployment sub what-if --name "$DEPLOYMENT" --location "$LOCATION" \
      --template-file "$ROOT/infra/main.bicep" --parameters "@$PARAMS" \
      --result-format FullResourcePayloads | tee "$ROOT/.local/whatif.txt"
    echo
    echo "Expected resources: resource group, policy assignment, budget, Cosmos DB account (free tier) + 1 database + 7 containers, Static Web App (Free)."
    echo "Expected cost: ₹0 while within free limits (SWA Free plan; Cosmos free tier 1000 RU/s + 25 GB; policy and budgets are free)."
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

    # Build + deploy (SWA CLI). See VERIFIED_FACTS F4: if the CLI rejects dotnet-isolated:10.0, stop and report.
    dotnet publish "$ROOT/src/api/PmoReview.Api" -c Release -o "$ROOT/.local/api"
    npm --prefix "$ROOT/src/web" ci
    npm --prefix "$ROOT/src/web" run build
    TOKEN=$(az staticwebapp secrets list -n "$SWA" -g "$RG" --query properties.apiKey -o tsv)
    (cd "$ROOT/src/web" && SWA_CLI_DEPLOYMENT_TOKEN="$TOKEN" npx swa deploy dist \
      --api-location "$ROOT/.local/api" --api-language dotnet-isolated --api-version 10.0 \
      --swa-config-location "$ROOT" --env production)

    COSMOS_CONNECTION_STRING="$CS" python3 "$ROOT/tools/seed_sample_data.py"
    unset CS TOKEN
    echo "Deployed commit $(git -C "$ROOT" rev-parse --short HEAD) to https://$(az staticwebapp show -n "$SWA" -g "$RG" --query defaultHostname -o tsv)"
    echo "Next: each test user signs in once, reads userId from /.auth/me, then:"
    echo "  COSMOS_CONNECTION_STRING=... python3 tools/seed_sample_data.py --register-user <userId> --project SAMPLE-ALPHA --roles PMO"
    "$ROOT/tools/cost_check.sh" "$RG"
    ;;
  *) echo "usage: deploy_free.sh whatif|apply ..."; exit 1 ;;
esac
