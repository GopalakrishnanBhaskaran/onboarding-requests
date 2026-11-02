#!/usr/bin/env python3
"""Compact discovery summary (stdout). The caller pipes it through mask() before showing it.
Usage: summarize_discovery.py <rawDir> <storageAccount> <prefix>   |   summarize_discovery.py --self-test"""
import json
import os
import re
import sys


def load(raw, name):
    p = os.path.join(raw, name)
    if not os.path.exists(p):
        return None
    txt = open(p).read().strip()
    if not txt:
        return None
    docs, dec, i = [], json.JSONDecoder(), 0
    while i < len(txt):
        while i < len(txt) and txt[i] in " \r\n\t":
            i += 1
        if i >= len(txt):
            break
        try:
            obj, i = dec.raw_decode(txt, i)
            docs.append(obj)
        except json.JSONDecodeError:
            return txt  # plain text file
    return docs[0] if len(docs) == 1 else docs


def last(rid, n=1):
    return "/".join(str(rid or "").rstrip("/").split("/")[-n:])


def section(title):
    print(f"\n## {title}")


def summarize(raw, stg, prefix):
    out = print
    acct = load(raw, "01-account.json") or {}
    section("Account")
    out(f"tenant={acct.get('tenantId')} subscription='{acct.get('name')}' id={acct.get('id')} user={acct.get('user', {}).get('name')} ({acct.get('user', {}).get('type')})")
    subs = load(raw, "01-subscriptions.json") or []
    out(f"subscriptions visible: {len(subs)}")
    section("Resource providers")
    out((load(raw, "01-providers.txt") or "").strip())

    section("HNS storage accounts")
    for r in load(raw, "01-storage-hns.json") or []:
        out(f"- {r['name']} rg={r['rg']} {r['location']} {r['sku']} public={r['publicNetworkAccess']} default={r['defaultAction']} bypass={r['bypass']} ip={r['ipRules']} vnet={r['vnetRules']} resRules={r['resourceRules']} pes={r['pes']} sharedKey={r['sharedKey']}")

    net = load(raw, "01-storage-network.json") or {}
    nrs = net.get("networkRuleSet") or {}
    section(f"Target storage {stg}")
    out(f"location={net.get('location')} hns={net.get('isHnsEnabled')} publicNetworkAccess={net.get('publicNetworkAccess')} defaultAction={nrs.get('defaultAction')} bypass={nrs.get('bypass')}")
    out(f"sharedKey={net.get('allowSharedKeyAccess')} blobPublic={net.get('allowBlobPublicAccess')} tls={net.get('minimumTlsVersion')} oauthDefault={net.get('defaultToOAuthAuthentication')}")
    out("ipRules: " + ", ".join(f"{r.get('ipAddressOrRange')}({r.get('action')})" for r in nrs.get("ipRules") or []) or "ipRules: none")
    out("vnetRules: " + (", ".join(f"{last(r.get('virtualNetworkResourceId'), 3)}({r.get('state')})" for r in nrs.get("virtualNetworkRules") or []) or "none"))
    out("resourceAccessRules: " + (", ".join(f"{'/'.join(str(r.get('resourceId')).split('/')[-3:-1])}/{last(r.get('resourceId'))}" for r in nrs.get("resourceAccessRules") or []) or "none"))
    pec = load(raw, "01-storage-pe-connections.json") or []
    out("PE connections: " + (", ".join(f"{last(c.get('properties', {}).get('privateEndpoint', {}).get('id'))}[{','.join(c.get('properties', {}).get('groupIds') or [])}]={c.get('properties', {}).get('privateLinkServiceConnectionState', {}).get('status')}" for c in pec) or "none"))
    out("containers: " + ", ".join(c["name"] for c in load(raw, "01-containers.json") or []))

    section("RBAC on the storage account (incl. inherited)")
    for r in load(raw, "01-storage-rbac.json") or []:
        scope = r.get("scope", "")
        lvl = "container" if "/containers/" in scope else "account" if "/storageAccounts/" in scope else "rg" if "/resourceGroups/" in scope else "subscription+"
        out(f"- {r.get('role')} | {r.get('principalType')} | {r.get('principalName')} | {lvl}")

    section(f"{prefix} VNets")
    for v in load(raw, "01-vnets.json") or []:
        out(f"- {v['name']} rg={v['rg']} {v['location']} space={v['addressSpace']} dns={v.get('dns') or 'Azure'} peerings={v['peerings']}")
        for s in v.get("subnets") or []:
            out(f"    {s['name']} {s.get('prefix') or s.get('prefixes')} deleg={s.get('delegations') or []} pePolicies={s.get('peNetworkPolicies')} nsg={last(s.get('nsg')) or '-'} svcEp={s.get('serviceEndpoints') or []} used={s.get('ipConfigs')}")

    section(f"Private endpoints targeting {stg}")
    pes = [p for p in load(raw, "01-private-endpoints.json") or [] if f"/storageaccounts/{stg.lower()}" in str(p.get("target") or p.get("manual") or "").lower()]
    for p in pes:
        out(f"- {p['name']} rg={p['rg']} subnet={last(p.get('subnet'), 3)} group={p.get('groupIds')} status={p.get('status')} ips={p.get('ips')} fqdns={p.get('fqdns')}")
    if not pes:
        out("none")
    section("Private DNS (privatelink zones)")
    for z in load(raw, "01-dns-zones.json") or []:
        out(f"- {z['name']} rg={z['rg']} records={z['records']} links={z['links']}")
    links = load(raw, "01-dns-links-records.txt")
    if isinstance(links, str):
        for line in links.splitlines():
            line = line.strip()
            if line.startswith("##") or '"vnet"' in line or '"name"' in line or re.search(r"\d+\.\d+\.\d+\.\d+", line):
                out("  " + re.sub(r"/subscriptions/[^/]+/resourceGroups/", "…/", line))

    section("Fabric capacities")
    for c in (lambda x: x if isinstance(x, list) else [x])(load(raw, "01-fabric-capacities-arm.json") or []):
        out(f"- ARM {c.get('name')} {c.get('sku')} {c.get('location')} rg={c.get('rg')}")
    st = load(raw, "01-fabric-capacities-state.json")
    for c in (st if isinstance(st, list) else [st] if st else []):
        out(f"  state {c.get('name')} {c.get('sku', {}).get('name')} {c.get('state')} admins={len(c.get('admins') or [])}")
    api = load(raw, "01-fabric-capacities-api.json") or {}
    caps = {c["id"]: c for c in api.get("value", [])} if isinstance(api, dict) else {}
    for c in caps.values():
        out(f"- API {c.get('displayName')} sku={c.get('sku')} region={c.get('region')} state={c.get('state')}")
    if isinstance(api, dict) and api.get("error"):
        out(f"API: {api['error']}")

    section("Fabric workspaces")
    ws = load(raw, "01-fabric-workspaces.json") or {}
    details = load(raw, "01-fabric-workspace-details.txt")
    det = {}
    if isinstance(details, str):
        for block in details.split("## ")[1:]:
            head = block.splitlines()[0]
            det[head.split(" capacity=")[0]] = ("workspaceIdentity" in block, block.count('"targetPrivateLinkResourceId"'))
    for w in ws.get("value", []) if isinstance(ws, dict) else []:
        cap = caps.get(w.get("capacityId"), {})
        ident, mpe = det.get(w["displayName"].replace(" ", "_"), (None, None))
        out(f"- {w['displayName']} type={w.get('type')} capacity={cap.get('displayName', w.get('capacityId') or 'shared')} sku={cap.get('sku', '-')} identity={ident} managedPEs={mpe}")
    if isinstance(ws, dict) and ws.get("error"):
        out(f"API: {ws['error']}")

    section("Fabric gateways")
    gw = load(raw, "01-fabric-gateways.json") or {}
    for g in gw.get("value", []) if isinstance(gw, dict) else []:
        out(f"- {g.get('displayName')} type={g.get('type')}")
    section("Fabric tenant settings (filtered)")
    ts = load(raw, "01-fabric-tenant-settings.json")
    if isinstance(ts, list):
        for s in ts:
            out(f"- {s.get('name')}: {s.get('enabled')} groups={s.get('groups')} ({s.get('title')})")
    else:
        out(str(ts))

    section("Device / DNS")
    out((load(raw, "01-device.txt") or "").strip())


