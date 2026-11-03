#!/usr/bin/env bash
# Read-only: find storage accounts whose name, resource group or tags contain a word, in EVERY subscription
# of the tenant you are signed in to. Shows what matters for this POC (HNS, network, private endpoints).
# Usage: bash find_storage.sh <word> [<word> ...]      e.g. bash find_storage.sh scanntech scan
source "$(dirname "$0")/lib.sh"
need az; need python3
[ $# -gt 0 ] || die "usage: bash find_storage.sh <word> [<word> ...]"
TEN="$(az account show --query tenantId -o tsv)"
OUT="$RAW_DIR/find_storage.json"; : > "$OUT.l"
n=0
for s in $(az account list --query "[?tenantId=='$TEN' && state=='Enabled'].id" -o tsv); do
  n=$((n+1))
  az storage account list --subscription "$s" -o json 2>/dev/null | python3 -c '
import json,sys
sub=sys.argv[1]; words=[w.lower() for w in sys.argv[2:]]
for a in json.load(sys.stdin) or []:
    tags=a.get("tags") or {}
    hay=" ".join([a.get("name",""),a.get("resourceGroup","")]+[f"{k}={v}" for k,v in tags.items()]).lower()
    if any(w in hay for w in words):
        n=a.get("networkRuleSet") or {}
        print(json.dumps({"name":a["name"],"sub":sub,"rg":a.get("resourceGroup"),"location":a.get("location"),"kind":a.get("kind"),
          "hns":a.get("isHnsEnabled"),"public":a.get("publicNetworkAccess"),"default":n.get("defaultAction"),"bypass":n.get("bypass"),
          "ipRules":len(n.get("ipRules") or []),"resourceRules":len(n.get("resourceAccessRules") or []),
          "pes":len(a.get("privateEndpointConnections") or []),
          "matchedTags":{k:v for k,v in tags.items() if any(w in f"{k}={v}".lower() for w in words)}}))' "$s" "$@" >> "$OUT.l" || true
done
python3 - "$OUT.l" "$n" "$@" <<'PY' | tee "$OUT.txt"
import json,sys
rows=[json.loads(l) for l in open(sys.argv[1]) if l.strip()]; nsub=sys.argv[2]; words=" ".join(sys.argv[3:])
print(f"== storage accounts matching '{words}' in {nsub} subscription(s) of this tenant: {len(rows)}")
for r in rows:
    print(f"- {r['name']}  rg={r['rg']}  {r['location']}  sub={r['sub'][:4]}****  kind={r['kind']}  HNS={r['hns']}")
    print(f"    public={r['public']} default={r['default']} bypass={r['bypass']} ipRules={r['ipRules']} resourceRules={r['resourceRules']} PEs={r['pes']}"
          + (f"  tags={r['matchedTags']}" if r['matchedTags'] else ""))
    ok = r['hns'] is True
    print("    POC fit: " + ("ADLS Gen2 (HNS on): usable" if ok else "HNS off: not ADLS Gen2, so OneLake ADLS shortcuts/abfss need an HNS account"))
    if ok and (r['public'] in (None,"Enabled")) and r['default']=="Allow":
        print("    network: OPEN to the internet today. Using it means adding private endpoints and Disabling public access first (a new, approved wave).")
    elif ok and r['public']=="Disabled" and r['pes']:
        print("    network: private already (public Disabled, PEs present), the same pattern as pesstddevdaiweu01")
if not rows: print("  none visible to you. It may be in a subscription you can't read, or named differently.")
PY
save "FIND-storage.txt" < "$OUT.txt" >/dev/null
