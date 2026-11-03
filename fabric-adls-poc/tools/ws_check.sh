#!/usr/bin/env bash
# Read-only: can an existing Fabric workspace be used for this POC?
# Usage: bash ws_check.sh <workspace id from the URL: app.fabric.microsoft.com/groups/<id>/...>
# Checks, from this Cloud Shell sign-in:
#   0. the POC storage account is in the tenant you are signed in to (TWA "isn't compatible with cross-tenant requests", FB32)
#   1. the workspace is visible from the same sign-in
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
# The storage account must be in the SAME tenant as the workspace (TWA isn't compatible with cross-tenant requests, FB32).
# Fabric answers for the tenant you are signed in to, so check that the POC storage account is in that tenant too.
TEN="$(az account show --query tenantId -o tsv)"
ME_UPN="$(az ad signed-in-user show --query userPrincipalName -o tsv 2>/dev/null || echo '?')"
say "        signed in to tenant $(echo "$TEN" | mask) as $(case "$ME_UPN" in *"#EXT#"*) echo 'an external (#EXT#) account';; *) echo 'a member account';; esac)"
STG="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"]["storageAccount"])' "$STATE_FILE")"
STG_SUB=""
for s in $(az account list --query "[?tenantId=='$TEN'].id" -o tsv); do
  if [ -n "$(az storage account list --subscription "$s" --query "[?name=='$STG'].id | [0]" -o tsv 2>/dev/null)" ]; then STG_SUB="$s"; break; fi
done
if [ -n "$STG_SUB" ]; then SAME_TENANT=yes; say "OK      POC storage account $STG is in this tenant (subscription $(echo "$STG_SUB" | mask))"
else SAME_TENANT=no; say "FAIL    POC storage account $STG is NOT visible in this tenant: the workspace and the storage are in different tenants"
     say "        (or this Cloud Shell is signed in to another directory). Trusted workspace access can't work cross-tenant (FB32)."; fi
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
say "OK      workspace visible from this sign-in (tenant $(echo "$TEN" | mask))"
say "        name: $NAME"
say "        workspace identity: $WI"
ROLE="$(fab "/workspaces/$WSID/roleAssignments" 2>/dev/null | j "next((r['role'] for r in d['value'] if r['principal']['id']=='$ME_OID'),'not listed (you may hold it through a group)')" 2>/dev/null || echo 'unknown (listing roles needs Member or Admin)')"
say "        your role: $ROLE"

SKU=""; REGION=""; CSTATE=""; CNAME=""
if [ -z "$CAPID" ]; then say "WARN    workspace is not on a capacity (shared/Pro)"; else
  C="$(fab /capacities 2>/dev/null | j "next(({'n':c['displayName'],'s':c['sku'],'r':c['region'],'st':c['state']} for c in d['value'] if c['id'].lower()=='$CAPID'),{})" 2>/dev/null || echo '{}')"
  ACC=member; if [ "$C" = "{}" ]; then ACC=fabric-admin
    C="$(az rest --method get --resource https://analysis.windows.net/powerbi/api --url https://api.powerbi.com/v1.0/myorg/admin/capacities 2>/dev/null \
         | j "next(({'n':c['displayName'],'s':c['sku'],'r':c.get('region',''),'st':c.get('state','')} for c in d['value'] if c['id'].lower()=='$CAPID'),{})" 2>/dev/null || echo '{}')"
  fi
  CNAME="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('n',''))" "$C")"
  SKU="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('s',''))" "$C")"
  REGION="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('r',''))" "$C")"
  CSTATE="$(python3 -c "import ast,sys;print(ast.literal_eval(sys.argv[1]).get('st',''))" "$C")"
  if [ -n "$SKU" ]; then say "        capacity: $CNAME  SKU $SKU  region $REGION  state $CSTATE  (you: $([ "$ACC" = member ] && echo 'capacity admin or contributor' || echo 'Fabric admin only'))"
  else say "WARN    capacity $CAPID not visible to you (not a capacity admin/contributor, and the admin API gave nothing)"; fi
fi

say "== What this workspace can do for the POC"
if [ "$SAME_TENANT" = no ]; then
  say "NO      shortcut + pipeline over trusted workspace access: workspace and storage are in different tenants (FB32)"
  say "        Options: (1) sign Cloud Shell in to the storage tenant and use a workspace there, or"
  say "                 (2) build the POC storage set in THIS tenant (w1_build_env.sh + Agent A; needs a new approval)."
  save "WS-check.txt" < "$OUT" >/dev/null; exit 0
fi
case "$SKU" in
  FT*|*Trial*|*trial*) say "NO      shortcut (TWA)   Trial capacity is not supported"; say "OK      notebook (MPE)   Trial is supported (FB10)"; say "NO      pipeline         TWA needs an F SKU; VNet gateway lists F, P, A4+ only (FB12)"
       say "        => to run all three tests, move this workspace to an F capacity (e.g. fabpocf2 from b_fabric_identity.sh)";;
  F[0-9]*)  say "OK      shortcut (TWA)   F SKU"; say "OK      notebook (MPE)   F SKU"; say "OK      pipeline (TWA or VNet gateway; gateway: F8+ recommended, FB12)";;
  P*)  say "NO      shortcut (TWA)   needs an F SKU"; say "UNVERIFIED notebook (MPE) on a P SKU"; say "OK      pipeline         VNet gateway supports P (FB12)";;
  "")  say "UNVERIFIED  capacity SKU unknown: ask the capacity admin, or run this as a capacity admin/contributor";;
  *)   say "NO      SKU $SKU is not an F SKU";;
esac
SREG="$(az storage account list --subscription "${STG_SUB:-x}" --query "[?name=='$STG'].location | [0]" -o tsv 2>/dev/null || true)"
[ -n "$REGION" ] && say "        capacity region $REGION, storage region ${SREG:-unknown} (cross-region works; the gateway runs in the VNet region, FB12)"
save "WS-check.txt" < "$OUT" >/dev/null
echo "Paste this output back. Next, if it is usable: Agent B with --workspace \"$NAME\"${CNAME:+ and --capacity $CNAME}."
