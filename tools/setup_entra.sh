#!/usr/bin/env bash
# Phase 2 test setup in Microsoft Entra (run by a Global Administrator in Cloud Shell). Test identities only.
# The app itself never creates users or groups; this is one-off setup for the SAMPLE end-to-end test.
#
#   bash tools/setup_entra.sh identities <tenant-domain> [approver-upn]
#       Creates (if missing): security group SAMPLE-GRP-ALPHA-DEV-ENG and test user sample.joiner@<tenant-domain>
#       (temporary password shown ONCE, must be changed at first sign-in). Assigns an Entra ID P2 / Governance
#       licence (trial) to the joiner and to the approver, if one is available.
#
#   tools/setup_entra.sh check
#       Read-only: lists Entra ID P2 / Governance licences in the tenant and the SAMPLE group/user.
set -euo pipefail
CMD="${1:?identities|check}"
GROUP="SAMPLE-GRP-ALPHA-DEV-ENG"

graph() { az rest --method "$1" --url "https://graph.microsoft.com/v1.0/$2" "${@:3}"; }

governance_sku() {
  # P2 or ID Governance (incl. trials). Prints "<skuId> <skuPartNumber> <free>" for the first with free seats.
  graph GET subscribedSkus --query "value[?contains(skuPartNumber,'AAD_PREMIUM_P2') || contains(skuPartNumber,'GOVERNANCE') || contains(skuPartNumber,'Entra_Suite') || contains(skuPartNumber,'ENTRA_SUITE')].[skuId, skuPartNumber, to_string(prepaidUnits.enabled), to_string(consumedUnits)]" -o tsv \
    | awk '{ free=$3-$4; if (free>0) { print $1, $2, free; exit } }'
}

assign_licence() {
  local upn="$1" sku="$2" id
  id=$(az ad user show --id "$upn" --query id -o tsv)
  graph PATCH "users/$id" --headers "Content-Type=application/json" --body '{"usageLocation":"IN"}' -o none
  if graph GET "users/$id/licenseDetails" --query "value[].skuId" -o tsv | grep -qx "$sku"; then
    echo "Licence already assigned: $upn"
  else
    graph POST "users/$id/assignLicense" --headers "Content-Type=application/json" \
      --body "{\"addLicenses\":[{\"skuId\":\"$sku\",\"disabledPlans\":[]}],\"removeLicenses\":[]}" -o none
    echo "Licence assigned: $upn"
  fi
}

case "$CMD" in
  check)
    echo "== Entra ID P2 / Governance licences (skuId, part number, enabled, consumed) =="
    graph GET subscribedSkus --query "value[].[skuId, skuPartNumber, to_string(prepaidUnits.enabled), to_string(consumedUnits)]" -o tsv
    echo "== SAMPLE group / joiner =="
    az ad group list --display-name "$GROUP" --query "[].{name:displayName,id:id}" -o table
    az ad user list --filter "startswith(userPrincipalName,'sample.joiner@')" --query "[].{upn:userPrincipalName,id:id}" -o table
    ;;
  identities)
    DOMAIN="${2:?tenant domain, e.g. contoso.onmicrosoft.com}"
    APPROVER="${3:-}"
    JOINER="sample.joiner@$DOMAIN"

    if [[ -z "$(az ad group list --display-name "$GROUP" --query "[0].id" -o tsv)" ]]; then
      az ad group create --display-name "$GROUP" --mail-nickname "sample-grp-alpha-dev-eng" \
        --description "SAMPLE pilot: Data Engineer, Dev. Membership managed only by the Entra access package." -o none
      echo "Created group $GROUP"
    else
      echo "Group exists: $GROUP"
    fi

    if [[ -z "$(az ad user list --filter "userPrincipalName eq '$JOINER'" --query "[0].id" -o tsv)" ]]; then
      PW="Sample-$(python3 -c 'import secrets;print(secrets.token_urlsafe(12))')!9"
      az ad user create --display-name "SAMPLE Joiner" --user-principal-name "$JOINER" \
        --password "$PW" --force-change-password-next-sign-in true -o none
      echo "Created test user $JOINER"
      echo "Temporary password (shown once, not saved; must be changed at first sign-in): $PW"
      unset PW
    else
      echo "User exists: $JOINER"
    fi

    read -r SKU PART FREE < <(governance_sku || true) || true
    if [[ -n "${SKU:-}" ]]; then
      echo "Using licence $PART ($FREE free)"
      assign_licence "$JOINER" "$SKU"
      [[ -n "$APPROVER" ]] && assign_licence "$APPROVER" "$SKU"
    else
      echo "No free Entra ID P2 / Governance licence found. Start the free trial (Entra admin center > Billing > Licenses > All products > Try / Buy), then re-run."
    fi
    echo "Next: create the catalog and access package in the Entra admin center (workflows/08-entra-handoff.md, steps 4-6)."
    ;;
  *) echo "usage: setup_entra.sh identities <tenant-domain> [approver-upn] | check"; exit 1 ;;
esac
