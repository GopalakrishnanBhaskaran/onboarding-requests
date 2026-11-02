#!/usr/bin/env bash
# Automatic identity management (AIM) for Azure Databricks: the account reads users, groups, nested groups and service
# principals straight from Microsoft Entra ID, with no SCIM app. Runs in Azure Cloud Shell (Bash).
#
#   aim_setup.sh plan   --account-id <id> [--workspace <name|id|URL> --group <name|object id>...]
#   aim_setup.sh apply  --account-id <id> --workspace <name|id|URL> --group <name|object id>... [--grant <catalog>[.<schema>]] [--admin] --approved-by "<name> <date>"
#   aim_setup.sh status --account-id <id> --workspace <name|id|URL>
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
#          v1.2 (2 Nov 2026), USER groups only: A4 gives the workspace entitlements the group's NAME stands for (every
#          level: Workspace access; readers: also Databricks SQL access); with --grant, A5 grants that level's Unity Catalog
#          privileges on the catalog and schema (config/databricks-access.json, the same rules as the AI agent). Admin or
#          owner names, names with no or two levels: left to a person. Then the tests: everything read back (PASS/FAIL),
#          appended to .local/databricks-setup.log with a line ready for docs/DEPLOYMENT_LOG.md; any FAIL exits non-zero.
#   status who is assigned to the workspace.
# Sign-in: the Azure CLI, as a Databricks account admin (F113). The Databricks token stays in memory and never reaches the
# output or a command line (curl gets it through a header file descriptor). Guide: docs/DATABRICKS_AIM.md.
set -euo pipefail

ACCOUNTS="https://accounts.azuredatabricks.net"
DBX_RESOURCE="2ff814a6-3304-4ab8-85cb-cd0e6f879c1d"                    # Azure Databricks resource id (F113)
SCIM_GALLERY_APP="Azure Databricks SCIM Provisioning Connector"
GUID_RE='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
DBX_TOKEN="" ACCOUNT="" WORKSPACE="" APPROVED_BY="" PERMISSION="USER" GRANT=""
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
POLICY="$ROOT/config/databricks-access.json"
LOG="${AIM_SETUP_LOG:-$ROOT/.local/databricks-setup.log}"
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