def self_test():
    import tempfile
    with tempfile.TemporaryDirectory() as d:
        w = lambda n, o: open(os.path.join(d, n), "w").write(o if isinstance(o, str) else json.dumps(o))
        w("01-account.json", {"tenantId": "t", "name": "Pilot", "id": "s", "user": {"name": "a@b.com", "type": "user"}})
        w("01-storage-hns.json", [{"name": "stgx", "rg": "rg", "location": "centralindia", "sku": "Standard_LRS", "publicNetworkAccess": "Disabled", "defaultAction": "Deny", "bypass": "AzureServices", "ipRules": 0, "vnetRules": 0, "resourceRules": 0, "pes": 2, "sharedKey": False}])
        w("01-storage-network.json", {"location": "centralindia", "networkRuleSet": {"defaultAction": "Deny", "bypass": "AzureServices", "ipRules": [], "resourceAccessRules": []}})
        w("01-fabric-capacities-state.json", '{"name":"a","sku":{"name":"F2"},"state":"Active"}\n{"name":"b","sku":{"name":"F4"},"state":"Paused"}')
        w("01-fabric-workspace-details.txt", '## ws_one capacity=c1\n{"id":"1","workspaceIdentity":{"applicationId":"x"}}\n{"value":[]}\n')
        w("01-fabric-workspaces.json", {"value": [{"id": "1", "displayName": "ws one", "type": "Workspace", "capacityId": "c1"}]})
        import io, contextlib
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            summarize(d, "stgx", "pvdbx-")
        s = buf.getvalue()
        assert "public=Disabled" in s and "state b F4 Paused" in s and "identity=True" in s, s
    print("summarize_discovery self-test: passed")


if __name__ == "__main__":
    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        summarize(*sys.argv[1:4])
