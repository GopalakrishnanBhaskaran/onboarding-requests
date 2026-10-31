#!/usr/bin/env bash
# Automatic identity management (AIM) for Azure Databricks: the account reads users, groups, nested groups and service
# principals straight from Microsoft Entra ID, with no SCIM app. Runs in Azure Cloud Shell (Bash).
#
#   aim_setup.sh plan   --account-id <id> [--workspace <name|id> --group <name|object id>...]
#   aim_setup.sh apply  --account-id <id> --workspace <name|id> --group <name|object id>... [--admin] --approved-by "<name> <date>"
#   aim_setup.sh status --account-id <id> --workspace <name|id>
#   aim_setup.sh grants --workspace <name> --catalog <c> --schema <s> --group <name|object id>[=READ|=WRITE]... [--approved-by "<name> <date>"]
#   aim_setup.sh self-test                                   offline, against mocked Azure CLI and Databricks
#   Option for all: --subscription <id> (where the workspaces are; default: the Azure CLI's current one)
#
# AIM itself is switched on in the account console only: Microsoft Learn documents no API, CLI or Terraform setting for it
# (F110). This script does what the documented APIs allow around that switch:
#   plan   changes nothing: proves you are an admin of that account (one read), lists the workspaces and any Entra SCIM
#          connector apps, prints the console steps (readiness report, the AIM toggle) and what apply would assign.
#   apply  after the toggle: adds each Entra group to the account through the external group API, which fails while AIM is
#          off, so the first group is also the proof that AIM works (F111); then assigns it to the workspace as USER, or
#          ADMIN with --admin (F112). A second apply changes only what is missing; it never lowers ADMIN to USER.
#   status who is assigned to the workspace.
#   grants Unity Catalog data access for the groups (workspace access is not data access). READ = USE_CATALOG on the catalog
#          + USE_SCHEMA, SELECT on the schema (all its current and future tables); WRITE adds MODIFY, CREATE_TABLE on the
#          schema (F123). Without --approved-by it only reads and prints the plan; with it, it adds what is missing through
#          the Unity Catalog permissions API (no SQL warehouse, so no DBUs; F125) and reads it back. It never removes a
#          privilege and never grants ALL_PRIVILEGES, MANAGE or ownership. Needs MANAGE on the catalog/schema, ownership of
#          it or its catalog, or metastore admin; workspace admins own the workspace catalog (F124). No --account-id.
# Sign-in: the Azure CLI, as a Databricks account admin (F113). The Databricks token stays in memory and never reaches the
# output or a command line (curl gets it through a header file descriptor). Guide: docs/DATABRICKS_AIM.md.
set -euo pipefail

ACCOUNTS="https://accounts.azuredatabricks.net"
DBX_RESOURCE="2ff814a6-3304-4ab8-85cb-cd0e6f879c1d"                    # Azure Databricks resource id (F113)
SCIM_GALLERY_APP="Azure Databricks SCIM Provisioning Connector"
GUID_RE='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
DBX_TOKEN="" ACCOUNT="" WORKSPACE="" APPROVED_BY="" PERMISSION="USER" CATALOG="" SCHEMA=""
GROUP_ARGS=() SUB_ARGS=()

say() { printf '%s\n' "$*"; }
mask() { local s="$1"; [[ -n "$DBX_TOKEN" ]] && s="${s//"$DBX_TOKEN"/******}"; printf '%s' "$s"; }
die() { printf 'ERROR: %s\n' "$(mask "$*")" >&2; exit 1; }
urlenc() { jq -rn --arg v "$1" '$v|@uri'; }
odata() { printf '%s' "${1//\'/\'\'}"; }
body() { cat "$WORK/body"; }
http_status() { cat "$WORK/status"; }
api_error() { jq -r '.message // .detail // .error.message // .error_code // .' "$WORK/body" 2>/dev/null | head -c 400; }
is_guid() { [[ "$1" =~ $GUID_RE ]]; }

# Azure CLI calls go through azq so the self-test can replace them.
azq() { if [[ -n "${AIM_SETUP_MOCK:-}" ]]; then mock_az "$@"; else az "$@"; fi; }