workspace_id() {  # a workspace name in the subscription, its numeric id, or its URL → the numeric workspace id
  local w="$1" ids n
  if [[ "$w" =~ ^[0-9]+$ ]]; then printf '%s' "$w"; return; fi
  # The workspace URL (https://adb-<id>.<n>.azuredatabricks.net/…, with or without https:// or a trailing slash) names the id.
  if [[ "$w" =~ adb-([0-9]+)\.[0-9]+\.azuredatabricks\.net ]]; then printf '%s' "${BASH_REMATCH[1]}"; return; fi
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


# ---- v1.2: entitlements and Unity Catalog by group name (A4, A5) and the tests ------------------------------------------------

# The access a group name stands for, as one JSON line: {level, byAgent, why, entitlements, catalog, schema}. Same rules as
# DatabricksPolicy.Decide in the app: each name part takes the first level whose word it contains; an admin/owner part
# refuses; no level or two levels refuse.
decide() {
  jq -c --arg g "$1" '
    ($g | ascii_downcase | [splits("[-_ ]+")] | map(select(length > 0))) as $parts
    | .levels as $levels | .entitlementsForEveryone as $everyone
    | [ $parts[] as $p | ($levels | map(select(any(.match[]; . as $w | $p | contains($w)))) | .[0]) as $l
        | select($l != null) | {part: $p, level: $l} ] as $found
    | ($found | map(select(.level.byAgent | not)) | .[0]) as $ref
    | if $ref != null then {level: $ref.level.level, byAgent: false,
          why: "the group name part \"\($ref.part)\" means \($ref.level.level): \($ref.level.why // "a person decides")"}
      else ($found | map(.level.level) | unique) as $names
        | if ($names | length) == 0 then {level: "Unknown", byAgent: false, why: "the group name names no access level (reader, writer, engineer ...)"}
          elif ($names | length) > 1 then {level: "Ambiguous", byAgent: false, why: "the group name names more than one access level (\($names | join(", ")))"}
          else $found[0].level as $l | {level: $l.level, byAgent: true, why: "group name part \"\($found[0].part)\" means \($l.level)",
               entitlements: ($everyone + ($l.entitlements // []) | unique), catalog: ($l.catalogPrivileges // []), schema: ($l.schemaPrivileges // [])}
          end
      end' "$POLICY"
}

workspace_host() {  # → adb-<id>.<n>.azuredatabricks.net (from the URL, else from the workspace resource)
  local w="$1" id
  if [[ "$w" =~ (adb-[0-9]+\.[0-9]+\.azuredatabricks\.net) ]]; then printf '%s' "${BASH_REMATCH[1]}"; return; fi
  [[ "$w" =~ ^[0-9]+$ ]] && die "pass the workspace name or URL with --grant (the numeric id does not name its host)"
  id=$(azq resource list --resource-type Microsoft.Databricks/workspaces ${SUB_ARGS[@]+"${SUB_ARGS[@]}"} --query "[?name=='$w'].id | [0]" -o tsv)
  [[ -n "$id" ]] || die "workspace \"$w\" not found in this subscription"
  azq resource show --ids "$id" --query properties.workspaceUrl -o tsv
}

scim_group() {  # host name → "id<TAB>entitlement,entitlement" ("" when the workspace does not show the group)
  call GET "https://$1/api/2.0/preview/scim/v2/Groups?filter=$(urlenc "displayName eq \"$2\"")" \
    || die "reading group $2 in the workspace failed ($(http_status)): $(api_error) (are you a workspace admin there?)"
  jq -r --arg n "$2" '[.Resources[]? | select((.displayName | ascii_downcase) == ($n | ascii_downcase))][0] // empty
    | "\(.id)\t\([.entitlements[]?.value] | join(","))"' "$WORK/body"
}

effective() {  # host type name group → "PRIV,PRIV" (inherited included, F117)
  call GET "https://$1/api/2.1/unity-catalog/effective-permissions/$2/$3?principal=$(urlenc "$4")" \
    || die "reading the privileges of $4 on $2 $3 failed ($(http_status)): $(api_error)"
  jq -r --arg g "$4" '[.privilege_assignments[]? | select((.principal | ascii_downcase) == ($g | ascii_downcase)) | .privileges[]?
    | (if type == "string" then . else .privilege end)] | unique | join(",")' "$WORK/body"
}

missing() { jq -rn --arg want "$1" --arg have "$2" '($have | split(",")) as $h | [$want | split(",")[] | select(length > 0) | select(. as $w | $h | index($w) | not)] | join(",")'; }
has_all() { [[ -z "$(missing "$1" "$2")" || ",$2," == *",ALL_PRIVILEGES,"* ]]; }

# A4 + A5 for one group (USER), then the tests. Prints what it did; appends the result to the log; returns 1 on a FAIL.
setup_access() {
  local name="$1" ws="$2" d host line gid have add catalog schema stype sname want got fails=0 did=() tests=() i
  d=$(decide "$name")
  if [[ "$(jq -r .byAgent <<<"$d")" != true ]]; then
    say "  $name: entitlements and Unity Catalog left to a person: $(jq -r .why <<<"$d")."
    mkdir -p "$(dirname "$LOG")"
    printf '%s | %s | workspace %s | group %s | level %s | not set up: %s\n' "$(date -u +%FT%TZ)" "${APPROVED_BY:-plan}" "$WORKSPACE" "$name" \
      "$(jq -r .level <<<"$d")" "$(jq -r .why <<<"$d")" >> "$LOG"
    return 0
  fi
  host=$(workspace_host "$WORKSPACE")
  # A4. A group just assigned can take a moment to show in the workspace.
  for i in 1 2 3 4 5 6; do line=$(scim_group "$host" "$name"); [[ -n "$line" ]] && break; [[ -n "${AIM_SETUP_MOCK:-}" ]] || sleep 5; done
  [[ -n "$line" ]] || die "workspace $WORKSPACE does not show $name yet: run apply again in a few minutes"
  gid=${line%%$'\t'*}; have=${line#*$'\t'}
  add=$(missing "$(jq -r '.entitlements | join(",")' <<<"$d")" "$have")
  if [[ -n "$add" ]]; then
    jq -nc --arg a "$add" '{schemas: ["urn:ietf:params:scim:api:messages:2.0:PatchOp"], Operations: [{op: "add", path: "entitlements", value: [$a | split(",")[] | {value: .}]}]}' \
      | call PATCH "https://$host/api/2.0/preview/scim/v2/Groups/$gid" body || die "adding entitlements to $name failed ($(http_status)): $(api_error)"
    did+=("entitlements $add")
  fi
  # A5 (with --grant): the level's privileges on the catalog, then on the schema (or on the whole catalog).
  if [[ -n "$GRANT" ]]; then
    catalog=${GRANT%%.*}; schema=""; [[ "$GRANT" == *.* ]] && schema=${GRANT#*.}
    for i in catalog schema; do
      if [[ $i == catalog ]]; then stype=catalog; sname=$catalog; want=$(jq -r '.catalog | join(",")' <<<"$d")
      else if [[ -n "$schema" ]]; then stype=schema; sname="$catalog.$schema"; else stype=catalog; sname=$catalog; fi; want=$(jq -r '.schema | join(",")' <<<"$d"); fi
      [[ -n "$want" ]] || continue
      got=$(effective "$host" "$stype" "$sname" "$name")
      has_all "$want" "$got" && continue
      add=$(missing "$want" "$got")
      jq -nc --arg g "$name" --arg a "$add" '{changes: [{principal: $g, add: ($a | split(","))}]}' \
        | call PATCH "https://$host/api/2.1/unity-catalog/permissions/$stype/$sname" body || die "granting $add on $stype $sname failed ($(http_status)): $(api_error)"
      did+=("$add on $stype $sname")
    done
  fi
  say "  $name: $(jq -r .level <<<"$d") ($(jq -r .why <<<"$d")): ${did[*]:-everything was already in place}."

  # The tests: everything read back.
  t() { if [[ "$1" == PASS ]]; then tests+=("PASS $2"); else tests+=("FAIL $2"); fails=$((fails + 1)); fi; say "    $1 $2 $3"; }
  local perms; perms=$(assignments "$ws" | awk -F'\t' -v n="$name" 'tolower($2) == tolower(n) {print $3}')
  [[ ",$perms," == *",USER,"* || ",$perms," == *",ADMIN,"* ]] && t PASS DBX_WORKSPACE "assigned ${perms}" || t FAIL DBX_WORKSPACE "not assigned to $WORKSPACE"
  line=$(scim_group "$host" "$name"); have=${line#*$'\t'}; want=$(jq -r '.entitlements | join(",")' <<<"$d")
  [[ -n "$line" && -z "$(missing "$want" "$have")" ]] && t PASS DBX_ENTITLEMENTS "$want" || t FAIL DBX_ENTITLEMENTS "has ${have:-none}, needs $want"
  if [[ -n "$GRANT" ]]; then
    want=$(jq -r '.catalog | join(",")' <<<"$d"); got=$(effective "$host" catalog "$catalog" "$name")
    has_all "$want" "$got" && t PASS DBX_UC_CATALOG "$want on catalog $catalog" || t FAIL DBX_UC_CATALOG "has ${got:-none} on catalog $catalog, needs $want"
    if [[ -n "$schema" ]]; then stype=schema; sname="$catalog.$schema"; else stype=catalog; sname=$catalog; fi
    want=$(jq -r '.schema | join(",")' <<<"$d"); got=$(effective "$host" "$stype" "$sname" "$name")
    has_all "$want" "$got" && t PASS DBX_UC_SCHEMA "$want on $stype $sname" || t FAIL DBX_UC_SCHEMA "has ${got:-none} on $stype $sname, needs $want"
  fi
  mkdir -p "$(dirname "$LOG")"
  printf '%s | approved %s | workspace %s | group %s | level %s | did: %s | tests %s: %s\n' "$(date -u +%FT%TZ)" "$APPROVED_BY" "$WORKSPACE" "$name" \
    "$(jq -r .level <<<"$d")" "${did[*]:-nothing (in place)}" "$(( ${#tests[@]} - fails ))/${#tests[@]} PASS" "${tests[*]}" >> "$LOG"
  (( fails == 0 ))
}

# ---- commands -----------------------------------------------------------------------------------------------------------

console_steps() {
  say "In the account console (https://accounts.azuredatabricks.net, as an account admin), Security > Identity provider setup (F110):"
  say "  1. Identity management: start a readiness report scan and fix what it lists (needed if SCIM or manual groups were used)."
  say "  2. Toggle Automatic identity management to Enabled; it takes 5-10 minutes. (Learn documents no API for this switch.)"
  say "  Needs: Premium tier, one Entra tenant (no cross-tenant identities), at least one identity-federated workspace."
}

plan_or_apply() {
  local mode="$1" ws="" line oid name pid perms current connectors d fails=0
  is_guid "$ACCOUNT" || die "--account-id must be the Databricks account id (a GUID; account console > your user menu)"
  [[ "$PERMISSION" == USER || "$PERMISSION" == ADMIN ]] || die "permission must be USER or ADMIN"
  [[ -z "$GRANT" || "$GRANT" =~ ^[A-Za-z0-9_]+(\.[A-Za-z0-9_]+)?$ ]] || die "--grant takes <catalog> or <catalog>.<schema> (Unity Catalog names)"
  [[ -z "$GRANT" || "$PERMISSION" == USER ]] || die "--grant is for USER groups; an --admin group gets no grants from this script"
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
      if [[ "$PERMISSION" == USER ]]; then
        d=$(decide "$name")
        if [[ "$(jq -r .byAgent <<<"$d")" == true ]]; then
          say "    by name: $(jq -r .level <<<"$d") -> entitlements $(jq -r '.entitlements | join(", ")' <<<"$d")${GRANT:+; on $GRANT: catalog $(jq -r '.catalog | join(", ")' <<<"$d"), schema $(jq -r '.schema | join(", ")' <<<"$d")}"
        else say "    by name: left to a person ($(jq -r .why <<<"$d"))"; fi
      fi
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
    if [[ ",$perms," == *",ADMIN,"* || ",$perms," == *",$PERMISSION,"* ]]; then say "$name: in the account (id $pid), already ${perms} on the workspace: kept."
    else
      jq -nc --arg p "$PERMISSION" '{permissions: [$p]}' \
        | call PUT "$ACCOUNTS/api/2.0/accounts/$ACCOUNT/workspaces/$ws/permissionassignments/principals/$pid" body \
        || die "assigning $name to workspace $ws failed ($(http_status)): $(api_error) (is the workspace identity-federated?)"
      say "$name: in the account (id $pid), assigned $PERMISSION on workspace $ws."
    fi
    if [[ "$PERMISSION" == USER ]]; then setup_access "$name" "$ws" || fails=$((fails + 1)); fi
  done
  [[ "$PERMISSION" == USER ]] && { say ""; say "Logged in $LOG. For docs/DEPLOYMENT_LOG.md:"; tail -n "${#groups[@]}" "$LOG" | sed 's/^/  /'; }
  (( fails == 0 )) || die "$fails group(s) did not pass every test (see FAIL above): fix and run apply again"
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

# ---- offline self-test (mocked Azure CLI and Databricks) --------------------------------------------------------------------

mock_az() {
  local m="$AIM_SETUP_MOCK" a="$*"
  printf 'az %s\n' "$a" >> "$m/calls.log"
  case "$a" in
    "account get-access-token"*) printf 'mock-DBX-token-91c2\n' ;;
    "resource list"*"?name=='ws-sample'"*) printf '/subscriptions/s1/resourceGroups/rg-sample/providers/Microsoft.Databricks/workspaces/ws-sample\n' ;;
    "resource list"*"?name=="*) ;;
    "resource list"*) printf 'ws-sample\trg-sample\tpremium\n' ;;
    "resource show"*"workspaceUrl"*) printf 'adb-1234567890123456.7.azuredatabricks.net\n' ;;
    "resource show"*) printf '1234567890123456\n' ;;
    "ad group show --group SAMPLE-GRP-DBX-USERS"*) printf '{"id":"aaaaaaaa-0000-4000-8000-000000000001","name":"SAMPLE-GRP-DBX-USERS"}\n' ;;
    "ad group show --group SAMPLE-GRP-DBX-ADMINS"*) printf '{"id":"aaaaaaaa-0000-4000-8000-000000000002","name":"SAMPLE-GRP-DBX-ADMINS"}\n' ;;
    "ad group show --group SAMPLE-GRP-DBX-READERS"*) printf '{"id":"aaaaaaaa-0000-4000-8000-000000000003","name":"SAMPLE-GRP-DBX-READERS"}\n' ;;
    "ad group show"*) printf 'ERROR: Resource does not exist\n'; return 1 ;;
    "rest --method GET --url https://graph.microsoft.com/v1.0/applicationTemplates"*) printf 'tmpl-1\n' ;;
    "rest --method GET --url https://graph.microsoft.com/v1.0/servicePrincipals"*) printf 'SAMPLE account SCIM\n' ;;
    *) printf 'mock az: no route for %s\n' "$a" >&2; return 1 ;;
  esac
}

