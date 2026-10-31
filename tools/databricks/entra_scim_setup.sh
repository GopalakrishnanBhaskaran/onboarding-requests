#!/usr/bin/env bash
# Account-level SCIM provisioning from Microsoft Entra ID to Azure Databricks. Runs in Azure Cloud Shell (Bash).
#
#   entra_scim_setup.sh plan   --scim-url <Account SCIM URL> [--app-name <name>] [--group <name|object id>]...
#   entra_scim_setup.sh apply  --scim-url <Account SCIM URL> [--app-name <name>] [--group <name|object id>]... --approved-by "<name> <date>"
#   entra_scim_setup.sh status [--app-name <name>]
#   entra_scim_setup.sh pause  [--app-name <name>]      stops provisioning (always allowed)
#   entra_scim_setup.sh self-test                       offline, against mocked Graph and Databricks
#   Option for all: --tenant <Entra tenant id> (default: the Azure CLI's tenant)
#
# plan changes nothing. It checks the Databricks SCIM token with one read of the account SCIM API, signs in to Microsoft Graph,
# finds the gallery app, lists other SCIM connectors, resolves the groups and flags nested ones (SCIM does not sync them).
# apply (only with --approved-by) creates or reuses the enterprise app from the gallery app "Azure Databricks SCIM Provisioning
# Connector", sets Assignment required to No, creates the provisioning job, tests and saves the SCIM URL and token, assigns the
# groups and starts the job. A second apply changes only what is missing. Guide and facts: docs/DATABRICKS_SCIM.md.
#
# The Databricks SCIM token comes from DATABRICKS_SCIM_TOKEN or a hidden prompt. It is never printed, logged or put on a
# command line: curl and jq read it from stdin, and bearer tokens go to curl as a header file descriptor.
set -euo pipefail

GALLERY_APP="Azure Databricks SCIM Provisioning Connector"           # F105
GRAPH="https://graph.microsoft.com/v1.0"
LOGIN="https://login.microsoftonline.com"
CLI_TOOLS_APP="14d82eec-204b-4c2f-b7e8-296a70dab67e"                  # Microsoft Graph Command Line Tools (F109)
# Delegated permissions (F107, F108): create the app, configure the job, assign groups, read groups and their nested groups.
NEED_SCOPES=(Application.ReadWrite.All Synchronization.ReadWrite.All AppRoleAssignment.ReadWrite.All GroupMember.Read.All)
GUID_RE='^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
SCIM_URL_RE='^https://accounts\.azuredatabricks\.net/api/2\.1/accounts/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/scim/v2$'
DEFAULT_ROLE="00000000-0000-0000-0000-000000000000"

WORK=$(mktemp -d); trap 'rm -rf "$WORK"' EXIT
GRAPH_TOKEN="" DBX_TOKEN="" SCIM_URL="" APP_NAME="Azure Databricks SCIM Provisioning" TENANT="" APPROVED_BY=""
GROUP_ARGS=()
WAIT_SECONDS=10; [[ -n "${SCIM_SETUP_MOCK:-}" ]] && WAIT_SECONDS=0

say() { printf '%s\n' "$*"; }
# Any text that might echo a secret back (an API error) goes through mask first.
mask() { local s="$1"; [[ -n "$DBX_TOKEN" ]] && s="${s//"$DBX_TOKEN"/******}"; [[ -n "$GRAPH_TOKEN" ]] && s="${s//"$GRAPH_TOKEN"/******}"; printf '%s' "$s"; }
die() { printf 'ERROR: %s\n' "$(mask "$*")" >&2; exit 1; }
urlenc() { jq -rn --arg v "$1" '$v|@uri'; }
odata() { printf '%s' "${1//\'/\'\'}"; }          # a string literal inside an OData $filter
body() { cat "$WORK/body"; }
http_status() { cat "$WORK/status"; }
api_error() { jq -r '.error.message // .detail // .error_description // .' "$WORK/body" 2>/dev/null | head -c 400; }