# One Databricks call: call METHOD URL [body]. A request body comes on stdin; the response goes to $WORK/body, the status to
# $WORK/status; returns 0 on 2xx.
call() {
  local method="$1" url="$2" with_body="${3:-}" status
  if [[ -n "${AIM_SETUP_MOCK:-}" ]]; then mock_call "$@"; return; fi
  if [[ "$with_body" == body ]]; then
    status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" "$url" \
      -H @<(printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$DBX_TOKEN") --data-binary @-) || status=000
  else
    status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" "$url" -H @<(printf 'Authorization: Bearer %s\n' "$DBX_TOKEN")) || status=000
  fi
  printf '%s' "$status" > "$WORK/status"
  [[ "$status" == 2* ]]
}

# ---- sign-in and lookups ------------------------------------------------------------------------------------------------

dbx_login() {
  DBX_TOKEN=$(azq account get-access-token --resource "$DBX_RESOURCE" --query accessToken -o tsv 2>/dev/null || true)
  [[ -n "$DBX_TOKEN" ]] || die "the Azure CLI gave no Azure Databricks token: run az login (as a Databricks account admin) and try again"
}

# One read of the account SCIM API: proves the token belongs to an admin of this account.
check_account_admin() {
  if call GET "$ACCOUNTS/api/2.1/accounts/$ACCOUNT/scim/v2/Users?count=1"; then
    say "Databricks account $ACCOUNT: you are an account admin ($(jq -r '.totalResults // "?"' "$WORK/body") users in the account)."
  else
    case "$(http_status)" in
      401) die "Databricks did not accept your sign-in (401): sign in to the account's Entra tenant (az login --tenant <tenant id>)." ;;
      403) die "you are not an account admin of $ACCOUNT (403). An account admin runs this script." ;;
      404) die "no Databricks account $ACCOUNT (404): copy the account id from the account console (your user menu, top right)." ;;
      *) die "the account check failed ($(http_status)): $(api_error)" ;;
    esac
  fi
}