mock_call() {
  local method="$1" url="$2" with_body="${3:-}" m="$AIM_SETUP_MOCK" in="" code=200 out='{}' path
  [[ "$with_body" == body ]] && in=$(cat)
  path=$(sed -E 's#^https://[^/]+##' <<<"$url")
  printf 'dbx %s %s %s\n' "$method" "$path" "$in" >> "$m/calls.log"
  case "$method $path" in
    "GET /api/2.1/accounts/"*"/scim/v2/Users?count=1") out='{"totalResults":5}' ;;
    "GET /api/2.0/accounts/"*"/workspaces/1234567890123456/permissionassignments")
      out=$(jq -Rn '{permission_assignments: [inputs | select(length > 0) | split("\t") | {principal: {principal_id: (.[0] | tonumber), group_name: .[1]}, permissions: (.[2] | split(","))}]}' \
        < <(cat "$m/assigned" 2>/dev/null || true)) ;;
    "GET /api/2.0/identity/accounts/"*"/external-groups/"*)
      if [[ -f "$m/aim_off" ]]; then code=400; out='{"error_code":"INVALID_STATE","message":"mock: account is not onboarded onto Automatic Identity Management"}'
      else case "$path" in *000000000001) out='{"internal_id":"9001","display_name":"SAMPLE-GRP-DBX-USERS"}' ;;
        *000000000003) out='{"internal_id":"9003","display_name":"SAMPLE-GRP-DBX-READERS"}' ;; *) out='{"internal_id":"9002","display_name":"SAMPLE-GRP-DBX-ADMINS"}' ;; esac; fi ;;
    "PUT /api/2.0/accounts/"*"/permissionassignments/principals/"*)
      local pid=${path##*/} name; case $pid in 9001) name=SAMPLE-GRP-DBX-USERS ;; 9003) name=SAMPLE-GRP-DBX-READERS ;; *) name=SAMPLE-GRP-DBX-ADMINS ;; esac
      { grep -v "^$pid	" "$m/assigned" 2>/dev/null || true; printf '%s\t%s\t%s\n' "$pid" "$name" "$(jq -r '.permissions | join(",")' <<<"$in")"; } > "$m/assigned.new"
      mv "$m/assigned.new" "$m/assigned" ;;
    "GET /api/2.0/preview/scim/v2/Groups?filter="*)  # an assigned group shows in the workspace, with its entitlements
      local row; row=$(while IFS=$'\t' read -r p n _; do [[ "$path" == *"$n"* ]] && printf '%s\t%s' "$p" "$n"; done < <(cat "$m/assigned" 2>/dev/null || true))
      if [[ -n "$row" ]]; then out=$(jq -nc --arg id "${row%%$'\t'*}" --arg n "${row#*$'\t'}" --rawfile e <(cat "$m/ent_${row%%$'\t'*}" 2>/dev/null || true) \
          '{Resources: [{id: $id, displayName: $n, entitlements: [$e | split("\n")[] | select(length > 0) | {value: .}]}]}')
      else out='{"Resources":[]}'; fi ;;
    "PATCH /api/2.0/preview/scim/v2/Groups/"*) jq -r '.Operations[].value[].value' <<<"$in" >> "$m/ent_${path##*/}" ;;
    "GET /api/2.1/unity-catalog/effective-permissions/"*)
      local sec=${path#/api/2.1/unity-catalog/effective-permissions/}; sec=${sec%%\?*}; local g=${path##*principal=}
      out=$(jq -Rn --arg s "$sec" --arg c "catalog/${sec#*/}" --arg g "$g" \
        '[inputs | split("\t") | select(.[1] == $g and (.[0] == $s or (($s | startswith("schema/")) and .[0] == ($c | split(".")[0]))))] | {privilege_assignments: [{principal: $g, privileges: [.[] | {privilege: .[2]}]}]}' \
        < <(cat "$m/grants" 2>/dev/null || true)) ;;
    "PATCH /api/2.1/unity-catalog/permissions/"*)
      [[ -f "$m/drop_grants" ]] || jq -r --arg s "${path#/api/2.1/unity-catalog/permissions/}" '.changes[] | .principal as $p | .add[] | "\($s)\t\($p)\t\(.)"' <<<"$in" >> "$m/grants" ;;
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
  run() { local o; o=$(AIM_SETUP_MOCK="$m" AIM_SETUP_LOG="$m/setup.log" bash "$0" "$@" 2>&1) && rc=0 || rc=$?; all+="$o"; printf '%s' "$o"; return "$rc"; }
  patches() { grep -c '^dbx PATCH ' "$m/calls.log" || true; }
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
  out=$(run plan --account-id "$acc" --workspace "adb-1234567890123456.0.azuredatabricks.net/" "${G[@]}" || true)
  check "plan: the workspace URL (no https, trailing slash) names the same workspace" '[[ "$out" == *"SAMPLE-GRP-DBX-USERS"*"already USER"* && "$out" == *"SAMPLE-GRP-DBX-ADMINS"*"already ADMIN"* && "$out" != *"found in this subscription"* ]]'
  out=$(run status --account-id "$acc" --workspace "https://adb-1234567890123456.7.azuredatabricks.net/?o=1234567890123456" || true)
  check "status: the full https workspace URL works" '[[ "$out" == *"SAMPLE-GRP-DBX-USERS"*"USER"* ]]'
  # v1.2: A4/A5 by group name, the tests and the log.
  local R=(--group SAMPLE-GRP-DBX-READERS) out2
  out=$(run plan --account-id "$acc" --workspace ws-sample "${R[@]}" --grant sample_e2e.curated)
  check "plan: the name decides (Reader: SQL access; USE_CATALOG; USE_SCHEMA, SELECT)" '[[ "$out" == *"by name: Reader -> entitlements databricks-sql-access, workspace-access; on sample_e2e.curated: catalog USE_CATALOG, schema USE_SCHEMA, SELECT"* && "$(patches)" == 0 ]]'
  out=$(run apply --account-id "$acc" --workspace ws-sample "${R[@]}" --grant sample_e2e.curated --approved-by "SAMPLE 2026-11-02")
  check "apply --grant: Workspace access and Databricks SQL access added" 'grep -q "^dbx PATCH /api/2.0/preview/scim/v2/Groups/9003 .*databricks-sql-access.*workspace-access" "$m/calls.log"'
  check "apply --grant: USE_CATALOG on the catalog, USE_SCHEMA and SELECT on the schema" 'grep -q "catalog/sample_e2e	SAMPLE-GRP-DBX-READERS	USE_CATALOG" "$m/grants" && [[ $(grep -c "schema/sample_e2e.curated	SAMPLE-GRP-DBX-READERS" "$m/grants") == 2 ]]'
  check "apply --grant: the four tests pass and are logged" '[[ "$out" == *"PASS DBX_WORKSPACE"*"PASS DBX_ENTITLEMENTS"*"PASS DBX_UC_CATALOG"*"PASS DBX_UC_SCHEMA"* ]] && grep -q "group SAMPLE-GRP-DBX-READERS | level Reader | .*tests 4/4 PASS" "$m/setup.log"'
  : > "$m/calls.log"
  out=$(run apply --account-id "$acc" --workspace ws-sample "${R[@]}" --grant sample_e2e.curated --approved-by "SAMPLE 2026-11-02")
  check "second apply: nothing granted again, tests pass" '[[ "$(patches)" == 0 && "$out" == *"everything was already in place"* && "$out" == *"PASS DBX_UC_SCHEMA"* ]]'
  out=$(run apply --account-id "$acc" --workspace ws-sample --group SAMPLE-GRP-DBX-USERS --grant sample_e2e.curated --approved-by "SAMPLE 2026-11-02")
  check "a name with no access level: left to a person, nothing granted" '[[ "$out" == *"left to a person"*"no access level"* && "$(patches)" == 0 ]]'
  rm -f "$m/grants"; touch "$m/drop_grants"
  out2=$(run apply --account-id "$acc" --workspace ws-sample "${R[@]}" --grant sample_e2e.curated --approved-by "SAMPLE 2026-11-02" || true)
  check "a grant that does not read back fails the test and the run" '[[ "$out2" == *"FAIL DBX_UC_CATALOG"* && "$out2" == *"did not pass every test"* ]] && grep -q "tests 2/4 PASS" "$m/setup.log"'
  rm -f "$m/drop_grants"
  check "--grant with a name that is not a Unity Catalog name refused" '! run apply --account-id "$acc" --workspace ws-sample "${R[@]}" --grant "x;DROP" --approved-by t >/dev/null'
  check "--grant with --admin refused" '! run apply --account-id "$acc" --workspace ws-sample "${R[@]}" --grant sample_e2e --admin --approved-by t >/dev/null'
  check "unknown Entra group refused" '! run plan --account-id "$acc" --workspace ws-sample --group NOPE >/dev/null'
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
    --grant) GRANT="${2:?catalog or catalog.schema}"; shift 2 ;;
    --subscription) SUB_ARGS=(--subscription "${2:?subscription id}"); shift 2 ;;
    --approved-by) APPROVED_BY="${2:?name and date}"; shift 2 ;;
    *) die "unknown argument $1 (see the header of this script)" ;;
  esac
done
case "$CMD" in
  plan|apply) plan_or_apply "$CMD" ;;
  status) status_cmd ;;
  self-test) self_test ;;
  *) die "usage: $(basename "$0") plan|apply|status|self-test [options] (see the header of this script)" ;;
esac
