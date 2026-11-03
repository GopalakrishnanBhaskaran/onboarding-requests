#!/usr/bin/env bash
# Push the PEST-tenant part of the Fabric POC to an Azure DevOps repo, as YOU (Entra sign-in in Cloud Shell; no PAT).
# Default: DRY RUN (builds the export, scans it, lists files). --push pushes a new branch; --pr also opens a PR.
# Usage: bash push_to_ado.sh <https://dev.azure.com/<org>/<project>/_git/<repo>> [--branch feature/fabric-adls-poc]
#                            [--path fabric-adls-poc] [--push] [--pr]
# Exports: tools/, workflows/, report docs (tenant-2 parts), tenant-2 evidence (masked), state template without tenant-1 history.
# Never exports tenant-1 evidence, the Purview handoff, or anything the scan flags (keys, SAS, passwords, personal ids, full IPs).
source "$(dirname "$0")/lib.sh"
need az; need git; need python3; need curl
URL="${1:-}"; shift || true
BRANCH="feature/fabric-adls-poc"; DEST="fabric-adls-poc"; PUSH=0; PR=0
while [ $# -gt 0 ]; do case "$1" in --branch) BRANCH="$2"; shift 2;; --path) DEST="$2"; shift 2;; --push) PUSH=1; shift;; --pr) PUSH=1; PR=1; shift;; *) die "unknown argument $1";; esac; done
URL="$(echo "$URL" | sed -E 's#https://[^@/]+@#https://#')"   # copied clone URLs carry "org@"
echo "$URL" | grep -qE '^https://dev\.azure\.com/[^/]+/[^/]+/_git/[^/?#]+$' || die "usage: push_to_ado.sh https://dev.azure.com/<org>/<project>/_git/<repo> [...]"
echo "$URL" | grep -qi vestacy && die "refused: this POC is PEST-tenant only and must not go to a Vestacy repo (owner, 2026-11-03)"
ORG="$(echo "$URL" | cut -d/ -f4)"; PROJECT="$(echo "$URL" | cut -d/ -f5)"; REPO="$(echo "$URL" | cut -d/ -f7)"

# ---- 1. build the export ----
EX="$RAW_DIR/ado-export"; rm -rf "$EX"; mkdir -p "$EX/$DEST"/{tools,workflows,report,evidence,state}
cp "$POC_DIR"/tools/* "$EX/$DEST/tools/"; cp "$POC_DIR"/workflows/* "$EX/$DEST/workflows/"
for f in ARCHITECTURE.md RUNBOOK_DRAFT.md PERMISSIONS.md; do [ -f "$POC_DIR/report/$f" ] && cp "$POC_DIR/report/$f" "$EX/$DEST/report/"; done
cp "$POC_DIR"/evidence/T2-* "$POC_DIR"/evidence/PROBE-T2-* "$EX/$DEST/evidence/" 2>/dev/null || true
python3 - "$POC_DIR" "$EX/$DEST" <<'PY'
import json,re,sys,os
src,dst=sys.argv[1:3]
# network table: tenant-2 section only
t=open(f"{src}/report/NETWORK_BEFORE_AFTER.md").read()
i=t.find("# Tenant 2"); open(f"{dst}/report/NETWORK_BEFORE_AFTER.md","w").write("# Network / firewall: before and after\n\n"+(t[i:] if i>=0 else t))
# verified facts: drop rows that describe the owner's own (tenant-1) subscription
drop={"FB22","FB23","FB25","FB26","FB30"}
out=[l for l in open(f"{src}/report/VERIFIED_FACTS.md").read().splitlines() if not any(l.startswith(f"| {x} |") for x in drop)]
open(f"{dst}/report/VERIFIED_FACTS.md","w").write("\n".join(out)+"\n")
# state template: no tenant-1 history, no handoff before the tenant switch
d=json.load(open(f"{src}/state/state.json")); d.pop("history",None)
d["handoff"]=[h for h in d.get("handoff",[]) if h.get("at","")>="2026-11-03T01:17"]
for a in d.get("agents",{}).values(): a["notes"]=[n for n in a.get("notes",[]) if n[:16]>="2026-11-03T01:17"]
json.dump(d,open(f"{dst}/state/state.json","w"),indent=2)
open(f"{dst}/README.md","w").write("""# Fabric -> ADLS Gen2 (private) POC, PEST dev

Microsoft Fabric (public) reading and writing ADLS Gen2 `pesstddevdaiweu01` with public access **Disabled**:
trusted workspace access (shortcut), managed private endpoint (notebook), VNet data gateway (pipeline).

- Run from Azure Cloud Shell, signed in to the PEST tenant. Every tool is read-only unless `--apply` is given,
  and `--apply` needs the wave approval in `state/state.json` (`tools/state.py approve <wave> --by "<name> <date>"`).
- Steps you lack rights for become storage-admin requests (`tools/admin_request.sh` prints one e-mail).
- Facts and Microsoft Learn links with dates: `report/VERIFIED_FACTS.md`. Evidence is masked.
""")
PY

# ---- 2. scan: no secrets, no personal ids, no full public IPs, no unexpected GUIDs ----
python3 - "$EX" <<'PY' || die "scan failed: nothing pushed (fix the files listed above)"
import os,re,sys,ipaddress
root=sys.argv[1]; bad=[]
allow_guid={"00000000-0000-0000-0000-000000000000","db58b8e5-c6ad-4a2a-8342-4190687cbf4a","ba92f5b4-2d11-453d-a403-e96b0029c9fe","f58310d9-a9f6-439a-9e8d-f62e7b41a168","17d1049b-9a84-46fb-8f53-869881c3d3ab","499b84ac-1321-427f-aa17-267ca6975798"}
pats=[(r"AccountKey=[A-Za-z0-9+/]{30,}={0,2}|SharedAccessSignature=sv=[0-9]{4}-[0-9]{2}-|[?&]sig=[A-Za-z0-9%]{20,}","storage key/SAS value"),
      (r"(?i)password\s*[:=]\s*['\"]?[A-Za-z0-9!#$%^&*()_+=-]{8,}","password value"),(r"-----BEGIN [A-Z ]*PRIVATE KEY-----","private key"),
      (r"(?i)[a-z0-9._%+-]+@(gmail|outlook|hotmail|yahoo|live)\.[a-z.]+|gmail\.onmicrosoft\.com","personal mail / personal tenant"),
      (r"eyJ[A-Za-z0-9_-]{20,}\.eyJ[A-Za-z0-9_-]{20,}","token")]
for dp,_,fs in os.walk(root):
  for f in fs:
    p=os.path.join(dp,f); t=open(p,errors="ignore").read(); rel=os.path.relpath(p,root)
    for rx,what in pats:
      if re.search(rx,t): bad.append(f"{rel}: {what}")
    for g in set(re.findall(r"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b",t)):
      if g.lower() not in allow_guid: bad.append(f"{rel}: unmasked id {g[:4]}****")
    for ip in set(re.findall(r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b",t)):
      try:
        a=ipaddress.ip_address(ip)
        if a.is_global: bad.append(f"{rel}: full public IP {'.'.join(ip.split('.')[:2])}.x.x")
      except ValueError: pass
print("== scan:", "OK, nothing sensitive found" if not bad else "FOUND:"); [print("   ",b) for b in bad]
sys.exit(1 if bad else 0)
PY
echo "== export for $ORG / $PROJECT / $REPO, branch $BRANCH, folder $DEST/"
(cd "$EX" && find . -type f | sort | sed 's#^\./#   #')
if [ "$PUSH" = 0 ]; then echo "== DRY RUN: nothing pushed. Re-run with --push (branch only) or --pr (branch + pull request)."; exit 0; fi

# ---- 3. push as you (Entra token for Azure DevOps, kept in memory; never written to disk or logs) ----
TOK="$(az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798 --query accessToken -o tsv)" || die "no Azure DevOps token for this sign-in"
export GIT_CONFIG_COUNT=1 GIT_CONFIG_KEY_0=http.extraHeader GIT_CONFIG_VALUE_0="Authorization: Bearer $TOK"
WD="$RAW_DIR/ado-clone"; rm -rf "$WD"
git clone -q --depth 1 "$URL" "$WD" || die "clone failed (no access to $ORG/$PROJECT/$REPO, or the org blocks Entra tokens)"
DEF="$(git -C "$WD" symbolic-ref --short HEAD)"
git -C "$WD" ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1 && die "branch $BRANCH already exists in $REPO: choose another --branch"
git -C "$WD" checkout -q -b "$BRANCH"
mkdir -p "$WD/$DEST"; cp -r "$EX/$DEST/." "$WD/$DEST/"
ME_NAME="$(az ad signed-in-user show --query displayName -o tsv)"; ME_MAIL="$(az ad signed-in-user show --query "mail || userPrincipalName" -o tsv)"
git -C "$WD" add "$DEST"
git -C "$WD" -c user.name="$ME_NAME" -c user.email="$ME_MAIL" commit -q -m "Fabric -> ADLS Gen2 private access POC (PEST dev): tools, runbook drafts, verified facts, masked evidence"
git -C "$WD" push -q -u origin "$BRANCH" || die "push failed"
echo "OK      pushed branch $BRANCH to $URL (base $DEF)"
if [ "$PR" = 1 ]; then
  API="https://dev.azure.com/$ORG/$PROJECT/_apis/git/repositories/$REPO/pullrequests?api-version=7.1"
  BODY="$(python3 -c 'import json,sys;print(json.dumps({"sourceRefName":"refs/heads/"+sys.argv[1],"targetRefName":"refs/heads/"+sys.argv[2],"title":"Fabric -> ADLS Gen2 private access POC (PEST dev)","description":"Tools and runbook drafts for Fabric trusted workspace access, managed private endpoint and VNet data gateway to pesstddevdaiweu01 (public access Disabled). Evidence is masked. See fabric-adls-poc/README.md."}))' "$BRANCH" "$DEF")"
  R="$(curl -sS -m 60 -X POST -H "Authorization: Bearer $TOK" -H "Content-Type: application/json" -d "$BODY" "$API")"
  ID="$(echo "$R" | python3 -c 'import json,sys;print(json.load(sys.stdin).get("pullRequestId",""))' 2>/dev/null || true)"
  [ -n "$ID" ] && echo "OK      pull request $ID: $URL/pullrequest/$ID" || { echo "PR not created: $(echo "$R" | head -c 300)"; echo "Create it in the browser: $URL/pullrequestcreate?sourceRef=$BRANCH&targetRef=$DEF"; }
else
  echo "Next: open a pull request in the browser: $URL/pullrequestcreate?sourceRef=$BRANCH&targetRef=$DEF"
fi
unset TOK GIT_CONFIG_VALUE_0