# One HTTP call: call graph|dbx METHOD URL [body]. A request body comes on stdin. The response goes to $WORK/body and the
# status to $WORK/status; returns 0 on 2xx.
call() {
  local kind="$1" method="$2" url="$3" with_body="${4:-}" token status
  if [[ -n "${SCIM_SETUP_MOCK:-}" ]]; then mock_call "$@"; return; fi
  case "$kind" in graph) token="$GRAPH_TOKEN" ;; dbx) token="$DBX_TOKEN" ;; *) die "unknown endpoint kind $kind" ;; esac
  if [[ "$with_body" == body ]]; then
    status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" "$url" \
      -H @<(printf 'Authorization: Bearer %s\nContent-Type: application/json\n' "$token") --data-binary @-) || status=000
  else
    status=$(curl -sS -o "$WORK/body" -w '%{http_code}' -X "$method" "$url" \
      -H @<(printf 'Authorization: Bearer %s\n' "$token")) || status=000
  fi
  printf '%s' "$status" > "$WORK/status"
  [[ "$status" == 2* ]]
}
graph_get() { call graph GET "$GRAPH$1" || die "Graph GET $1 failed ($(http_status)): $(api_error)"; body; }

# ---- inputs ---------------------------------------------------------------------------------------------------------------

valid_scim_url() { [[ "$1" =~ $SCIM_URL_RE ]]; }
is_guid() { [[ "$1" =~ $GUID_RE ]]; }

read_dbx_token() {
  if [[ -n "${DATABRICKS_SCIM_TOKEN:-}" ]]; then DBX_TOKEN="$DATABRICKS_SCIM_TOKEN"
  else read -rsp "Databricks SCIM token (account console > Security > Identity provider setup; input hidden): " DBX_TOKEN; echo; fi
  [[ -n "$DBX_TOKEN" && "$DBX_TOKEN" != *[[:space:]]* ]] || die "the SCIM token is empty or contains spaces"
}

# One read of the account SCIM API proves the URL and the token belong together, before Entra is touched.
check_scim() {
  if call dbx GET "$SCIM_URL/Users?count=1"; then
    say "Databricks: the SCIM token is accepted by $SCIM_URL ($(jq -r '.totalResults // "?"' "$WORK/body") users in the account)."
  else
    case "$(http_status)" in
      401|403) die "Databricks refused the SCIM token ($(http_status)): expired, revoked, or from another account. Generate a new one in the account console." ;;
      404) die "Databricks has no account SCIM API at $SCIM_URL (404): check the account id in the URL." ;;
      *) die "Databricks SCIM check failed ($(http_status)): $(api_error)" ;;
    esac
  fi
}

# ---- Microsoft Graph sign-in ------------------------------------------------------------------------------------------------

jwt_claim() {  # jwt_claim CLAIM < token
  python3 -c 'import sys,json,base64
t=sys.stdin.read().strip().split(".")
p=t[1]+"="*(-len(t[1])%4) if len(t)>2 else ""
c=json.loads(base64.urlsafe_b64decode(p)) if p else {}
v=c.get(sys.argv[1],"")
print(" ".join(v) if isinstance(v,list) else v)' "$1"
}
missing_scopes() {  # missing_scopes "<scp claim>" → the needed scopes it lacks
  local s lacking=()
  for s in "${NEED_SCOPES[@]}"; do [[ " $1 " == *" $s "* ]] || lacking+=("$s"); done
  printf '%s' "${lacking[*]}"
}

graph_login() {
  if [[ -n "${SCIM_SETUP_MOCK:-}" ]]; then GRAPH_TOKEN="mock-graph-token"; return; fi
  local t lacking
  t=$(az account get-access-token --resource-type ms-graph --tenant "$TENANT" --query accessToken -o tsv 2>/dev/null || true)
  if [[ -n "$t" ]]; then
    lacking=$(missing_scopes "$(printf '%s' "$t" | jwt_claim scp)")
    if [[ -z "$lacking" ]]; then GRAPH_TOKEN="$t"; say "Graph: Azure CLI sign-in ($(printf '%s' "$t" | jwt_claim upn))."; return; fi
    say "Graph: the Azure CLI token lacks $lacking. Signing in with a device code (Microsoft Graph Command Line Tools)."
  fi
  device_code_login
}

