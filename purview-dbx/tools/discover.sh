#!/usr/bin/env bash
# Phase 1 discovery: READ-ONLY. Changes nothing in Azure, Databricks or Purview.
# Run where Azure CLI is signed in as you (your Mac, Cloud Shell, or a device-code session).
# It never asks for or types credentials: if you are not signed in it stops.
#
#   bash purview-dbx/tools/discover.sh [--purview <account-name>]
#
# Output: state.discovery.* in state/state.json, report/01-discovery.md (local, not in git),
#         logs/discover.log (tokens masked).
set -euo pipefail
TOOL=discover
AGENT=orchestrator
. "$(cd "$(dirname "$0")" && pwd)/lib.sh"
need az; need python3; need curl

PURVIEW=""
while [ $# -gt 0 ]; do
  case "$1" in
    --purview) PURVIEW="${2:-}"; shift ;;
    *) die "unknown argument $1" ;;
  esac
  shift
done

az account show >/dev/null 2>&1 || die "Azure CLI is not signed in. Run 'az login' yourself (this tool never handles credentials), then re-run."

WS_ID=$(state get inputs.workspace_id)
ACCT=$(state get inputs.databricks_account_id)
CAT=$(state get inputs.scan_catalog); SCH=$(state get inputs.scan_schema); TBL=$(state get inputs.scan_table)
[ -n "$PURVIEW" ] && state set inputs.purview_account_name "\"$PURVIEW\""
D=$(mktemp -d); trap 'rm -rf "$D"' EXIT
state step orchestrator discovery running >/dev/null

log "1/7 signed-in context"
az account show --query "{tenantId:tenantId, user:user.name, userType:user.type, defaultSubscription:id}" -o json >"$D/ctx.json"
echo "{\"ip\": \"$(curl -4 -sS --max-time 10 https://ifconfig.me || echo unknown)\"}" >"$D/runner.json"

log "2/7 subscriptions (tenant check)"
arg_query "resourcecontainers | where type =~ 'microsoft.resources/subscriptions' | project subscriptionId, name, tenantId, state=tostring(properties.state)" >"$D/subs.json"

log "3/7 Databricks workspace $WS_ID (all subscriptions you can read)"
arg_query "resources | where type =~ 'microsoft.databricks/workspaces' | where tostring(properties.workspaceId) == '$WS_ID' | project id, name, subscriptionId, resourceGroup, location, tenantId, sku=tostring(sku.name), properties" >"$D/ws.json"
n=$(python3 -c 'import json,sys;print(len(json.load(open(sys.argv[1]))))' "$D/ws.json")
[ "$n" = "1" ] || die "found $n workspaces with id $WS_ID (need exactly 1). Do you have Reader on its subscription?"
WS_URL=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["properties"]["workspaceUrl"])' "$D/ws.json")
WS_ARM=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["id"])' "$D/ws.json")
WS_LOC=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["location"])' "$D/ws.json")
# Managed resource group: workspace storage account, NAT/LB left from a managed VNet.
MRG=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))[0]["properties"]["managedResourceGroupId"])' "$D/ws.json")
arg_query "resources | where id startswith '$MRG/' | project name, type, location, sku=tostring(sku.name)" >"$D/mrg.json" || echo "[]" >"$D/mrg.json"

log "4/7 Purview account(s) and system-assigned MI"
arg_query "resources | where type =~ 'microsoft.purview/accounts' | project id, name, subscriptionId, resourceGroup, location, tenantId, identity, sku, properties" >"$D/pv_all.json"
python3 - "$D/pv_all.json" "$PURVIEW" >"$D/pv.json" <<'PY'
import json, sys
allp, want = json.load(open(sys.argv[1])), sys.argv[2]
pick = [p for p in allp if not want or p["name"].lower() == want.lower()]
print(json.dumps(pick[0] if len(pick) == 1 else {"choose_one": [p["name"] for p in allp]}))
PY
PV_PID=$(python3 -c 'import json,sys;d=json.load(open(sys.argv[1]));print((d.get("identity") or {}).get("principalId",""))' "$D/pv.json")
if [ -n "$PV_PID" ]; then
  az ad sp show --id "$PV_PID" --query "{appId:appId, objectId:id, displayName:displayName}" -o json >"$D/pv_sp.json" 2>/dev/null \
    || az rest --method get --url "https://graph.microsoft.com/v1.0/servicePrincipals/$PV_PID?\$select=appId,id,displayName" -o json >"$D/pv_sp.json" 2>/dev/null \
    || echo '{"error":"could not read the MI service principal from Microsoft Graph"}' >"$D/pv_sp.json"
else
  echo '{"error":"no Purview account selected or no system-assigned MI; re-run with --purview <name>"}' >"$D/pv_sp.json"
fi
PV_APP=$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1])).get("appId",""))' "$D/pv_sp.json")