workspace_id() {  # a workspace name in the subscription, or its numeric id → the numeric workspace id
  local w="$1" ids n
  if [[ "$w" =~ ^[0-9]+$ ]]; then printf '%s' "$w"; return; fi
  mapfile -t ids < <(azq resource list --resource-type Microsoft.Databricks/workspaces ${SUB_ARGS[@]+"${SUB_ARGS[@]}"} --query "[?name=='$w'].id" -o tsv)
  n=${#ids[@]}
  (( n == 1 )) || die "workspace \"$w\": $n found in this subscription (use --subscription, or the numeric workspace id from the URL adb-<id>.<n>.azuredatabricks.net)"
  azq resource show --ids "${ids[0]}" --query properties.workspaceId -o tsv
}

entra_group() {  # → "objectId<TAB>displayName"
  local out
  out=$(azq ad group show --group "$1" --query "{id:id,name:displayName}" -o json 2>&1) || die "Entra group \"$1\": $(head -c 300 <<<"$out")"
  jq -r '"\(.id)\t\(.name)"' <<<"$out"
}

scim_connectors() {  # Entra apps made from the SCIM gallery app (run both in parallel until AIM is validated, F110)
  local tmpl
  tmpl=$(azq rest --method GET --url "https://graph.microsoft.com/v1.0/applicationTemplates?\$filter=$(urlenc "displayName eq '$(odata "$SCIM_GALLERY_APP")'")&\$select=id" --query "value[0].id" -o tsv 2>/dev/null) || return 0
  [[ -n "$tmpl" ]] || return 0
  azq rest --method GET --url "https://graph.microsoft.com/v1.0/servicePrincipals?\$filter=$(urlenc "applicationTemplateId eq '$tmpl'")&\$select=displayName" --query "value[].displayName" -o tsv 2>/dev/null || true
}

assignments() {  # the workspace's assignments as "principal_id<TAB>name<TAB>PERM,PERM"
  call GET "$ACCOUNTS/api/2.0/accounts/$ACCOUNT/workspaces/$1/permissionassignments" \
    || die "reading the assignments of workspace $1 failed ($(http_status)): $(api_error) (is the workspace identity-federated?)"
  jq -r '.permission_assignments[]? | select(.principal) | "\(.principal.principal_id)\t\(.principal.group_name // .principal.user_name // .principal.service_principal_name // .principal.display_name)\t\((.permissions // []) | join(","))"' "$WORK/body"
}

# Adds the Entra group to the account (or refreshes it) and returns its Databricks id. Fails while AIM is off (F111).
external_group() {
  if call GET "$ACCOUNTS/api/2.0/identity/accounts/$ACCOUNT/external-groups/$1"; then jq -r '.internal_id' "$WORK/body"; return; fi
  die "Databricks could not read Entra group $1 ($(http_status)): $(api_error).
  Most likely automatic identity management is not on yet: account console > Security > Identity provider setup >
  Automatic identity management > Enabled, wait 5-10 minutes, then run apply again."
}

# ---- commands -----------------------------------------------------------------------------------------------------------

console_steps() {
  say "In the account console (https://accounts.azuredatabricks.net, as an account admin), Security > Identity provider setup (F110):"
  say "  1. Identity management: start a readiness report scan and fix what it lists (needed if SCIM or manual groups were used)."
  say "  2. Toggle Automatic identity management to Enabled; it takes 5-10 minutes. (Learn documents no API for this switch.)"
  say "  Needs: Premium tier, one Entra tenant (no cross-tenant identities), at least one identity-federated workspace."
}

plan_or_apply() {
  local mode="$1" ws="" line oid name pid perms current connectors
  is_guid "$ACCOUNT" || die "--account-id must be the Databricks account id (a GUID; account console > your user menu)"
  [[ "$PERMISSION" == USER || "$PERMISSION" == ADMIN ]] || die "permission must be USER or ADMIN"
  if [[ "$mode" == apply ]]; then
    [[ -n "$WORKSPACE" && ${#GROUP_ARGS[@]} -gt 0 ]] || die "apply needs --workspace and at least one --group"
    [[ -n "$APPROVED_BY" ]] || die "apply gives groups access to a workspace: run plan, get the approval, then add --approved-by \"<name> <date>\""
  fi
  dbx_login
  check_account_admin

  if [[ "$mode" == plan ]]; then
    say ""; say "Workspaces in this subscription (name, resource group, tier):"
    azq resource list --resource-type Microsoft.Databricks/workspaces ${SUB_ARGS[@]+"${SUB_ARGS[@]}"} --query "[].[name, resourceGroup, sku.name]" -o tsv | sed 's/^/  /' || true
    connectors=$(scim_connectors)
    if [[ -n "$connectors" ]]; then
      say ""; say "SCIM connector apps in Entra (keep them running next to AIM until AIM is validated; then pause them only if"
      say "there are no nested groups and the readiness report is clean: tools/databricks/entra_scim_setup.sh pause):"
      sed 's/^/  /' <<<"$connectors"
    fi
    say ""; console_steps
  fi

  [[ -z "$WORKSPACE" ]] && { say ""; say "No --workspace given: nothing to assign. Plan only: nothing changed."; return; }
  ws=$(workspace_id "$WORKSPACE")
  current=$(assignments "$ws")
  local groups=()
  for g in "${GROUP_ARGS[@]}"; do groups+=("$(entra_group "$g")"); done

  say ""; say "Workspace $WORKSPACE (id $ws), groups as $PERMISSION:"
  for line in ${groups[@]+"${groups[@]}"}; do
    oid=${line%%$'\t'*}; name=${line#*$'\t'}
    perms=$(awk -F'\t' -v n="$name" '$2 == n {print $3}' <<<"$current")
    if [[ "$mode" == plan ]]; then
      if [[ ",$perms," == *",ADMIN,"* || ( ",$perms," == *",$PERMISSION,"* ) ]]; then say "  $name: already ${perms} (kept)"
      else say "  $name ($oid): will be added to the account and assigned ${PERMISSION}${perms:+ (now $perms)}"; fi
    fi
  done
  if [[ "$mode" == plan ]]; then
    say ""; say "Plan only: nothing changed. After AIM is on and the plan is approved, run the same command as apply with --approved-by \"<name> <date>\"."
    return
  fi

  say ""; say "Approval recorded: $APPROVED_BY"
  for line in ${groups[@]+"${groups[@]}"}; do
    oid=${line%%$'\t'*}; name=${line#*$'\t'}
    pid=$(external_group "$oid")
    perms=$(awk -F'\t' -v p="$pid" '$1 == p {print $3}' <<<"$current")
    if [[ ",$perms," == *",ADMIN,"* || ",$perms," == *",$PERMISSION,"* ]]; then say "$name: in the account (id $pid), already ${perms} on the workspace: kept."; continue; fi
    jq -nc --arg p "$PERMISSION" '{permissions: [$p]}' \
      | call PUT "$ACCOUNTS/api/2.0/accounts/$ACCOUNT/workspaces/$ws/permissionassignments/principals/$pid" body \
      || die "assigning $name to workspace $ws failed ($(http_status)): $(api_error) (is the workspace identity-federated?)"
    say "$name: in the account (id $pid), assigned $PERMISSION on workspace $ws."
  done
  say ""; say "Done. Members sign in to the workspace and are added on first sign-in (just in time); nested groups' members too."
  say "Check later: $(basename "$0") status --account-id $ACCOUNT --workspace \"$WORKSPACE\""
}

status_cmd() {
  local ws
  is_guid "$ACCOUNT" || die "--account-id must be the Databricks account id (a GUID)"
  [[ -n "$WORKSPACE" ]] || die "status needs --workspace"
  dbx_login
  ws=$(workspace_id "$WORKSPACE")
  say "Workspace $WORKSPACE (id $ws): assignments (principal id, name, permissions):"
  assignments "$ws" | sed 's/^/  /'
}

# ---- Unity Catalog grants (F123-F125) ----------------------------------------------------------------------------------

uc_privileges() {  # LEVEL TYPE → the privileges for that securable; READ and WRITE are the only levels
  case "$1:$2" in
    READ:catalog|WRITE:catalog) printf 'USE_CATALOG' ;;
    READ:schema) printf 'USE_SCHEMA,SELECT' ;;
    WRITE:schema) printf 'USE_SCHEMA,SELECT,MODIFY,CREATE_TABLE' ;;
    *) die "access level must be READ or WRITE" ;;
  esac
}

workspace_url() {  # a workspace name in the subscription → its host (adb-<id>.<n>.azuredatabricks.net)
  local ids n
  mapfile -t ids < <(azq resource list --resource-type Microsoft.Databricks/workspaces ${SUB_ARGS[@]+"${SUB_ARGS[@]}"} --query "[?name=='$1'].id" -o tsv)
  n=${#ids[@]}
  (( n == 1 )) || die "workspace \"$1\": $n found in this subscription (grants needs the workspace name; use --subscription if it is elsewhere)"
  azq resource show --ids "${ids[0]}" --query properties.workspaceUrl -o tsv
}

uc_fail() {
  case "$(http_status)" in
    401) die "$1 failed (401): the workspace did not accept your sign-in (az login to the workspace's tenant)." ;;
    403) die "$1 failed (403): $(api_error). You need MANAGE on it, ownership of it or of its catalog, or metastore admin (workspace admins own the workspace catalog, F124)." ;;
    404) die "$1 failed (404): $(api_error). Check the catalog and schema names in Catalog Explorer." ;;
    *) die "$1 failed ($(http_status)): $(api_error)" ;;
  esac
}

