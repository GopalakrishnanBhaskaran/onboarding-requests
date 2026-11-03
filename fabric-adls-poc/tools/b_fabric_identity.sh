#!/usr/bin/env bash
# Agent B — Fabric capacity/workspace/identity + RBAC + resource instance rule.
# Default: PLAN (read-only). --apply needs wave 1 approval. Run after Agent A's device IP rule.
# Usage: bash b_fabric_identity.sh --workspace <name> --container <c> [--capacity <armName>] [--create-capacity <name> --region <loc> --capacity-admin <native UPN>] [--apply]
#        bash b_fabric_identity.sh --list-admins   # native member users who can be capacity admin (read-only)
#        add --capacity-only for step B1 (capacity only); the capacity admin then adds you as contributor, and the full run follows
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
ST="$POC_DIR/tools/state.py"
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"][sys.argv[2]])' "$STATE_FILE" "$1"; }
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"; STG="$(env_get storageAccount)"; TENANT="$(env_get tenant)"
case "$SUB" in *'*'*|*UNVERIFIED*|"") SUB="$(stg_sub "$(env_get storageAccount)" || az account show --query id -o tsv)";; esac   # template values are masked
case "${TENANT:-}" in *'*'*|*UNVERIFIED*|"") TENANT="$(az account show --query tenantId -o tsv)";; esac
WS="" ; CONTAINER="" ; CAP="" ; NEWCAP="" ; REGION="" ; APPLY=0 ; CAP_ADMIN="" ; CAP_ONLY=0
list_admins() {   # read-only; on screen only, never saved to evidence. grep instead of JMESPath "!" (bash history expansion)
  # Graph returns userType/accountEnabled only with $select (az ad user list/show leave them null, FB31)
  az rest --method get --url 'https://graph.microsoft.com/v1.0/users?$select=userPrincipalName,userType,accountEnabled&$top=999' \
    --query "value[?userType=='Member'].[userPrincipalName,accountEnabled]" -o tsv | grep -v '#EXT#' || true
}
for a in "$@"; do case "$a" in *"<"*|*">"*) die "replace the placeholder $a with a real value (no < >)";; esac; done
if [ "${1:-}" = --list-admins ]; then
  L="$(list_admins)"
  if [ -n "$L" ]; then echo "Native member users (pick one for --capacity-admin):"; echo "$L"
  else echo "No native member user in this tenant. Create one in the Entra portal (you set the password; Claude never sees it):"
       D="$(az ad signed-in-user show --query userPrincipalName -o tsv | sed 's/.*#EXT#@//; s/^[^@]*@//')"
       echo "  portal.azure.com > Microsoft Entra ID > Users > New user > Create new user > UPN fabricadmin@$D > Create"
       echo "  (no roles, no groups; then: ADMIN='fabricadmin@$D')"; fi
  exit 0
fi
while [ $# -gt 0 ]; do
  case "$1" in
    --workspace) WS="$2"; shift 2;; --container) CONTAINER="$2"; shift 2;;
    --capacity) CAP="$2"; shift 2;; --create-capacity) NEWCAP="$2"; shift 2;; --region) REGION="$2"; shift 2;;
    --capacity-admin) CAP_ADMIN="$2"; shift 2;;   # a native member UPN of this tenant (B2B users can't be capacity admins, FB24)
    --capacity-only) CAP_ONLY=1; shift;;   # step B1: stop once the capacity is Active (the capacity admin then adds you as contributor in the UI)
    --apply) APPLY=1; shift;; *) die "unknown argument $1";;
  esac