log "5/7 network inventory (address spaces, private DNS zones, private endpoints, NAT, Bastion)"
arg_query "resources | where type =~ 'microsoft.network/virtualnetworks' | project name, subscriptionId, resourceGroup, location, prefixes=properties.addressSpace.addressPrefixes, peerings=array_length(properties.virtualNetworkPeerings)" >"$D/vnets.json"
arg_query "resources | where type =~ 'microsoft.network/privatednszones' | project name, subscriptionId, resourceGroup, links=toint(properties.numberOfVirtualNetworkLinks)" >"$D/zones.json"
arg_query "resources | where type =~ 'microsoft.network/privateendpoints' | mv-expand c = properties.privateLinkServiceConnections | project name, subscriptionId, resourceGroup, target=tostring(c.properties.privateLinkServiceId), groupIds=c.properties.groupIds, state=tostring(c.properties.privateLinkServiceConnectionState.status)" >"$D/pes.json"
arg_query "resources | where type in~ ('microsoft.network/natgateways','microsoft.network/bastionhosts','microsoft.containerservice/managedclusters') | project name, type, subscriptionId, resourceGroup, location" >"$D/netmisc.json"

log "6/7 Databricks workspace REST (read-only) at $WS_URL"
H="https://$WS_URL"
get() { # $1 = name, $2 = path
  local status
  status=$(dbx_call GET "$H$2" "$D/raw_$1")
  python3 -c 'import json,sys
s, f = sys.argv[1], sys.argv[2]
try: b = open(f).read()
except Exception: b = ""
try: j = json.loads(b) if b.strip() else None
except Exception: j = {"raw": b[:300]}
print(json.dumps({"status": int(s) if s.isdigit() else s, "body": j}))' "$status" "$D/raw_$1" >"$D/dbx_$1.json"
}
get me "/api/2.0/preview/scim/v2/Me"
get metastore "/api/2.1/unity-catalog/current-metastore-assignment"
get metastore_summary "/api/2.1/unity-catalog/metastore_summary"
get catalog "/api/2.1/unity-catalog/catalogs/$CAT"
get table "/api/2.1/unity-catalog/tables/$CAT.$SCH.$TBL"
get warehouses "/api/2.0/sql/warehouses"
get ipacl_conf "/api/2.0/workspace-conf?keys=enableIpAccessLists"
get ipacl "/api/2.0/ip-access-lists"
get clusters "/api/2.0/clusters/list"
if [ -n "$PV_APP" ]; then
  get sp "/api/2.0/preview/scim/v2/ServicePrincipals?filter=applicationId%20eq%20%22$PV_APP%22"
  get grants_catalog "/api/2.1/unity-catalog/effective-permissions/catalog/$CAT?principal=$PV_APP"
  get grants_table "/api/2.1/unity-catalog/effective-permissions/table/$CAT.$SCH.$TBL?principal=$PV_APP"
fi

log "7/7 Databricks account REST (read-only; 401/403 = not an account admin)"
H="https://accounts.azuredatabricks.net"
get acct_ws "/api/2.0/accounts/$ACCT/workspaces/$WS_ID"
get acct_ipacl "/api/2.0/accounts/$ACCT/ip-access-lists"
[ -n "$PV_APP" ] && get acct_sp "/api/2.0/accounts/$ACCT/scim/v2/ServicePrincipals?filter=applicationId%20eq%20%22$PV_APP%22"

log "summarising"
python3 - "$D" "$PVD_ROOT" "$WS_ID" "$CAT.$SCH.$TBL" <<'PY'
import glob, json, os, sys
D, ROOT, WS_ID, FULL = sys.argv[1:5]
L = lambda n, dflt=None: json.load(open(os.path.join(D, n))) if os.path.exists(os.path.join(D, n)) else dflt
ctx, subs, ws, pv, sp = L("ctx.json"), L("subs.json", []), L("ws.json")[0], L("pv.json", {}), L("pv_sp.json", {})
p = ws["properties"]; par = p.get("parameters") or {}
v = lambda k: (par.get(k) or {}).get("value")
dbx = {os.path.basename(f)[4:-5]: json.load(open(f)) for f in glob.glob(os.path.join(D, "dbx_*.json"))}
B = lambda k: (dbx.get(k) or {}).get("body") or {}
S = lambda k: (dbx.get(k) or {}).get("status")
pp = pv.get("properties") or {}
sub_t = {s["subscriptionId"]: s["tenantId"] for s in subs}
me = B("me")
wh = [{"id": w.get("id"), "name": w.get("name"), "type": w.get("warehouse_type"), "serverless": w.get("enable_serverless_compute"),
       "size": w.get("cluster_size"), "state": w.get("state"), "http_path": (w.get("odbc_params") or {}).get("path"),
       "auto_stop_mins": w.get("auto_stop_mins")} for w in B("warehouses").get("warehouses", [])]