uc_current() {  # HOST TYPE FULL_NAME PRINCIPAL → that principal's direct privileges on it, comma-separated
  call GET "https://$1/api/2.1/unity-catalog/permissions/$2/$(urlenc "$3")?principal=$(urlenc "$4")" || uc_fail "reading the grants on $2 $3"
  jq -r --arg p "$4" '[.privilege_assignments[]? | select(.principal == $p) | .privileges[]?] | unique | join(",")' "$WORK/body"
}

missing_privs() {  # WANTED HAVE → the wanted privileges not in HAVE, comma-separated
  local w p out=()
  IFS=, read -ra w <<<"$1"
  for p in "${w[@]}"; do [[ ",$2," == *",$p,"* ]] || out+=("$p"); done
  local IFS=,; printf '%s' "${out[*]-}"
}

grants_cmd() {
  local host g level line name row type full wanted have miss fails=0 rows=()
  [[ -n "$WORKSPACE" && -n "$CATALOG" && -n "$SCHEMA" && ${#GROUP_ARGS[@]} -gt 0 ]] \
    || die "grants needs --workspace <name>, --catalog, --schema and at least one --group <name|object id>[=READ|=WRITE]"
  [[ "$WORKSPACE" =~ ^[0-9]+$ ]] && die "grants needs the workspace name, not its id"
  for g in "${GROUP_ARGS[@]}"; do
    level=READ
    case "${g^^}" in
      *=READ) g=${g%=*} ;;
      *=WRITE) level=WRITE; g=${g%=*} ;;
      *=*) die "group \"$g\": the access level is READ or WRITE only (never ALL_PRIVILEGES, MANAGE or ownership)" ;;
    esac
    rows+=("$g"$'\t'"$level")
  done
  dbx_login
  host=$(workspace_url "$WORKSPACE")
  [[ -n "$host" ]] || die "workspace \"$WORKSPACE\" has no URL yet"
  local resolved=()
  for row in "${rows[@]}"; do line=$(entra_group "${row%%$'\t'*}"); resolved+=("${line#*$'\t'}"$'\t'"${row#*$'\t'}"); done

  say "Workspace $WORKSPACE ($host): Unity Catalog grants on catalog $CATALOG and schema $CATALOG.$SCHEMA"
  [[ -n "$APPROVED_BY" ]] && say "Approval recorded: $APPROVED_BY"
  for row in "${resolved[@]}"; do
    name=${row%%$'\t'*}; level=${row#*$'\t'}
    for type in catalog schema; do
      full=$CATALOG; [[ $type == schema ]] && full="$CATALOG.$SCHEMA"
      wanted=$(uc_privileges "$level" "$type")
      have=$(uc_current "$host" "$type" "$full" "$name")
      miss=$(missing_privs "$wanted" "$have")
      if [[ -z "$miss" ]]; then say "  $name: $type $full already has $wanted (kept)"; continue; fi
      if [[ -z "$APPROVED_BY" ]]; then say "  $name ($level): $type $full will get $miss${have:+ (now $have)}"; continue; fi
      jq -nc --arg p "$name" --arg a "$miss" '{changes: [{principal: $p, add: ($a | split(","))}]}' \
        | call PATCH "https://$host/api/2.1/unity-catalog/permissions/$type/$(urlenc "$full")" body \
        || uc_fail "granting $miss on $type $full to $name"
      have=$(uc_current "$host" "$type" "$full" "$name")
      if [[ -z "$(missing_privs "$wanted" "$have")" ]]; then say "  PASS $name: $type $full has $have (added $miss)"
      else say "  FAIL $name: $type $full has ${have:-nothing}, expected $wanted"; fails=$((fails + 1)); fi
    done
  done
  if [[ -z "$APPROVED_BY" ]]; then
    say "Plan only: nothing changed. After the approval, run the same command with --approved-by \"<name> <date>\"."
    return
  fi
  (( fails == 0 )) || die "$fails grant(s) not confirmed by the read-back"
  say "Done. Members get the access at their next query; the group membership itself comes from Entra (automatic identity management)."
}

# ---- offline self-test (mocked Azure CLI and Databricks) --------------------------------------------------------------------

mock_az() {
  local m="$AIM_SETUP_MOCK" a="$*"
  printf 'az %s\n' "$a" >> "$m/calls.log"
  case "$a" in
    "account get-access-token"*) printf 'mock-DBX-token-91c2\n' ;;
    "resource list"*"?name=='ws-sample'"*) printf '/subscriptions/s1/resourceGroups/rg-sample/providers/Microsoft.Databricks/workspaces/ws-sample\n' ;;
    "resource list"*"?name=="*) ;;
    "resource list"*) printf 'ws-sample\trg-sample\tpremium\n' ;;
    "resource show"*workspaceUrl*) printf 'adb-1234567890123456.7.azuredatabricks.net\n' ;;
    "resource show"*) printf '1234567890123456\n' ;;
    "ad group show --group SAMPLE-GRP-DBX-USERS"*) printf '{"id":"aaaaaaaa-0000-4000-8000-000000000001","name":"SAMPLE-GRP-DBX-USERS"}\n' ;;
    "ad group show --group SAMPLE-GRP-DBX-ADMINS"*) printf '{"id":"aaaaaaaa-0000-4000-8000-000000000002","name":"SAMPLE-GRP-DBX-ADMINS"}\n' ;;
    "ad group show"*) printf 'ERROR: Resource does not exist\n'; return 1 ;;
    "rest --method GET --url https://graph.microsoft.com/v1.0/applicationTemplates"*) printf 'tmpl-1\n' ;;
    "rest --method GET --url https://graph.microsoft.com/v1.0/servicePrincipals"*) printf 'SAMPLE account SCIM\n' ;;
    *) printf 'mock az: no route for %s\n' "$a" >&2; return 1 ;;
  esac
}

