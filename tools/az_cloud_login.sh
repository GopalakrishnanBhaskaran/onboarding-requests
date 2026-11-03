#!/usr/bin/env bash
# Azure CLI sign-in for Claude's cloud environment (claude.ai/code): one sign-in per tenant, one az profile per
# tenant + environment, so PEST and Vestacy (and dev / test / prod) never share a login or a current subscription.
# You sign in yourself in your own browser (device code + your MFA). Claude never sees or types a password.
#
# Usage (Claude runs these; you only do the browser step):
#   bash tools/az_cloud_login.sh login  <tenant>          start the device-code sign-in; prints the URL and the one-time code
#   bash tools/az_cloud_login.sh finish <tenant>          after you signed in: checks the tenant, makes one profile per environment
#   bash tools/az_cloud_login.sh status                   every tenant / environment: signed in? which subscription?
#   bash tools/az_cloud_login.sh subs   <tenant>          subscriptions you can see (to fill in the variables below)
#   bash tools/az_cloud_login.sh dir    <tenant> <env>    prints the AZURE_CONFIG_DIR of that profile
#   bash tools/az_cloud_login.sh logout <tenant>          signs out and deletes that tenant's profiles
#   <tenant> = pest | vestacy (any lower-case name works),  <env> = dev | test | prod
# Run a command in one environment:
#   export AZURE_CONFIG_DIR="$(bash tools/az_cloud_login.sh dir pest dev)" && az account show -o table
#
# Settings = environment variables of the Claude cloud environment (Edit environment > Environment variables), not git.
# They are names or ids, not secrets; a new session picks them up:
#   PEST_TENANT     PEST_SUB_DEV     PEST_SUB_TEST     PEST_SUB_PROD        (tenant: id or domain; subscription: name or id)
#   VESTACY_TENANT  VESTACY_SUB_DEV  VESTACY_SUB_TEST  VESTACY_SUB_PROD
# An environment whose variable is empty gets no profile. The container is reclaimed after inactivity and the
# sign-ins go with it, so a new session runs login + finish again. Nothing here changes Azure.
set -uo pipefail
BASE="${AZ_ENVS_DIR:-$HOME/.azure-envs}"
ENVS="dev test prod"
die() { echo "ERROR: $*" >&2; exit 1; }
mask() { sed -E 's/\b([0-9a-fA-F]{4})[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b/\1****-****/g'; }
var() { local n; n="$(echo "$1" | tr 'a-z-' 'A-Z_')_$2"; printf '%s' "${!n:-}"; }   # var pest SUB_DEV -> $PEST_SUB_DEV
tname() { case "${1:-}" in ""|*[!a-z0-9-]*) die "tenant: lower-case name such as pest or vestacy";; esac; }
isguid() { [[ "$1" =~ ^[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$ ]]; }
azd() { local d="$1"; shift; AZURE_CONFIG_DIR="$d" az "$@"; }

cmd="${1:-}"; [ $# -gt 0 ] && shift
case "$cmd" in
login)
  t="${1:-}"; tname "$t"; d="$BASE/$t"; ten="$(var "$t" TENANT)"
  if [ -f "$d/login.log" ] && [ ! -f "$d/login.rc" ]; then
    echo "A sign-in for $t is already waiting:"; grep -i devicelogin "$d/login.log" | tail -1; exit 0
  fi
  rm -rf "$d" "$BASE/$t"-*; mkdir -p "$d"; chmod 700 "$BASE" "$d"
  azd "$d" config set core.login_experience_v2=off core.collect_telemetry=false -o none 2>/dev/null || true
  [ -n "$ten" ] || echo "NOTE    $(echo "$t" | tr a-z A-Z)_TENANT is not set: you will land in the home tenant of the account you sign in with."
  # --allow-no-subscriptions: a sign-in with tenant-level rights only still works (Fabric, Graph, Azure DevOps).
  # login.rc appears when az login has ended (its exit code): no rc file = still waiting for the browser.
  nohup bash -c 'AZURE_CONFIG_DIR="$1" az login --use-device-code ${2:+--tenant "$2"} --allow-no-subscriptions -o none > "$1/login.log" 2>&1; echo $? > "$1/login.rc"' _ "$d" "$ten" >/dev/null 2>&1 &
  for _ in $(seq 1 60); do
    grep -qi devicelogin "$d/login.log" 2>/dev/null && break
    [ -f "$d/login.rc" ] && break
    sleep 1
  done
  if grep -qi devicelogin "$d/login.log"; then
    echo "== Sign in to $t (you, in your browser; Claude never sees your password):"
    grep -i devicelogin "$d/login.log" | tail -1
    echo "   Use the $t account. The code works once, for about 15 minutes."
    echo "   When the page says you are signed in, tell Claude 'done'. Claude then runs: finish $t"
  else
    echo "FAIL    the sign-in did not start:"; mask < "$d/login.log" | grep -v -i 'token' | tail -5
    echo "        AADSTS50199 / 'You don't have access to this' / a Conditional Access message = this tenant blocks device-code sign-in."
    exit 1
  fi;;
finish)
  t="${1:-}"; tname "$t"; d="$BASE/$t"; ten="$(var "$t" TENANT)"
  [ -d "$d" ] || die "no sign-in started for $t: run login $t"
  if [ ! -f "$d/login.rc" ]; then
    echo "WAIT    still waiting for your browser sign-in to $t:"; grep -i devicelogin "$d/login.log" | tail -1; exit 3
  fi
  if ! tid="$(azd "$d" account show --query tenantId -o tsv 2>/dev/null)" || [ -z "$tid" ]; then
    echo "FAIL    not signed in to $t:"; mask < "$d/login.log" | grep -v -i 'token' | tail -5; exit 1
  fi
  if isguid "$ten" && [ "${tid,,}" != "${ten,,}" ]; then
    echo "FAIL    signed in to tenant ${tid:0:4}****, but $(echo "$t" | tr a-z A-Z)_TENANT is ${ten:0:4}****. Wrong account: run logout $t, then login $t."; exit 1
  fi
  for o in "$BASE"/*/; do   # one tenant per sign-in: the same tenant must not sit under two names
    o="$(basename "$o")"; [ "$o" = "$t" ] || [[ "$o" == *-dev || "$o" == *-test || "$o" == *-prod ]] && continue
    ot="$(azd "$BASE/$o" account show --query tenantId -o tsv 2>/dev/null || true)"
    [ -n "$ot" ] && [ "${ot,,}" = "${tid,,}" ] && { echo "FAIL    $t and $o are the same tenant (${tid:0:4}****). Sign in to $t with the $t account."; exit 1; }
  done
  org="$(azd "$d" rest --method get --url 'https://graph.microsoft.com/v1.0/organization?$select=displayName' --query 'value[0].displayName' -o tsv 2>/dev/null || echo '?')"
  echo "OK      $t: tenant ${tid:0:4}**** ($org)"
  [ -n "$ten" ] || echo "CHECK   confirm that '$org' is $t, then set $(echo "$t" | tr a-z A-Z)_TENANT=<tenant id or domain> in the environment settings"
  made=0
  for e in $ENVS; do
    sub="$(var "$t" "SUB_$(echo "$e" | tr a-z A-Z)")"; ed="$BASE/$t-$e"; rm -rf "$ed"
    if [ -z "$sub" ]; then echo "SKIP    $t $e: $(echo "$t" | tr a-z A-Z)_SUB_$(echo "$e" | tr a-z A-Z) not set"; continue; fi
    cp -a "$d" "$ed"; rm -f "$ed/login.log" "$ed/login.rc"
    if ! azd "$ed" account set --subscription "$sub" 2>/dev/null; then echo "FAIL    $t $e: subscription '$(echo "$sub" | mask)' not visible to this sign-in"; rm -rf "$ed"; continue; fi
    st="$(azd "$ed" account show --query tenantId -o tsv)"
    if [ "${st,,}" != "${tid,,}" ]; then echo "FAIL    $t $e: that subscription is in another tenant (${st:0:4}****)"; rm -rf "$ed"; continue; fi
    echo "OK      $t $e: subscription $(azd "$ed" account show --query name -o tsv) ($(azd "$ed" account show --query id -o tsv | cut -c1-4)****)"
    made=$((made+1))
  done
  [ "$made" -gt 0 ] || { echo "== No environment profile yet. Subscriptions you can see in $t:"; bash "$0" subs "$t"; };;
subs)
  t="${1:-}"; tname "$t"; d="$BASE/$t"
  tid="$(azd "$d" account show --query tenantId -o tsv 2>/dev/null)" || die "not signed in to $t: run login $t"
  azd "$d" account list --query "[?tenantId=='$tid'].[name, state, id]" -o tsv | while IFS=$'\t' read -r n s i; do
    printf '  %-45s %-9s %s\n' "$n" "$s" "${i:0:4}****"; done
  echo "  Set $(echo "$t" | tr a-z A-Z)_SUB_DEV / _SUB_TEST / _SUB_PROD to these names in the environment settings (new session), or export them now and run finish $t.";;
status)
  [ -d "$BASE" ] || { echo "no sign-ins in this container yet (new container or reclaimed): run login <tenant>"; exit 0; }
  printf '%-10s %-5s %-8s %s\n' TENANT ENV STATE SUBSCRIPTION
  for p in "$BASE"/*/; do
    p="$(basename "$p")"; case "$p" in *-dev|*-test|*-prod) t="${p%-*}"; e="${p##*-}";; *) t="$p"; e="-";; esac
    if [ "$e" = - ] && [ -f "$BASE/$p/login.log" ] && [ ! -f "$BASE/$p/login.rc" ]; then printf '%-10s %-5s %-8s %s\n' "$t" "$e" WAITING "browser sign-in not finished"; continue; fi
    if ! azd "$BASE/$p" account get-access-token --query expiresOn -o none 2>/dev/null; then printf '%-10s %-5s %-8s %s\n' "$t" "$e" EXPIRED "sign in again: login $t"
    elif [ "$e" = - ]; then printf '%-10s %-5s %-8s %s\n' "$t" "$e" OK "tenant sign-in $(azd "$BASE/$p" account show --query tenantId -o tsv | cut -c1-4)**** (base for the environment profiles)"
    else
      printf '%-10s %-5s %-8s %s (%s****) tenant %s****\n' "$t" "$e" OK "$(azd "$BASE/$p" account show --query name -o tsv)" \
        "$(azd "$BASE/$p" account show --query id -o tsv | cut -c1-4)" "$(azd "$BASE/$p" account show --query tenantId -o tsv | cut -c1-4)"
    fi
  done;;
dir)
  t="${1:-}"; e="${2:-}"; tname "$t"
  case "$e" in dev|test|prod) ;; *) die "env: dev | test | prod";; esac
  [ -d "$BASE/$t-$e" ] || die "no $t $e profile: run login $t, then finish $t (with $(echo "$t" | tr a-z A-Z)_SUB_$(echo "$e" | tr a-z A-Z) set)"
  [ "$e" = prod ] && echo "NOTE: PROD profile ($t). Changes still need the dry run and an explicit approval." >&2
  echo "$BASE/$t-$e";;
logout)
  t="${1:-}"; tname "$t"
  pkill -f "az login --use-device-code.*$BASE/$t" 2>/dev/null || true
  for p in "$BASE/$t" "$BASE/$t"-*; do [ -d "$p" ] && { azd "$p" logout 2>/dev/null; rm -rf "$p"; }; done
  echo "OK      signed out of $t; its profiles are deleted";;
*) sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'; exit 2;;
esac