device_code_login() {
  local scopes="" s resp dc interval deadline tok err
  for s in "${NEED_SCOPES[@]}"; do scopes+="https://graph.microsoft.com/$s "; done
  resp=$(printf 'client_id=%s&scope=%s' "$CLI_TOOLS_APP" "$(urlenc "${scopes}offline_access")" \
    | curl -sS -X POST "$LOGIN/$TENANT/oauth2/v2.0/devicecode" --data-binary @-)
  dc=$(jq -r '.device_code // empty' <<<"$resp")
  [[ -n "$dc" ]] || die "device code sign-in could not start: $(jq -r '.error_description // .error // "no answer"' <<<"$resp" | head -c 300)"
  say "$(jq -r '.message' <<<"$resp")"
  say "(Sign in as a Cloud Application Administrator or Application Administrator; consent to the listed permissions if asked.)"
  interval=$(jq -r '.interval // 5' <<<"$resp"); deadline=$((SECONDS + $(jq -r '.expires_in // 900' <<<"$resp")))
  while (( SECONDS < deadline )); do
    sleep "$interval"
    tok=$(printf 'grant_type=urn:ietf:params:oauth:grant-type:device_code&client_id=%s&device_code=%s' "$CLI_TOOLS_APP" "$(urlenc "$dc")" \
      | curl -sS -X POST "$LOGIN/$TENANT/oauth2/v2.0/token" --data-binary @-)
    GRAPH_TOKEN=$(jq -r '.access_token // empty' <<<"$tok")
    if [[ -n "$GRAPH_TOKEN" ]]; then say "Graph: signed in ($(printf '%s' "$GRAPH_TOKEN" | jwt_claim upn))."; return; fi
    err=$(jq -r '.error // empty' <<<"$tok")
    case "$err" in
      authorization_pending) ;;
      slow_down) interval=$((interval + 5)) ;;
      *) die "device code sign-in failed: $(jq -r '.error_description // .error' <<<"$tok" | head -c 300) (Conditional Access may block device code sign-in: see docs/DATABRICKS_SCIM.md)" ;;
    esac
  done
  die "device code sign-in timed out"
}

# ---- Entra objects ----------------------------------------------------------------------------------------------------------

template_id() {
  local id
  id=$(graph_get "/applicationTemplates?\$filter=$(urlenc "displayName eq '$(odata "$GALLERY_APP")'")&\$select=id,displayName" | jq -r '.value[0].id // empty')
  [[ -n "$id" ]] || die "gallery app \"$GALLERY_APP\" not found in Microsoft Graph"
  printf '%s' "$id"
}

# The service principal named APP_NAME as JSON ("" when none); two with that name is an error (pass another --app-name).
find_sp() {
  local list n
  list=$(graph_get "/servicePrincipals?\$filter=$(urlenc "displayName eq '$(odata "$APP_NAME")'")&\$select=id,appId,displayName,applicationTemplateId,appRoleAssignmentRequired,appRoles")
  n=$(jq '.value | length' <<<"$list")
  (( n <= 1 )) || die "$n enterprise apps are named \"$APP_NAME\": pass --app-name with a unique name"
  jq -c '.value[0] // empty' <<<"$list"
}

# Other enterprise apps from the same gallery app: workspace-level connectors must be switched off (F105).
other_connectors() {
  if call graph GET "$GRAPH/servicePrincipals?\$filter=$(urlenc "applicationTemplateId eq '$1'")&\$select=id,displayName"; then
    jq -r --arg me "$APP_NAME" '.value[] | select(.displayName != $me) | "\(.displayName) (\(.id))"' "$WORK/body"
  else
    say "(could not list other connectors: $(http_status). Check Enterprise applications for other \"$GALLERY_APP\" apps.)" >&2
  fi
}

resolve_group() {  # → "id<TAB>displayName"
  local g="$1" list n
  if is_guid "$g"; then
    call graph GET "$GRAPH/groups/$g?\$select=id,displayName" || die "group $g not found ($(http_status))"
    jq -r '"\(.id)\t\(.displayName)"' "$WORK/body"; return
  fi
  list=$(graph_get "/groups?\$filter=$(urlenc "displayName eq '$(odata "$g")'")&\$select=id,displayName")
  n=$(jq '.value | length' <<<"$list")
  (( n == 1 )) || die "group \"$g\": $n groups have that name (use its object id)"
  jq -r '.value[0] | "\(.id)\t\(.displayName)"' <<<"$list"
}

nested_groups() { graph_get "/groups/$1/members/microsoft.graph.group?\$select=id,displayName&\$top=5" | jq -r '[.value[].displayName] | join(", ")'; }
assigned_principals() { graph_get "/servicePrincipals/$1/appRoleAssignedTo?\$select=principalId&\$top=999" | jq -r '.value[].principalId'; }