mock_call() {
  local method="$1" url="$2" with_body="${3:-}" m="$AIM_SETUP_MOCK" in="" code=200 out='{}' path
  [[ "$with_body" == body ]] && in=$(cat)
  path="${url#"$ACCOUNTS"}"; path="${path#https://adb-1234567890123456.7.azuredatabricks.net}"
  printf 'dbx %s %s %s\n' "$method" "$path" "$in" >> "$m/calls.log"
  case "$method $path" in
    "GET /api/2.1/accounts/"*"/scim/v2/Users?count=1") out='{"totalResults":5}' ;;
    "GET /api/2.0/accounts/"*"/workspaces/1234567890123456/permissionassignments")
      out=$(jq -Rn '{permission_assignments: [inputs | select(length > 0) | split("\t") | {principal: {principal_id: (.[0] | tonumber), group_name: .[1]}, permissions: (.[2] | split(","))}]}' \
        < <(cat "$m/assigned" 2>/dev/null || true)) ;;
    "GET /api/2.0/identity/accounts/"*"/external-groups/"*)
      if [[ -f "$m/aim_off" ]]; then code=400; out='{"error_code":"INVALID_STATE","message":"mock: account is not onboarded onto Automatic Identity Management"}'
      else case "$path" in *000000000001) out='{"internal_id":"9001","display_name":"SAMPLE-GRP-DBX-USERS"}' ;; *) out='{"internal_id":"9002","display_name":"SAMPLE-GRP-DBX-ADMINS"}' ;; esac; fi ;;
    "PUT /api/2.0/accounts/"*"/permissionassignments/principals/"*)
      local pid=${path##*/} name; name=$([[ $pid == 9001 ]] && echo SAMPLE-GRP-DBX-USERS || echo SAMPLE-GRP-DBX-ADMINS)
      { grep -v "^$pid	" "$m/assigned" 2>/dev/null || true; printf '%s\t%s\t%s\n' "$pid" "$name" "$(jq -r '.permissions | join(",")' <<<"$in")"; } > "$m/assigned.new"
      mv "$m/assigned.new" "$m/assigned" ;;
    "GET /api/2.1/unity-catalog/permissions/"*|"PATCH /api/2.1/unity-catalog/permissions/"*)
      local rest=${path#/api/2.1/unity-catalog/permissions/} type name principal=""
      type=${rest%%/*}; rest=${rest#*/}; name=${rest%%\?*}
      [[ "$rest" == *"?principal="* ]] && principal=${rest#*principal=}
      if [[ -f "$m/uc_forbidden" ]]; then code=403; out='{"error_code":"PERMISSION_DENIED","message":"mock: User does not have MANAGE on Catalog"}'
      elif [[ "$name" == nope* ]]; then code=404; out='{"error_code":"CATALOG_DOES_NOT_EXIST","message":"mock: Catalog does not exist"}'
      elif [[ "$method" == GET ]]; then
        out=$(awk -F'\t' -v t="$type" -v n="$name" -v p="$principal" '$1 == t && $2 == n && (p == "" || $3 == p) {print $3 "\t" $4}' "$m/uc" 2>/dev/null \
          | jq -Rn '{privilege_assignments: [inputs | select(length > 0) | split("\t") | {principal: .[0], privileges: (.[1] | split(","))}]}')
      else
        principal=$(jq -r '.changes[0].principal' <<<"$in")
        local have add
        have=$(awk -F'\t' -v t="$type" -v n="$name" -v p="$principal" '$1 == t && $2 == n && $3 == p {print $4}' "$m/uc" 2>/dev/null)
        add=$(jq -r --arg h "$have" '[($h | split(",") | map(select(length > 0)))[], .changes[0].add[]] | unique | join(",")' <<<"$in")
        { awk -F'\t' -v t="$type" -v n="$name" -v p="$principal" '!($1 == t && $2 == n && $3 == p)' "$m/uc" 2>/dev/null || true
          printf '%s\t%s\t%s\t%s\n' "$type" "$name" "$principal" "$add"; } > "$m/uc.new"
        mv "$m/uc.new" "$m/uc"
      fi ;;
    *) code=404; out='{"message":"mock: no route"}' ;;
  esac
  printf '%s' "$out" > "$WORK/body"; printf '%s' "$code" > "$WORK/status"
  [[ "$code" == 2* ]]
}