done
[ -n "$WS" ] && [ -n "$CONTAINER" ] || die "--workspace and --container are required"
az account set --subscription "$SUB"
STG_ID="/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Storage/storageAccounts/$STG"
CONT_SCOPE="$STG_ID/blobServices/default/containers/$CONTAINER"
export CAN_SCOPE="$STG_ID"; : > "$ADMIN_REQ"   # steps you lack rights for become admin requests
ME_UPN="$(az ad signed-in-user show --query userPrincipalName -o tsv)"
ME_OID="$(az ad signed-in-user show --query id -o tsv)"
[ -n "$CAP_ADMIN" ] || CAP_ADMIN="$ME_UPN"
if [ -n "$NEWCAP" ]; then   # FB24/FB26: only a member of this tenant can be a Fabric capacity admin; check before the ARM call
  case "$CAP_ADMIN" in *"#EXT#"*) die "capacity admin must be a native member of this tenant, not $(echo "$CAP_ADMIN" | sed -E 's/^[^@#]+/***/') (FB26). Run: bash $0 --list-admins";; esac
  t="$(az rest --method get --url "https://graph.microsoft.com/v1.0/users/$CAP_ADMIN?\$select=userType,accountEnabled" --query "[userType,accountEnabled]" -o tsv 2>/dev/null | tr '\n\t' '  ' | tr '[:upper:]' '[:lower:]' || true)"
  case "$t" in "member true"*) echo "OK      capacity admin is an enabled member of this tenant";; "") die "capacity admin $(echo "$CAP_ADMIN" | sed -E 's/^[^@]+/***/') not found in this tenant. Run: bash $0 --list-admins";; *) die "capacity admin must be an enabled Member user (found: $t)";; esac
fi
[ "$APPLY" = 1 ] && { require_approval 1; python3 "$ST" lock fabric-workspace B; python3 "$ST" lock fabric-capacity B; python3 "$ST" agent B running apply; }
trap '[ "$APPLY" = 1 ] && { python3 "$ST" unlock fabric-workspace B; python3 "$ST" unlock fabric-capacity B; python3 "$ST" unlock storage-network-rules B 2>/dev/null; } || true' EXIT
PLAN=()
change() { echo "CHANGE  $1"; PLAN+=("$1"); }
ok() { echo "OK      $1"; }
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }

echo "== Agent B plan: workspace '$WS', storage $STG/$CONTAINER"
# 1. capacity: an F SKU is required for trusted workspace access (FB1); Trial does not work.
if [ -n "$NEWCAP" ]; then
  CAP="$NEWCAP"
  r="$(az provider show -n Microsoft.Fabric --query registrationState -o tsv 2>/dev/null || echo NotRegistered)"
  if [ "$r" = Registered ]; then ok "Microsoft.Fabric provider registered"; else change "register Microsoft.Fabric resource provider (free)"; if [ "$APPLY" = 1 ]; then az provider register -n Microsoft.Fabric --wait; fi; fi
  if az resource show -g "$RG" -n "$CAP" --resource-type Microsoft.Fabric/capacities >/dev/null 2>&1; then ok "capacity $CAP exists"; else
    change "create capacity $CAP F2 in $RG/$REGION, admin = $(echo "$CAP_ADMIN" | sed -E 's/^[^@#]+/***/') (billing starts; pause when idle, FB18/FB19)"
    if [ "$APPLY" = 1 ]; then
      az rest --method put --url "https://management.azure.com/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.Fabric/capacities/$CAP?api-version=2023-11-01" \
        --body "{\"location\":\"$REGION\",\"sku\":{\"name\":\"F2\",\"tier\":\"Fabric\"},\"properties\":{\"administration\":{\"members\":[\"$CAP_ADMIN\"]}},\"tags\":{\"purpose\":\"fabric-adls-poc\"}}" -o none
      for i in $(seq 1 40); do s="$(az resource show -g "$RG" -n "$CAP" --resource-type Microsoft.Fabric/capacities --query properties.state -o tsv)"; [ "$s" = Active ] && break; sleep 15; done
      [ "$s" = Active ] || die "capacity state $s"
    fi
  fi