# The app role a group gets: "User", else "msiam_access", else any enabled role open to users, else the default role (F108).
pick_app_role() {
  jq -r --arg d "$DEFAULT_ROLE" '
    [.appRoles[]? | select(.isEnabled != false and ((.allowedMemberTypes // []) | index("User")))] as $r
    | (($r | map(select(.displayName == "User" or .value == "User")) | .[0].id)
       // ($r | map(select(.displayName == "msiam_access")) | .[0].id)
       // ($r[0].id) // $d)'
}

sync_template() {  # the provisioning template of the new app; Entra may need a moment after the app is created
  local sp="$1" id
  for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do
    if call graph GET "$GRAPH/servicePrincipals/$sp/synchronization/templates"; then
      id=$(jq -r '.value[0].id // empty' "$WORK/body"); [[ -n "$id" ]] && { printf '%s' "$id"; return; }
    fi
    sleep "$WAIT_SECONDS"
  done
  die "no provisioning template for $sp after 2 minutes ($(http_status)): $(api_error)"
}

# True unless the app says false (jq's // would treat false as missing). A new app from the gallery requires assignment.
assignment_required() { [[ "$(jq -r 'if .appRoleAssignmentRequired == false then "no" else "yes" end')" == yes ]]; }

job_json() { graph_get "/servicePrincipals/$1/synchronization/jobs" | jq -c '.value[0] // empty'; }

credentials_json() {  # credentials_json validate|secrets: the token comes in on stdin, never as an argument
  jq -Rs --arg url "$SCIM_URL" --arg shape "$1" '
    [{key: "BaseAddress", value: $url}, {key: "SecretToken", value: .}] as $c
    | if $shape == "validate" then {credentials: $c} else {value: $c} end'
}

# ---- commands ---------------------------------------------------------------------------------------------------------------

aim_notice() {
  say "Before you continue (F106): Databricks recommends automatic identity management (AIM) instead of SCIM. AIM is on by"
  say "default for accounts created after 1 Aug 2025 and also syncs nested groups and service principals. Account console >"
  say "Security > Identity provider setup: if Automatic identity management is Enabled, you do not need SCIM, and adding the"
  say "same people through both creates duplicates. Use SCIM when AIM is off or not possible (e.g. cross-tenant B2B users)."
  say ""
}

plan_or_apply() {
  local mode="$1" tmpl sp sp_id job role others g line gid gname nested assigned=()
  valid_scim_url "$SCIM_URL" || die "--scim-url must be the Account SCIM URL: https://accounts.azuredatabricks.net/api/2.1/accounts/<account id>/scim/v2"
  [[ "$mode" == plan || -n "$APPROVED_BY" ]] || die "apply changes Entra ID: run plan, get the approval, then add --approved-by \"<name> <date>\""
  aim_notice
  read_dbx_token
  check_scim
  graph_login
  tmpl=$(template_id)
  sp=$(find_sp); sp_id=$(jq -r '.id // empty' <<<"${sp:-{\}}")
  others=$(other_connectors "$tmpl")

  local groups=()
  for g in ${GROUP_ARGS[@]+"${GROUP_ARGS[@]}"}; do groups+=("$(resolve_group "$g")"); done
  [[ -n "$sp_id" ]] && mapfile -t assigned < <(assigned_principals "$sp_id")

  say ""; say "Plan for tenant $TENANT (cost: none; group provisioning needs Entra ID P1 or P2):"
  if [[ -n "$sp_id" ]]; then say "  Enterprise app: \"$APP_NAME\" exists ($sp_id): reused"
  else say "  Enterprise app: \"$APP_NAME\" will be CREATED from the gallery app \"$GALLERY_APP\""; fi
  assignment_required <<<"${sp:-{\}}" && say "  Assignment required: will be set to No (Databricks recommends it)"
  if [[ -n "$sp_id" && -n "$(job_json "$sp_id")" ]]; then say "  Provisioning job: exists ($(job_json "$sp_id" | jq -r '"\(.id), \(.status.code // "?")"')): credentials saved again, then started"
  else say "  Provisioning job: will be CREATED from the app's provisioning template"; fi
  say "  SCIM URL: $SCIM_URL; SCIM token: ****** (tested by Entra, then saved in the app; never printed)"
  if (( ${#groups[@]} == 0 )); then say "  Groups: none given (assign them later with --group, or in the app's Users and groups)"; fi
  for line in ${groups[@]+"${groups[@]}"}; do
    gid=${line%%$'\t'*}; gname=${line#*$'\t'}
    if printf '%s\n' ${assigned[@]+"${assigned[@]}"} | grep -qx "$gid"; then say "  Group: $gname ($gid): already assigned"
    else say "  Group: $gname ($gid): will be ASSIGNED"; fi
    nested=$(nested_groups "$gid")
    [[ -n "$nested" ]] && say "    WARNING: nested groups ($nested) and their members are not provisioned by SCIM: assign them too, or use AIM"
  done
  say "  Start provisioning: the first cycle runs at once, then every 20 to 40 minutes"
  if [[ -n "$others" ]]; then
    say "  WARNING: other \"$GALLERY_APP\" apps exist. Workspace-level SCIM connectors must be switched off when the account-level"
    say "  connector is on (pause their provisioning in Entra):"; while IFS= read -r line; do say "    $line"; done <<<"$others"
  fi

  if [[ "$mode" == plan ]]; then
    say ""; say "Plan only: nothing changed. After approval run the same command as apply with --approved-by \"<name> <date>\"."
    return
  fi

  say ""; say "Approval recorded: $APPROVED_BY"
  if [[ -z "$sp_id" ]]; then
    printf '%s' "$APP_NAME" | jq -Rs '{displayName: .}' | call graph POST "$GRAPH/applicationTemplates/$tmpl/instantiate" body \
      || die "creating the enterprise app failed ($(http_status)): $(api_error)"
    sp_id=$(jq -r '.servicePrincipal.id' "$WORK/body"); say "Created enterprise app \"$APP_NAME\" ($sp_id)."
    for _ in 1 2 3 4 5 6 7 8 9 10 11 12; do sp=$(find_sp); [[ -n "$sp" ]] && break; sleep "$WAIT_SECONDS"; done
    [[ -n "$sp" ]] || die "the new app is not readable yet: run apply again in a few minutes (it reuses the app)"
  fi
  if assignment_required <<<"$sp"; then
    printf '{"appRoleAssignmentRequired":false}' | call graph PATCH "$GRAPH/servicePrincipals/$sp_id" body \
      || die "setting Assignment required to No failed ($(http_status)): $(api_error)"
    say "Assignment required: No."
  fi
  job=$(job_json "$sp_id")
  if [[ -z "$job" ]]; then
    jq -n --arg t "$(sync_template "$sp_id")" '{templateId: $t}' | call graph POST "$GRAPH/servicePrincipals/$sp_id/synchronization/jobs" body \
      || die "creating the provisioning job failed ($(http_status)): $(api_error)"
    job=$(body); say "Created provisioning job $(jq -r .id <<<"$job") (template $(jq -r .templateId <<<"$job"))."
  fi
  job=$(jq -r .id <<<"$job")
  printf '%s' "$DBX_TOKEN" | credentials_json validate | call graph POST "$GRAPH/servicePrincipals/$sp_id/synchronization/jobs/$job/validateCredentials" body \
    || die "Entra could not connect to Databricks with this URL and token ($(http_status)): $(api_error)"
  say "Test connection: OK."
  printf '%s' "$DBX_TOKEN" | credentials_json secrets | call graph PUT "$GRAPH/servicePrincipals/$sp_id/synchronization/secrets" body \
    || die "saving the SCIM URL and token failed ($(http_status)): $(api_error)"
  say "SCIM URL and token saved in the app."
  role=$(pick_app_role <<<"$sp")
  for line in ${groups[@]+"${groups[@]}"}; do
    gid=${line%%$'\t'*}; gname=${line#*$'\t'}
    if printf '%s\n' ${assigned[@]+"${assigned[@]}"} | grep -qx "$gid"; then continue; fi
    jq -n --arg p "$gid" --arg r "$sp_id" --arg a "$role" '{principalId: $p, resourceId: $r, appRoleId: $a}' \
      | call graph POST "$GRAPH/servicePrincipals/$sp_id/appRoleAssignedTo" body || die "assigning $gname failed ($(http_status)): $(api_error)"
    say "Assigned group $gname."
  done
  call graph POST "$GRAPH/servicePrincipals/$sp_id/synchronization/jobs/$job/start" || die "starting provisioning failed ($(http_status)): $(api_error)"
  say "Provisioning started. Check in a few minutes: $(basename "$0") status --app-name \"$APP_NAME\""
}

status_cmd() {
  local sp sp_id job
  graph_login
  sp=$(find_sp); sp_id=$(jq -r '.id // empty' <<<"${sp:-{\}}"); [[ -n "$sp_id" ]] || die "no enterprise app named \"$APP_NAME\""
  job=$(job_json "$sp_id"); [[ -n "$job" ]] || die "\"$APP_NAME\" has no provisioning job yet (run apply)"
  jq -r --arg app "$APP_NAME" '
    "App: \($app)  job \(.id)",
    "Schedule: \(.schedule.state // "?")  status: \(.status.code // "?")",
    "Last run: \(.status.lastExecution.state // "none") at \(.status.lastExecution.timeEnded // "-")",
    "Last successful run: \(.status.lastSuccessfulExecution.timeEnded // "none")",
    "Synced so far: \([.status.synchronizedEntryCountByType[]? | "\(.key)=\(.value)"] | join(", ") | if . == "" then "nothing yet" else . end)",
    (if .status.quarantine then "QUARANTINED: \(.status.quarantine.reason // "?") - \(.status.quarantine.error.message // "") (fix it, then run apply again)" else empty end)' <<<"$job"
  say "Groups assigned: $(assigned_principals "$sp_id" | wc -l)"
}

pause_cmd() {
  local sp sp_id job
  graph_login
  sp=$(find_sp); sp_id=$(jq -r '.id // empty' <<<"${sp:-{\}}"); [[ -n "$sp_id" ]] || die "no enterprise app named \"$APP_NAME\""
  job=$(job_json "$sp_id" | jq -r '.id // empty'); [[ -n "$job" ]] || die "no provisioning job"
  call graph POST "$GRAPH/servicePrincipals/$sp_id/synchronization/jobs/$job/pause" || die "pause failed ($(http_status)): $(api_error)"
  say "Provisioning paused on \"$APP_NAME\". Users already in Databricks stay; nothing more is synced until apply runs again."
}

# ---- offline self-test (mocked Graph and Databricks) ------------------------------------------------------------------------

mock_call() {
  local kind="$1" method="$2" url="$3" with_body="${4:-}" m="$SCIM_SETUP_MOCK" in="" code=200 out='{}' path
  [[ "$with_body" == body ]] && in=$(cat)
  path="${url#"$GRAPH"}"
  printf '%s %s %s\n' "$kind" "$method" "$path" >> "$m/calls.log"
  [[ -n "$in" ]] && printf '%s\n' "$in" >> "$m/bodies.log"
  local sp required
  required=$([[ -f $m/noassign ]] && echo false || echo true)
  sp='{"id":"sp-1","appId":"app-1","displayName":"Azure Databricks SCIM Provisioning","appRoleAssignmentRequired":'"$required"',"appRoles":[{"id":"role-msiam","displayName":"msiam_access","isEnabled":true,"allowedMemberTypes":["User"]}]}'
  local job='{"id":"job-1","templateId":"dataBricks","schedule":{"state":"Active"},"status":{"code":"Active","lastExecution":{"state":"Succeeded","timeEnded":"2026-10-31T10:00:00Z"},"lastSuccessfulExecution":{"timeEnded":"2026-10-31T10:00:00Z"},"synchronizedEntryCountByType":[{"key":"User","value":3}]}}'
  case "$method $path" in
    "GET https://accounts.azuredatabricks.net/"*) out='{"totalResults":3,"Resources":[]}' ;;
    "GET /applicationTemplates?"*) out='{"value":[{"id":"tmpl-1","displayName":"Azure Databricks SCIM Provisioning Connector"}]}' ;;
    "GET /servicePrincipals?\$filter=displayName"*) out="{\"value\":[$([[ -f $m/sp ]] && echo "$sp")]}" ;;
    "GET /servicePrincipals?\$filter=applicationTemplateId"*) out="{\"value\":[{\"id\":\"sp-old\",\"displayName\":\"SAMPLE workspace SCIM\"}$([[ -f $m/sp ]] && echo ",$sp")]}" ;;
    "POST /applicationTemplates/tmpl-1/instantiate") touch "$m/sp"; code=201; out='{"servicePrincipal":{"id":"sp-1"}}' ;;
    "PATCH /servicePrincipals/sp-1") touch "$m/noassign"; code=204 ;;
    "GET /servicePrincipals/sp-1/synchronization/templates") out='{"value":[{"id":"dataBricks","factoryTag":"databricks"}]}' ;;
    "GET /servicePrincipals/sp-1/synchronization/jobs") out="{\"value\":[$([[ -f $m/job ]] && echo "$job")]}" ;;
    "POST /servicePrincipals/sp-1/synchronization/jobs") touch "$m/job"; code=201; out="$job" ;;
    "POST /servicePrincipals/sp-1/synchronization/jobs/job-1/"*) code=204 ;;
    "PUT /servicePrincipals/sp-1/synchronization/secrets") code=204 ;;
    "GET /groups?"*SAMPLE-GRP-DBX-USERS*) out='{"value":[{"id":"aaaaaaaa-0000-4000-8000-000000000001","displayName":"SAMPLE-GRP-DBX-USERS"}]}' ;;
    "GET /groups?"*SAMPLE-GRP-DBX-ADMINS*) out='{"value":[{"id":"aaaaaaaa-0000-4000-8000-000000000002","displayName":"SAMPLE-GRP-DBX-ADMINS"}]}' ;;
    "GET /groups/aaaaaaaa-0000-4000-8000-000000000002/members/"*) out='{"value":[{"id":"g-9","displayName":"SAMPLE-GRP-NESTED"}]}' ;;
    "GET /groups/"*"/members/"*) out='{"value":[]}' ;;
    "GET /servicePrincipals/sp-1/appRoleAssignedTo"*) out=$(jq -Rn '{value: [inputs | select(length > 0) | {principalId: .}]}' < <(cat "$m/assigned" 2>/dev/null || true)) ;;
    "POST /servicePrincipals/sp-1/appRoleAssignedTo") jq -r .principalId <<<"$in" >> "$m/assigned"; code=201 ;;
    *) code=404; out='{"error":{"message":"mock: no route"}}' ;;
  esac
  printf '%s' "$out" > "$WORK/body"; printf '%s' "$code" > "$WORK/status"
  [[ "$code" == 2* ]]
}