tbl = B("table")
running = [c.get("cluster_name") for c in B("clusters").get("clusters", []) if c.get("state") in ("RUNNING", "PENDING", "RESIZING", "RESTARTING")]
mrg = L("mrg.json", [])
summary = {
  "context": ctx, "runner_public_ip": L("runner.json"),
  "tenant": {"signed_in_tenant": ctx["tenantId"], "workspace_tenant": ws.get("tenantId") or sub_t.get(ws["subscriptionId"]),
             "purview_tenant": pv.get("tenantId") or sub_t.get(pv.get("subscriptionId")),},
  "workspace": {
    "arm_id": ws["id"], "name": ws["name"], "subscription": ws["subscriptionId"], "resource_group": ws["resourceGroup"],
    "region": ws["location"], "sku": ws.get("sku"), "url": p.get("workspaceUrl"),
    "managed_resource_group": p.get("managedResourceGroupId"),
    "vnet_injected": bool(v("customVirtualNetworkId")), "custom_vnet_id": v("customVirtualNetworkId"),
    "secure_cluster_connectivity": v("enableNoPublicIp"), "public_network_access": p.get("publicNetworkAccess"),
    "required_nsg_rules": p.get("requiredNsgRules"), "storage_account": v("storageAccountName"),
    "default_storage_firewall": p.get("defaultStorageFirewall"), "access_connector": p.get("accessConnector"),
    "load_balancer_configured": bool(v("loadBalancerId") or v("loadBalancerBackendPoolName")),
    "managed_nat_gateway": v("natGatewayName"), "managed_vnet_prefix": v("vnetAddressPrefix"),
    "private_endpoint_connections": [{"name": c.get("name"), "group_ids": (c.get("properties") or {}).get("groupIds"),
        "state": ((c.get("properties") or {}).get("privateLinkServiceConnectionState") or {}).get("status")} for c in p.get("privateEndpointConnections") or []],
    "managed_rg_resources": mrg, "provisioning_state": p.get("provisioningState"),
  },
  "purview": {
    "name": pv.get("name"), "choose_one": pv.get("choose_one"), "arm_id": pv.get("id"), "subscription": pv.get("subscriptionId"),
    "resource_group": pv.get("resourceGroup"), "region": pv.get("location"), "sku": pv.get("sku"),
    "mi_object_id": (pv.get("identity") or {}).get("principalId"), "mi_type": (pv.get("identity") or {}).get("type"),
    "mi_app_id": sp.get("appId"), "mi_display_name": sp.get("displayName"), "mi_lookup_error": sp.get("error"),
    "public_network_access": pp.get("publicNetworkAccess"), "managed_resources_public_network_access": pp.get("managedResourcesPublicNetworkAccess"),
    "managed_event_hub_state": pp.get("managedEventHubState"), "ingestion_storage": pp.get("ingestionStorage"),
    "managed_resource_group": pp.get("managedResourceGroupName"), "endpoints": pp.get("endpoints"),
    "tenant_endpoint_state": pp.get("tenantEndpointState"), "account_status": pp.get("accountStatus"),
    "private_endpoint_connections": [{"name": c.get("name"), "group_ids": (c.get("properties") or {}).get("groupIds")} for c in pp.get("privateEndpointConnections") or []],
  },
  "databricks": {
    "caller": me.get("userName"), "caller_is_workspace_admin": any(g.get("display") == "admins" for g in me.get("groups", [])),
    "http_status": {k: S(k) for k in sorted(dbx)},
    "metastore_id": B("metastore").get("metastore_id"), "metastore_name": B("metastore_summary").get("name"),
    "metastore_region": B("metastore_summary").get("region"), "default_catalog": B("metastore").get("default_catalog_name"),
    "catalog_exists": S("catalog") == 200, "catalog_owner": B("catalog").get("owner"),
    "table": {"full_name": FULL, "exists": S("table") == 200, "type": tbl.get("table_type"), "owner": tbl.get("owner"),
              "columns": [c.get("name") for c in tbl.get("columns", [])]},
    "warehouses": wh, "ip_access_lists_enabled": (B("ipacl_conf") or {}).get("enableIpAccessLists"),
    "ip_access_lists": [{"label": x.get("label"), "type": x.get("list_type"), "enabled": x.get("enabled"), "count": x.get("address_count")} for x in B("ipacl").get("ip_access_lists", [])],
    "running_clusters": running,
    "purview_sp_in_workspace": bool(B("sp").get("Resources")) if "sp" in dbx else None,
    "purview_sp_in_account": bool(B("acct_sp").get("Resources")) if "acct_sp" in dbx else None,
    "purview_effective_catalog": B("grants_catalog").get("privilege_assignments") if "grants_catalog" in dbx else None,
    "purview_effective_table": B("grants_table").get("privilege_assignments") if "grants_table" in dbx else None,
    "account_workspace": B("acct_ws") if S("acct_ws") == 200 else {"status": S("acct_ws")},
    "account_ip_access_lists": B("acct_ipacl") if S("acct_ipacl") == 200 else {"status": S("acct_ipacl")},
  },
  "network": {"vnets": L("vnets.json", []), "private_dns_zones": L("zones.json", []), "private_endpoints": L("pes.json", []), "nat_bastion_aks": L("netmisc.json", [])},
}
t = summary["tenant"]
w = summary["workspace"]; d = summary["databricks"]
checks = [
  ("Both subscriptions in the signed-in tenant", t["workspace_tenant"] == t["purview_tenant"] == t["signed_in_tenant"]),
  ("Workspace is Premium", (w["sku"] or "").lower() == "premium"),
  ("Workspace is VNet-injected (needed for front-end Private Link)", w["vnet_injected"]),
  ("Secure cluster connectivity on", w["secure_cluster_connectivity"] is True),
  ("No Azure Load Balancer (migration prerequisite)", not w["load_balancer_configured"]),
  ("Purview account selected", bool(summary["purview"]["name"])),
  ("Purview MI application id found", bool(summary["purview"]["mi_app_id"])),
  ("Caller is workspace admin", d["caller_is_workspace_admin"]),
  ("Scan target table exists", d["table"]["exists"]),
  ("A SQL warehouse exists", bool(d["warehouses"])),
  ("No running clusters (migration needs none)", not d["running_clusters"]),
]
summary["checks"] = [{"check": c, "ok": bool(ok)} for c, ok in checks]
os.makedirs(os.path.join(ROOT, "report"), exist_ok=True)
json.dump(summary, open(os.path.join(D, "summary.json"), "w"), indent=2)
md = ["# Discovery (read-only)", "", f"Runner public IP (the machine that ran this, **not** necessarily your device): {summary['runner_public_ip']}", "",
      "| Item | Value |", "|---|---|"]