fi
[ -n "$CAP" ] || die "--capacity <existing F capacity> or --create-capacity <name> --region <loc> is required"
SKU="$(az resource list --resource-type Microsoft.Fabric/capacities --query "[?name=='$CAP'].sku.name | [0]" -o tsv)"
[ -n "$SKU" ] || SKU="$(fab /capacities 2>/dev/null | j "next((c['sku'] for c in d['value'] if c['displayName']=='$CAP'),'')" 2>/dev/null || true)"   # capacity in another subscription
case "$SKU" in F*) ok "capacity $CAP is an F SKU ($SKU)";; "") [ "$APPLY" = 1 ] && die "capacity $CAP not found";; *) die "capacity $CAP is $SKU, not an F SKU";; esac
if [ "$CAP_ONLY" = 1 ]; then
  M="$([ "$APPLY" = 1 ] && echo apply || echo plan)"
  { echo "mode=$M (capacity only) changes=${#PLAN[@]}"; printf '%s\n' "${PLAN[@]:-none}"
    az resource show -g "$RG" -n "$CAP" --resource-type Microsoft.Fabric/capacities --query "{name:name,sku:sku.name,state:properties.state,location:location}" -o json 2>/dev/null || echo '{"capacity":"not created"}'
  } | save "B1-capacity-$M.txt"
  if [ "$APPLY" = 1 ]; then python3 "$ST" agent B running "capacity $CAP Active; next: capacity admin adds the owner as contributor (UI), then B without --capacity-only" >/dev/null; fi
  echo "== ${#PLAN[@]} change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed') (capacity only)"
  echo "Next, as the capacity admin in an InPrivate window (you type the password and MFA yourself):"
  echo "  app.fabric.microsoft.com > Settings > Admin portal > Capacity settings > Fabric capacity > $CAP > Contributor permissions > Specific users > your account > Apply"
  echo "Then re-run this command without --capacity-only."
  exit 0
fi
# capacity id: from the Fabric API when you are a capacity admin/contributor, else from the Power BI admin API
# (you are a Fabric administrator, FB25; "Admin - Capacities AssignWorkspacesToCapacity: the user must be a Fabric administrator").
PBI="https://analysis.windows.net/powerbi/api"
cap_id_member() { fab /capacities 2>/dev/null | j "[c['id'].lower() for c in d['value'] if c['displayName']=='$CAP'][0] if any(c['displayName']=='$CAP' for c in d['value']) else ''" 2>/dev/null || true; }
cap_id_admin() { az rest --method get --resource "$PBI" --url "https://api.powerbi.com/v1.0/myorg/admin/capacities" --query "value[?displayName=='$CAP'].id | [0]" -o tsv 2>/dev/null | tr '[:upper:]' '[:lower:]' || true; }
CAP_ID="$(cap_id_member)"; CAP_ACCESS=member
[ -n "$CAP_ID" ] || { CAP_ID="$(cap_id_admin)"; CAP_ACCESS=fabric-admin; }
if [ "$APPLY" = 1 ] && [ -z "$CAP_ID" ]; then  # a new capacity can take a few minutes to appear in Fabric
  for i in $(seq 1 20); do sleep 15; CAP_ID="$(cap_id_member)"; CAP_ACCESS=member; [ -n "$CAP_ID" ] && break; CAP_ID="$(cap_id_admin)"; CAP_ACCESS=fabric-admin; [ -n "$CAP_ID" ] && break; done
  [ -n "$CAP_ID" ] || die "capacity $CAP is not visible in Fabric (neither as capacity member nor through the admin API)"
fi
[ -n "$CAP_ID" ] && echo "        capacity visible to you as: $CAP_ACCESS"

# 2. workspace on that capacity
WS_ID="$(fab /workspaces | j "[w['id'] for w in d['value'] if w['displayName']=='$WS'][0] if any(w['displayName']=='$WS' for w in d['value']) else ''")"
if [ -n "$WS_ID" ]; then ok "workspace $WS exists"; else
  change "create workspace $WS on $CAP"
  if [ "$APPLY" = 1 ]; then
    if [ "$CAP_ACCESS" = member ]; then WS_ID="$(fab /workspaces POST --body "{\"displayName\":\"$WS\",\"capacityId\":\"$CAP_ID\"}" | j "d['id']")"
    else WS_ID="$(fab /workspaces POST --body "{\"displayName\":\"$WS\"}" | j "d['id']")"; fi   # assigned below through the admin API
  fi