# The checks are single-quoted on purpose (eval expands them), so variables used only there look unused, and the trap path
# is expanded when it is set because m is local.
# shellcheck disable=SC2016,SC2034,SC2064
self_test() {
  local m out secret="mock-dbx-SECRET-7f3a" url="https://accounts.azuredatabricks.net/api/2.1/accounts/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee/scim/v2" fails=0
  check() { if eval "$2"; then say "  PASS $1"; else say "  FAIL $1"; fails=$((fails + 1)); fi; }
  run() { SCIM_SETUP_MOCK="$m" DATABRICKS_SCIM_TOKEN="$secret" bash "$0" "$@" --tenant 00000000-0000-4000-8000-0000000000a1 2>&1; }
  writes() { grep -cE ' (POST|PUT|PATCH|DELETE) ' "$m/calls.log" || true; }
  m=$(mktemp -d); trap "rm -rf '$WORK' '$m'" EXIT   # expanded now on purpose: m is local to this function
  say "self-test:"
  check "SCIM URL accepted" 'valid_scim_url "$url"'
  check "SCIM URL with a workspace host refused" '! valid_scim_url "https://adb-123.4.azuredatabricks.net/api/2.0/preview/scim"'
  check "SCIM URL without account id refused" '! valid_scim_url "https://accounts.azuredatabricks.net/api/2.1/accounts/x/scim/v2"'
  local jwt; jwt="e30.$(printf '{"scp":"Application.ReadWrite.All Synchronization.ReadWrite.All User.Read"}' | base64 | tr -d '=\n' | tr '/+' '_-').sig"
  check "token scopes: the missing ones are named" '[[ "$(missing_scopes "$(printf "%s" "$jwt" | jwt_claim scp)")" == "AppRoleAssignment.ReadWrite.All GroupMember.Read.All" ]]'
  check "app role: msiam_access when there is no User role" '[[ "$(pick_app_role <<<"{\"appRoles\":[{\"id\":\"r1\",\"displayName\":\"msiam_access\",\"isEnabled\":true,\"allowedMemberTypes\":[\"User\"]}]}")" == r1 ]]'
  check "app role: the default role when the app has none" '[[ "$(pick_app_role <<<"{\"appRoles\":[]}")" == "$DEFAULT_ROLE" ]]'
  check "OData quotes doubled" '[[ "$(odata "O'"'"'Brien")" == "O'"'"''"'"'Brien" ]]'

  out=$(run plan --scim-url "$url" --group SAMPLE-GRP-DBX-USERS --group SAMPLE-GRP-DBX-ADMINS)
  check "plan: nothing written" '[[ "$(writes)" == 0 ]]'
  check "plan: says the app will be created and both groups assigned" '[[ "$out" == *"will be CREATED from the gallery"* && $(grep -c "will be ASSIGNED" <<<"$out") == 2 ]]'
  check "plan: nested group and other connector flagged" '[[ "$out" == *"SAMPLE-GRP-NESTED"* && "$out" == *"SAMPLE workspace SCIM"* && "$out" == *"automatic identity management"* ]]'
  check "apply without --approved-by refused, nothing written" '! run apply --scim-url "$url" >/dev/null && [[ "$(writes)" == 0 ]]'
  check "a bad SCIM URL is refused" '! run plan --scim-url "https://example.com/scim" >/dev/null'

  out=$(run apply --scim-url "$url" --group SAMPLE-GRP-DBX-USERS --group SAMPLE-GRP-DBX-ADMINS --approved-by "SAMPLE tester 2026-10-31")
  local order; order=$(grep -E ' (POST|PUT|PATCH) ' "$m/calls.log" | awk '{print $2" "$3}' | sed -E 's#aaaaaaaa-[0-9a-f-]+#G#' | tr '\n' ';')
  check "apply: create, assignment-required off, job, test, save, 2 groups, start (in order)" \
    '[[ "$order" == "POST /applicationTemplates/tmpl-1/instantiate;PATCH /servicePrincipals/sp-1;POST /servicePrincipals/sp-1/synchronization/jobs;POST /servicePrincipals/sp-1/synchronization/jobs/job-1/validateCredentials;PUT /servicePrincipals/sp-1/synchronization/secrets;POST /servicePrincipals/sp-1/appRoleAssignedTo;POST /servicePrincipals/sp-1/appRoleAssignedTo;POST /servicePrincipals/sp-1/synchronization/jobs/job-1/start;" ]]'
  check "apply: groups get the msiam_access role" '[[ $(grep -c "\"appRoleId\": \"role-msiam\"" "$m/bodies.log") == 2 ]]'
  check "apply: the token went only to Entra's test and save calls" '[[ $(grep -c "$secret" "$m/bodies.log") == 2 ]]'
  : > "$m/calls.log"
  out+=$(run apply --scim-url "$url" --group SAMPLE-GRP-DBX-USERS --group SAMPLE-GRP-DBX-ADMINS --approved-by "SAMPLE tester 2026-10-31")
  check "second apply: no new app, job or assignment (only test, save, start)" \
    '[[ "$(grep -E " (POST|PUT|PATCH) " "$m/calls.log" | awk "{print \$2\" \"\$3}" | tr "\n" ";")" == "POST /servicePrincipals/sp-1/synchronization/jobs/job-1/validateCredentials;PUT /servicePrincipals/sp-1/synchronization/secrets;POST /servicePrincipals/sp-1/synchronization/jobs/job-1/start;" ]]'
  out+=$(run status); check "status: state and counts" '[[ "$out" == *"status: Active"* && "$out" == *"User=3"* && "$out" == *"Groups assigned: 2"* ]]'
  out+=$(run pause); check "pause: job paused" 'grep -q "POST /servicePrincipals/sp-1/synchronization/jobs/job-1/pause" "$m/calls.log"'
  check "the SCIM token never appears in any output" '[[ "$out" != *"$secret"* ]]'
  if (( fails == 0 )); then say "self-test passed"; else say "self-test: $fails failed"; exit 1; fi
}