rows = [("Signed-in tenant", t["signed_in_tenant"]), ("Workspace tenant / Purview tenant", f"{t['workspace_tenant']} / {t['purview_tenant']}"),
  ("Workspace", f"{w['name']} ({w['sku']}, {w['region']})"), ("Subscription / RG", f"{w['subscription']} / {w['resource_group']}"),
  ("Managed RG", w["managed_resource_group"]), ("VNet injection", w["custom_vnet_id"] or "No (Databricks-managed VNet)"),
  ("SCC (No Public IP)", w["secure_cluster_connectivity"]), ("Public network access", w["public_network_access"]),
  ("Required NSG rules", w["required_nsg_rules"]), ("Workspace storage account", w["storage_account"]),
  ("Storage firewall", w["default_storage_firewall"]), ("Private endpoint connections", w["private_endpoint_connections"] or "none"),
  ("Purview account", f"{summary['purview']['name']} ({summary['purview']['region']}, sub {summary['purview']['subscription']})"),
  ("Purview MI object id / app id", f"{summary['purview']['mi_object_id']} / {summary['purview']['mi_app_id']}"),
  ("Purview public network access", f"{summary['purview']['public_network_access']} (managed resources: {summary['purview']['managed_resources_public_network_access']})"),
  ("Purview ingestion storage / Event Hubs", f"{summary['purview']['ingestion_storage']} / {summary['purview']['managed_event_hub_state']}"),
  ("Metastore", f"{d['metastore_name']} {d['metastore_id']} ({d['metastore_region']})"),
  ("Scan table", f"{FULL}: exists={d['table']['exists']}, columns={len(d['table']['columns'])}"),
  ("SQL warehouses", "; ".join(f"{x['name']} {x['type']} serverless={x['serverless']} {x['state']} {x['http_path']}" for x in d["warehouses"]) or "none"),
  ("IP access lists", f"enabled={d['ip_access_lists_enabled']} lists={d['ip_access_lists']}"),
  ("Running clusters", d["running_clusters"] or "none"),
  ("Purview SP in workspace / account", f"{d['purview_sp_in_workspace']} / {d['purview_sp_in_account']}"),
  ("Databricks HTTP status per call", d["http_status"])]
md += [f"| {k} | {v} |" for k, v in rows]
md += ["", "## Checks", ""] + [f"- {'PASS' if c['ok'] else 'FAIL'}: {c['check']}" for c in summary["checks"]]
open(os.path.join(ROOT, "report", "01-discovery.md"), "w").write("\n".join(md) + "\n")
print("\n".join(md))
PY
state set-file discovery "$D/summary.json"
state step orchestrator discovery done "report/01-discovery.md" >/dev/null
log "done: report/01-discovery.md and state.discovery"