fi
if [ -n "$WS_ID" ]; then
  wcap() { fab "/workspaces/$WS_ID" | j "(d.get('capacityId') or '').lower()"; }
  if [ -n "$CAP_ID" ] && [ "$(wcap)" = "$CAP_ID" ]; then ok "workspace on $CAP"; else
    change "assign workspace to $CAP ($([ "$CAP_ACCESS" = member ] && echo 'Fabric API' || echo 'Power BI admin API, as Fabric administrator'))"
    if [ "$APPLY" = 1 ]; then
      if [ "$CAP_ACCESS" = member ]; then fab "/workspaces/$WS_ID/assignToCapacity" POST --body "{\"capacityId\":\"$CAP_ID\"}" -o none
      else az rest --method post --resource "$PBI" --url "https://api.powerbi.com/v1.0/myorg/admin/capacities/AssignWorkspaces" \
             --body "{\"capacityMigrationAssignments\":[{\"targetCapacityObjectId\":\"$CAP_ID\",\"workspacesToAssign\":[\"$WS_ID\"]}]}" -o none; fi
      for i in $(seq 1 12); do [ "$(wcap)" = "$CAP_ID" ] && break; sleep 15; done
      [ "$(wcap)" = "$CAP_ID" ] || die "workspace is not on $CAP after assignment"
      echo "        done"
    fi
  fi
  python3 "$ST" set env.fabricWorkspace "$WS" >/dev/null; python3 "$ST" set env.fabricWorkspaceId "$WS_ID" >/dev/null; python3 "$ST" set env.fabricCapacity "$CAP ($SKU)" >/dev/null
fi

# 3. workspace identity (+ Contributor in the workspace, FB3)
SP_ID=""
[ -n "$WS_ID" ] && SP_ID="$(fab "/workspaces/$WS_ID" | j "(d.get('workspaceIdentity') or {}).get('servicePrincipalId','')")"
if [ -n "$SP_ID" ]; then ok "workspace identity exists"; else
  change "create workspace identity"
  if [ "$APPLY" = 1 ]; then
    fab "/workspaces/$WS_ID/provisionIdentity" POST -o none || true
    for i in $(seq 1 30); do SP_ID="$(fab "/workspaces/$WS_ID" | j "(d.get('workspaceIdentity') or {}).get('servicePrincipalId','')")"; [ -n "$SP_ID" ] && break; sleep 10; done
    [ -n "$SP_ID" ] || die "workspace identity not provisioned (FB: if state Failed, wait 1 h, delete, wait 5 min, retry)"
  fi
fi
if [ -n "$SP_ID" ]; then
  if fab "/workspaces/$WS_ID/roleAssignments" | j "any(r['principal']['id']=='$SP_ID' and r['role'] in ('Admin','Member','Contributor') for r in d['value'])" | grep -q True; then ok "identity has a workspace role"; else
    change "workspace identity = Contributor in $WS"
    if [ "$APPLY" = 1 ]; then fab "/workspaces/$WS_ID/roleAssignments" POST --body "{\"principal\":{\"id\":\"$SP_ID\",\"type\":\"ServicePrincipal\"},\"role\":\"Contributor\"}" -o none; fi
  fi
fi

# 3b. POC container (isolated from the real-data containers; ARM create, not blocked by the firewall)
if az storage container-rm show --storage-account "$STG" -g "$RG" -n "$CONTAINER" >/dev/null 2>&1; then ok "container $CONTAINER exists"; else
  change "create container $CONTAINER in $STG (empty, private, for the POC only)"
  if [ "$APPLY" = 1 ]; then az storage container-rm create --storage-account "$STG" -g "$RG" -n "$CONTAINER" --public-access off -o none; fi
