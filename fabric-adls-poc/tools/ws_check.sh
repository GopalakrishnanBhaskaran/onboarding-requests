#!/usr/bin/env bash
# Read-only: can an existing Fabric workspace be used for this POC?
# Usage: bash ws_check.sh <workspace id from the URL: app.fabric.microsoft.com/groups/<id>/...>
# Checks, from this Cloud Shell sign-in (= the storage account's tenant):
#   1. the workspace is visible   -> same tenant (TWA "isn't compatible with cross-tenant requests", FB32)
#   2. its capacity SKU and region -> F SKU needed for trusted workspace access (Trial not supported, FB1/FB32)
#   3. workspace identity, your role in the workspace
# Changes nothing. Full values on screen; the evidence copy is masked.
source "$(dirname "$0")/lib.sh"
need az; need python3
az_preflight
WSID="${1:-}"
case "$WSID" in *[!0-9a-fA-F-]*|"") die "usage: bash ws_check.sh <workspace id (GUID from the Fabric URL after /groups/)>";; esac
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }
ME_OID="$(az ad signed-in-user show --query id -o tsv)"
OUT="$RAW_DIR/ws_check.txt"; : > "$OUT"
say() { echo "$*" | tee -a "$OUT"; }

say "== Workspace check $(date -u +%FT%TZ)"
if ! W="$(fab "/workspaces/$WSID" 2>&1)"; then
  say "FAIL    workspace not visible from this sign-in: $(echo "$W" | grep -oE '"(errorCode|code)": *"[^"]+"' | head -1)"
  say "        Either it is in ANOTHER TENANT, or this account has no role in it."
  say "        Another tenant: trusted workspace access can't reach this storage (cross-tenant not supported), so"
  say "        the shortcut and pipeline tests can't use it. Add this account to the workspace (Manage access) if it is the same tenant."
  save "WS-check.txt" < "$OUT" >/dev/null; exit 0
fi
NAME="$(echo "$W" | j "d.get('displayName','')")"
CAPID="$(echo "$W" | j "(d.get('capacityId') or '').lower()")"
WI="$(echo "$W" | j "'yes' if (d.get('workspaceIdentity') or {}).get('servicePrincipalId') else 'no'")"
say "OK      workspace visible from this sign-in => same tenant as the storage account"
say "        name: $NAME"
say "        workspace identity: $WI"
ROLE="$(fab "/workspaces/$WSID/roleAssignments" 2>/dev/null | j "next((r['role'] for r in d['value'] if r['principal']['id']=='$ME_OID'),'not listed (you may hold it through a group)')" 2>/dev/null || echo 'unknown (listing roles needs Member or Admin)')"
say "        your role: $ROLE"

SKU=""; REGION=""; CSTATE=""; CNAME=""
if [ -z "$CAPID" ]; then say "WARN    workspace is not on a capacity (shared/Pro)"; else
  C="$(fab /capacities 2>/dev/null | j "next(({'n':c['displayName'],'s':c['sku'],'r':c['region'],'st':c['state']} for c in d['value'] if c['id'].lower()=='$CAPID'),{})" 2>/dev/null || echo '{}')"
  if [ "$C" = "{}" ]; then
    C="$(az rest --method get --resource https://analysis.windows.net/powerbi/api --url https://api.powerbi.com/v1.0/myorg/admin/capacities 2>/dev/null \
         | j "next(({'n':c['displayName'],'s':c['sku'],'r':c.get('region',''),'st':c.get('state','')} for c in d['value'] if c['id'].lower()=='$CAPID'),{})" 2>/dev/null || echo '{}')"
  fi
  CNAME="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('n',''))" "$C")"
  SKU="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('s',''))" "$C")"
  REGION="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('r',''))" "$C")"
  CSTATE="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('st',''))" "$C")"
  if [ -n "$SKU" ]; then say "        capacity: $CNAME  SKU $SKU  region $REGION  state $CSTATE"
  else say "WARN    capacity $CAPID not visible to you (not a capacity admin/contributor, and the admin API gave nothing)"; fi
fi

say "== What this workspace can do for the POC"
case "$SKU" in
  FT*|*Trial*|*trial*) say "NO      shortcut (TWA)   Trial capacity is not supported"; say "OK      notebook (MPE)   Trial is supported (FB10)"; say "NO      pipeline         TWA needs an F SKU; VNet gateway lists F, P, A4+ only (FB12)"
       say "        => to run all three tests, move this workspace to an F capacity (e.g. fabpocf2 from b_fabric_identity.sh)";;
  F[0-9]*)  say "OK      shortcut (TWA)   F SKU"; say "OK      notebook (MPE)   F SKU"; say "OK      pipeline (TWA or VNet gateway; gateway: F8+ recommended, FB12)";;
  P*)  say "NO      shortcut (TWA)   needs an F SKU"; say "UNVERIFIED notebook (MPE) on a P SKU"; say "OK      pipeline         VNet gateway supports P (FB12)";;
  "")  say "UNVERIFIED  capacity SKU unknown: ask the capacity admin, or run this as a capacity admin/contributor";;
  *)   say "NO      SKU $SKU is not an F SKU";;
esac
[ -n "$REGION" ] && say "        capacity region $REGION, storage region centralindia (cross-region works; the gateway runs in the VNet region, FB12)"
save "WS-check.txt" < "$OUT" >/dev/null
echo "Paste this output back. Next, if it is usable: Agent B with --workspace \"$NAME\"${CNAME:+ and --capacity $CNAME}."