# The checks are single-quoted on purpose (eval expands them); the trap path is expanded when set because m is local.
# shellcheck disable=SC2016,SC2034,SC2064
self_test() {
  local m out all="" fails=0 acc="aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee" token="mock-DBX-token-91c2"
  local G=(--group SAMPLE-GRP-DBX-USERS --group SAMPLE-GRP-DBX-ADMINS)
  check() { if eval "$2"; then say "  PASS $1"; else say "  FAIL $1"; fails=$((fails + 1)); fi; }
  run() { local o; o=$(AIM_SETUP_MOCK="$m" bash "$0" "$@" 2>&1) && rc=0 || rc=$?; all+="$o"; printf '%s' "$o"; return "$rc"; }
  puts() { grep -c '^dbx PUT ' "$m/calls.log" || true; }
  m=$(mktemp -d); trap "rm -rf '$WORK' '$m'" EXIT
  say "self-test:"
  check "account id must be a GUID" '! run plan --account-id not-a-guid >/dev/null'
  out=$(run plan --account-id "$acc" --workspace ws-sample "${G[@]}")
  check "plan: no write and no external-group read (it would add the group)" '[[ "$(puts)" == 0 ]] && ! grep -q external-groups "$m/calls.log"'
  check "plan: console steps, readiness report, SCIM connector noted" '[[ "$out" == *"readiness report"* && "$out" == *"Automatic identity management to Enabled"* && "$out" == *"SAMPLE account SCIM"* ]]'
  check "plan: both groups will be assigned USER" '[[ $(grep -c "will be added to the account and assigned USER" <<<"$out") == 2 ]]'
  check "apply without --approved-by refused" '! run apply --account-id "$acc" --workspace ws-sample "${G[@]}" >/dev/null && [[ "$(puts)" == 0 ]]'
  check "apply without --group refused" '! run apply --account-id "$acc" --workspace ws-sample --approved-by t >/dev/null'
  touch "$m/aim_off"
  out=$(run apply --account-id "$acc" --workspace ws-sample "${G[@]}" --approved-by "SAMPLE 2026-10-31" || true)
  check "apply while AIM is off: stops with the console step, nothing assigned" '[[ "$out" == *"not on yet"* && "$(puts)" == 0 ]]'
  rm "$m/aim_off"
  out=$(run apply --account-id "$acc" --workspace ws-sample "${G[@]}" --approved-by "SAMPLE 2026-10-31")
  check "apply: both groups resolved and assigned USER" '[[ $(grep -cE "^dbx PUT .*/principals/900[12] \{\"permissions\":\[\"USER\"\]\}$" "$m/calls.log") == 2 ]]'
  : > "$m/calls.log"
  out=$(run apply --account-id "$acc" --workspace ws-sample "${G[@]}" --approved-by "SAMPLE 2026-10-31")
  check "second apply: nothing re-assigned" '[[ "$(puts)" == 0 && $(grep -c "already USER" <<<"$out") == 2 ]]'
  out=$(run apply --account-id "$acc" --workspace ws-sample --group SAMPLE-GRP-DBX-ADMINS --admin --approved-by "SAMPLE 2026-10-31")
  check "--admin raises USER to ADMIN" '[[ "$(puts)" == 1 ]] && grep -q "principals/9002.*ADMIN" "$m/calls.log"'
  : > "$m/calls.log"
  out=$(run apply --account-id "$acc" --workspace ws-sample --group SAMPLE-GRP-DBX-ADMINS --approved-by "SAMPLE 2026-10-31")
  check "a USER run never lowers ADMIN" '[[ "$(puts)" == 0 && "$out" == *"already ADMIN"* ]]'
  out=$(run status --account-id "$acc" --workspace ws-sample)
  check "status lists both groups" '[[ "$out" == *"SAMPLE-GRP-DBX-USERS"*"USER"* && "$out" == *"SAMPLE-GRP-DBX-ADMINS"*"ADMIN"* ]]'
  check "unknown Entra group refused" '! run plan --account-id "$acc" --workspace ws-sample --group NOPE >/dev/null'
  local U=(--workspace ws-sample --catalog sample_cat --schema sales)
  patches() { grep -c '^dbx PATCH ' "$m/calls.log" || true; }
  : > "$m/calls.log"
  check "grants without --schema refused" '! run grants --workspace ws-sample --catalog sample_cat --group SAMPLE-GRP-DBX-USERS >/dev/null'
  check "grants with any level but READ/WRITE refused, nothing sent" '! run grants "${U[@]}" --group SAMPLE-GRP-DBX-USERS=MANAGE >/dev/null && [[ "$(patches)" == 0 ]]'
  out=$(run grants "${U[@]}" --group SAMPLE-GRP-DBX-USERS)
  check "grants plan: reads only, lists USE_CATALOG and USE_SCHEMA,SELECT" '[[ "$(patches)" == 0 && "$out" == *"catalog sample_cat will get USE_CATALOG"* && "$out" == *"schema sample_cat.sales will get USE_SCHEMA,SELECT"* && "$out" == *"Plan only"* ]]'
  out=$(run grants "${U[@]}" --group SAMPLE-GRP-DBX-USERS --approved-by "SAMPLE 2026-10-31")
  check "grants READ: two adds (catalog, schema), read back PASS" '[[ "$(patches)" == 2 && $(grep -c "  PASS SAMPLE-GRP-DBX-USERS" <<<"$out") == 2 ]] && grep -q "PATCH /api/2.1/unity-catalog/permissions/schema/sample_cat.sales {\"changes\":\[{\"principal\":\"SAMPLE-GRP-DBX-USERS\",\"add\":\[\"USE_SCHEMA\",\"SELECT\"\]}\]}" "$m/calls.log"'
  : > "$m/calls.log"
  out=$(run grants "${U[@]}" --group SAMPLE-GRP-DBX-USERS --approved-by "SAMPLE 2026-10-31")
  check "second grants run: nothing added" '[[ "$(patches)" == 0 && $(grep -c "already has" <<<"$out") == 2 ]]'
  out=$(run grants "${U[@]}" --group SAMPLE-GRP-DBX-USERS=WRITE --approved-by "SAMPLE 2026-10-31")
  check "READ to WRITE adds only MODIFY,CREATE_TABLE on the schema" '[[ "$(patches)" == 1 ]] && grep -q "\"add\":\[\"MODIFY\",\"CREATE_TABLE\"\]" "$m/calls.log"'
  check "grants never send a remove" '! grep -q "\"remove\"" "$m/calls.log"'
  touch "$m/uc_forbidden"
  out=$(run grants "${U[@]}" --group SAMPLE-GRP-DBX-ADMINS --approved-by "SAMPLE 2026-10-31" || true)
  check "grants without the right: stops and names MANAGE / ownership" '[[ "$out" == *"403"* && "$out" == *"MANAGE"* && "$out" == *"ownership"* ]]'
  rm "$m/uc_forbidden"
  check "the Databricks token never appears in any output" '[[ "$all" != *"$token"* ]]'
  if (( fails == 0 )); then say "self-test passed"; else say "self-test: $fails failed"; exit 1; fi
}

# ---- main -----------------------------------------------------------------------------------------------------------------

CMD="${1:-}"; shift || true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --account-id) ACCOUNT="${2:?account id}"; shift 2 ;;
    --workspace) WORKSPACE="${2:?workspace name or id}"; shift 2 ;;
    --group) GROUP_ARGS+=("${2:?group name or object id}"); shift 2 ;;
    --admin) PERMISSION=ADMIN; shift ;;
    --subscription) SUB_ARGS=(--subscription "${2:?subscription id}"); shift 2 ;;
    --approved-by) APPROVED_BY="${2:?name and date}"; shift 2 ;;
    --catalog) CATALOG="${2:?catalog name}"; shift 2 ;;
    --schema) SCHEMA="${2:?schema name}"; shift 2 ;;
    *) die "unknown argument $1 (see the header of this script)" ;;
  esac
done
case "$CMD" in
  plan|apply) plan_or_apply "$CMD" ;;
  status) status_cmd ;;
  grants) grants_cmd ;;
  self-test) self_test ;;
  *) die "usage: $(basename "$0") plan|apply|status|grants|self-test [options] (see the header of this script)" ;;
esac