fi
# 4. RBAC, least privilege (FB3): Delegator at account + Data Contributor on the container
rbac() { # rbac <principalId> <type> <role> <scope> <label>
  if [ -z "$1" ]; then change "$5 (after the identity exists)"; return; fi
  n="$(az role assignment list --scope "$4" --query "[?principalId=='$1' && roleDefinitionName=='$3'] | length(@)" -o tsv 2>/dev/null || echo 0)"
  if [ "$n" != 0 ]; then ok "$5"
  elif ! can Microsoft.Authorization/roleAssignments/write; then
    admin_req "$5$([ "$2" = ServicePrincipal ] && echo ' [optional for the POC: needed only if the shortcut signs in as the workspace identity, the production choice]')" \
      "az role assignment create --assignee-object-id $1 --assignee-principal-type $2 --role \"$3\" --scope $4"
  else change "$5"
    if [ "$APPLY" = 1 ]; then az role assignment create --assignee-object-id "$1" --assignee-principal-type "$2" --role "$3" --scope "$4" -o none; fi
  fi
  return 0
}
rbac "$SP_ID" ServicePrincipal "Storage Blob Delegator" "$STG_ID" "identity: Storage Blob Delegator @ account"
rbac "$SP_ID" ServicePrincipal "Storage Blob Data Contributor" "$CONT_SCOPE" "identity: Storage Blob Data Contributor @ container $CONTAINER"
# the owner needs a data role to upload test files and to browse in the portal with Entra auth
MYROLES="$(az role assignment list --assignee "$ME_OID" --include-groups --include-inherited --scope "$CONT_SCOPE" --query "[?contains(roleDefinitionName,'Storage Blob Data')].roleDefinitionName" -o tsv 2>/dev/null | sort -u | tr '\n' ' ' || true)"
if [ -n "$MYROLES" ]; then ok "you: $MYROLES(direct or through a group) on $CONTAINER"; else
  rbac "$ME_OID" User "Storage Blob Data Contributor" "$CONT_SCOPE" "you: Storage Blob Data Contributor @ container $CONTAINER"
fi

# 5. resource instance rule for this workspace (FB4). CLI = same Storage RP update as the documented PowerShell cmdlet.
RID="/subscriptions/00000000-0000-0000-0000-000000000000/resourcegroups/Fabric/providers/Microsoft.Fabric/workspaces/$WS_ID"
if [ -n "$WS_ID" ] && az storage account show -n "$STG" -g "$RG" --query "networkRuleSet.resourceAccessRules[].resourceId" -o tsv | grep -qix "$RID"; then ok "resource instance rule for the workspace"; else
  if [ -n "$WS_ID" ] && ! can Microsoft.Storage/storageAccounts/write; then
    admin_req "resource instance rule for Fabric workspace $WS ($WS_ID) on $STG (trusted workspace access, FB4/FB32)" \
      "az storage account network-rule add -g $RG --account-name $STG --resource-id $RID --tenant-id $TENANT"
  else
  change "resource instance rule Microsoft.Fabric/workspaces/<workspace id> (tenant $(echo "$TENANT" | mask))"
  if [ "$APPLY" = 1 ]; then
    python3 "$ST" lock storage-network-rules B
    az storage account network-rule add -g "$RG" --account-name "$STG" --resource-id "$RID" --tenant-id "$TENANT" -o none
  fi
  fi
fi

{
  echo "mode=$([ "$APPLY" = 1 ] && echo apply || echo plan) changes=${#PLAN[@]}"; printf '%s\n' "${PLAN[@]:-none}"
  [ -n "$WS_ID" ] && fab "/workspaces/$WS_ID" || true
  [ -n "$SP_ID" ] && az role assignment list --all --query "[?principalId=='$SP_ID'].{role:roleDefinitionName,scope:scope}" -o json || true
  az storage account show -n "$STG" -g "$RG" --query "networkRuleSet.resourceAccessRules" -o json
} | save "B-$([ "$APPLY" = 1 ] && echo apply || echo plan).txt"
if [ "$APPLY" = 1 ]; then
  if [ -s "$ADMIN_REQ" ]; then python3 "$ST" agent B blocked "applied what the owner may; waiting on storage admins: $(grep -c '^- ' "$ADMIN_REQ") request(s)" >/dev/null
  else python3 "$ST" agent B done "identity, RBAC, resource rule in place" >/dev/null; fi
fi
echo "== ${#PLAN[@]} change(s) $([ "$APPLY" = 1 ] && echo applied || echo 'planned; nothing changed')"
admin_req_show "B-admin-request.txt"
if [ "${CAP_ACCESS:-member}" != member ]; then
  echo "NOTE for wave 2: you are not a capacity admin/contributor on $CAP. The capacity admin should add you once:"
  echo "     app.fabric.microsoft.com > Settings > Admin portal > Capacity settings > Fabric capacity > $CAP > Contributor permissions > Specific users > you > Apply"
fi