# ---- main ---------------------------------------------------------------------------------------------------------------------

CMD="${1:-}"; shift || true
while [[ $# -gt 0 ]]; do
  case "$1" in
    --scim-url) SCIM_URL="${2:?Account SCIM URL}"; SCIM_URL="${SCIM_URL%/}"; shift 2 ;;
    --app-name) APP_NAME="${2:?name}"; shift 2 ;;
    --group) GROUP_ARGS+=("${2:?group name or object id}"); shift 2 ;;
    --tenant) TENANT="${2:?tenant id}"; shift 2 ;;
    --approved-by) APPROVED_BY="${2:?name and date}"; shift 2 ;;
    *) die "unknown argument $1 (see the header of this script)" ;;
  esac
done
if [[ "$CMD" != self-test && -z "$TENANT" ]]; then
  TENANT=$(az account show --query tenantId -o tsv 2>/dev/null || true)
  [[ -n "$TENANT" ]] || die "not signed in to the Azure CLI: run az login, or pass --tenant <tenant id>"
fi
case "$CMD" in
  plan|apply) plan_or_apply "$CMD" ;;
  status) status_cmd ;;
  pause) pause_cmd ;;
  self-test) self_test ;;
  *) die "usage: $(basename "$0") plan|apply|status|pause|self-test [options] (see the header of this script)" ;;
esac
