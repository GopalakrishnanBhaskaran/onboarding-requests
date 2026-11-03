#!/usr/bin/env bash
# Agent E — POC tests. Each test writes masked evidence and PASS/FAIL to evidence/E-results.txt.
# Usage: bash e_tests.sh <prep|lakehouse|shortcut|notebook|pipeline-twa|pipeline-gw --connection <id>|probe|all> --container <c> --folder <f>
#   tenant 2 (data plane reachable only from Fabric/private network):
#   seed | pipeline-gw-write --connection <id> | pipeline-gw --connection <id> | shortcut-verify [--shortcut <name>] | gw-roundtrip --connection <id>
# Creates only test objects (lakehouse poc_lh, notebook, pipelines, files under <folder>/); needs wave 3 approval.
source "$(dirname "$0")/lib.sh"
need az; need python3; need curl
az_preflight
ST="$POC_DIR/tools/state.py"
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"].get(sys.argv[2],""))' "$STATE_FILE" "$1"; }
SUB="$(env_get subscription)"; RG="$(env_get resourceGroup)"; STG="$(env_get storageAccount)"; STG="${STG%% *}"
case "$SUB" in *'*'*|*UNVERIFIED*|"") SUB="$(stg_sub "$(env_get storageAccount)" || az account show --query id -o tsv)";; esac   # template values are masked
CMD="${1:-}"; shift || true
CONTAINER="$(env_get container)" ; FOLDER="$(env_get testFolder)" ; GWCONN=""
case "$CONTAINER" in UNVERIFIED) CONTAINER="";; esac; case "$FOLDER" in UNVERIFIED|"") FOLDER="input";; esac
while [ $# -gt 0 ]; do case "$1" in --container) CONTAINER="$2"; shift 2;; --folder) FOLDER="$2"; shift 2;; --connection) GWCONN="$2"; shift 2;; --shortcut) SCNAME="$2"; shift 2;; *) die "unknown $1";; esac; done
SCNAME="${SCNAME:-adls_poc}"
[ -n "$CONTAINER" ] || die "--container required"
[ "$CMD" = probe ] || require_approval 3
WS_ID=""; [ "$CMD" = probe ] || WS_ID="$(resolve_ws_id)"   # the probe needs no Fabric call
az account set --subscription "$SUB" 2>/dev/null || true
DFS="https://$STG.dfs.core.windows.net"
RES="$EVIDENCE_DIR/E-results.txt"
j() { python3 -c "import json,sys;d=json.load(sys.stdin);print(eval(sys.argv[1]))" "$1"; }
result() { printf '%s  %-14s %s\n' "$(date -u +%FT%TZ)" "$1" "$2" | mask | tee -a "$RES"; }
b64() { python3 -c 'import base64,sys;print(base64.b64encode(sys.stdin.buffer.read()).decode())'; }
lh_id() { fab "/workspaces/$WS_ID/lakehouses" | j "[l['id'] for l in d['value'] if l['displayName']=='poc_lh'][0] if any(l['displayName']=='poc_lh' for l in d['value']) else ''"; }
onelake_get() { # onelake_get <path-and-query>; token kept in memory only
  local tok; tok="$(az account get-access-token --resource https://storage.azure.com --query accessToken -o tsv)"
  curl -sS -m 60 -H "Authorization: Bearer $tok" -H "x-ms-version: 2023-11-03" "https://onelake.dfs.fabric.microsoft.com/$1"
}
onelake_put() { # onelake_put <ws>/<lh>/Files/<path> <local file>: create, append, flush (OneLake DFS API). Token in memory only.
  local tok n u="https://onelake.dfs.fabric.microsoft.com/$1"
  tok="$(az account get-access-token --resource https://storage.azure.com --query accessToken -o tsv)"; n="$(wc -c < "$2" | tr -d ' ')"
  curl -sS -m 60 -f -X PUT -H "Authorization: Bearer $tok" -H "x-ms-version: 2023-11-03" -H 'Content-Length: 0' "$u?resource=file&overwrite=true" -o /dev/null \
  && curl -sS -m 60 -f -X PATCH -H "Authorization: Bearer $tok" -H "x-ms-version: 2023-11-03" -H 'Content-Type: application/octet-stream' --data-binary "@$2" "$u?action=append&position=0" -o /dev/null \
  && curl -sS -m 60 -f -X PATCH -H "Authorization: Bearer $tok" -H "x-ms-version: 2023-11-03" -H 'Content-Length: 0' "$u?action=flush&position=$n" -o /dev/null
}
wait_job() { # wait_job <itemId> <location-url or jobInstanceId>
  local item="$1" jid="$2" s
  for i in $(seq 1 90); do
    s="$(fab "/workspaces/$WS_ID/items/$item/jobs/instances/$jid" | j "d['status'] + ' ' + str((d.get('failureReason') or {}).get('message',''))[:300]")"
    case "$s" in Completed*|Failed*|Cancelled*|Deduped*) echo "$s"; return;; esac; sleep 20
  done; echo "Timeout"
}
run_job() { # run_job <itemId> <jobType> -> jobInstanceId (from the 202 Location header). Token stays in memory.
  local tok loc
  tok="$(az account get-access-token --resource "$FABRIC_RESOURCE" --query accessToken -o tsv)"
  loc="$(curl -sS -m 60 -o /dev/null -D - -X POST -H "Authorization: Bearer $tok" -H 'Content-Length: 0' \
    "$FABRIC_API/workspaces/$WS_ID/items/$1/jobs/instances?jobType=$2" | tr -d '\r' | awk 'tolower($1)=="location:"{print $2}')"
  [ -n "$loc" ] || die "job $2 not accepted for item $1"
  echo "${loc##*/}"
}
supported_adls() { fab "/connections/supportedConnectionTypes?showAllCreationMethods=true" | python3 -c '
import json,sys
d=json.load(sys.stdin)
for t in d.get("value",[]):
    if t.get("type")=="AzureDataLakeStorage":
        m=t["creationMethods"][0]
        print(json.dumps({"creationMethod":m["name"],"parameters":[p["name"] for p in m["parameters"]],"credentialTypes":t.get("supportedCredentialTypes")}))
        break'; }
twa_connection() { # cloud connection, workspace identity (FB7), test skipped (FB8: status check not supported)
  local name="conn-adls-twa-$STG" id meta
  id="$(fab /connections | j "[c['id'] for c in d['value'] if c.get('displayName')=='$name'][0] if any(c.get('displayName')=='$name' for c in d['value']) else ''")"
  [ -n "$id" ] && { echo "$id"; return; }
  meta="$(supported_adls)"; echo "$meta" | save E-adls-connection-type.json >/dev/null
  body="$(python3 - "$meta" "$name" "$DFS" "$CONTAINER" <<'PY'
import json,sys
m=json.loads(sys.argv[1]); name,url,cont=sys.argv[2:5]
params=[]
for p in m["parameters"]:
    v = url if any(k in p.lower() for k in ("server","url","account")) else cont
    params.append({"dataType":"Text","name":p,"value":v})
print(json.dumps({"connectivityType":"ShareableCloud","displayName":name,"privacyLevel":"Organizational",
 "connectionDetails":{"type":"AzureDataLakeStorage","creationMethod":m["creationMethod"],"parameters":params},
 "credentialDetails":{"singleSignOnType":"None","connectionEncryption":"NotEncrypted","skipTestConnection":True,
   "credentials":{"credentialType":"WorkspaceIdentity"}}}))
PY
)"
  fab /connections POST --body "$body" | j "d['id']"
}

do_prep() { # optional: only works from a client whose IP is allowed (your device); in Cloud Shell the notebook writes input.csv instead
  printf 'id,name,amount\n1,alpha,10\n2,beta,20\n3,gamma,30\n' > "$RAW_DIR/input.csv"
  az storage fs directory create -f "$CONTAINER" -n "$FOLDER" --account-name "$STG" --auth-mode login -o none 2>/dev/null || true
  if az storage fs file upload -f "$CONTAINER" -p "$FOLDER/input.csv" -s "$RAW_DIR/input.csv" --account-name "$STG" --auth-mode login --overwrite -o none 2>"$RAW_DIR/prep.err"; then
    result PREP "PASS upload $FOLDER/input.csv from the device (IP rule + your data role)"
  else result PREP "FAIL $(tail -1 "$RAW_DIR/prep.err")"; fi
}
do_lakehouse() {
  [ -n "$(lh_id)" ] || fab "/workspaces/$WS_ID/lakehouses" POST --body '{"displayName":"poc_lh"}' -o none
  sleep 5; [ -n "$(lh_id)" ] && result LAKEHOUSE "PASS poc_lh exists" || result LAKEHOUSE "FAIL poc_lh not created"
}
do_shortcut() {
  local lh conn out
  lh="$(lh_id)"; conn="$(twa_connection)"
  fab "/workspaces/$WS_ID/items/$lh/shortcuts?shortcutConflictPolicy=CreateOrOverwrite" POST --body "{\"path\":\"Files\",\"name\":\"adls_poc\",\"target\":{\"adlsGen2\":{\"location\":\"$DFS\",\"subpath\":\"/$CONTAINER/$FOLDER\",\"connectionId\":\"$conn\"}}}" 2>&1 | save E-shortcut-create.json
  sleep 10
  out="$(onelake_get "$WS_ID/$lh/Files/adls_poc/input.csv")"
  echo "$out" | save E-shortcut-read.txt
  if echo "$out" | grep -q "gamma,30"; then result SHORTCUT "PASS read input.csv through OneLake shortcut (TWA, workspace identity)"; else result SHORTCUT "FAIL $(echo "$out" | head -c 300)"; fi
}
do_notebook() {
  local lh nb ipynb st ts
  lh="$(lh_id)"; ts="$(date -u +%Y%m%d%H%M%S)"
  ipynb="$(python3 - "$STG" "$CONTAINER" "$FOLDER" "$ts" "$lh" "$WS_ID" <<'PY'
import json,sys
stg,cont,folder,ts,lh,ws=sys.argv[1:7]
code=f'''import json
base = "abfss://{cont}@{stg}.dfs.core.windows.net/{folder}"
# 1. WRITE a single CSV file into ADLS over the managed private endpoint (no client outside Fabric can reach the account)
mssparkutils.fs.put(base + "/input.csv", "id,name,amount\\n1,alpha,10\\n2,beta,20\\n3,gamma,30\\n", True)
# 2. READ it back with Spark, 3. WRITE parquet, 4. READ the parquet back
df = spark.read.option("header", True).csv(base + "/input.csv")
n_in = df.count()
out = base + "/from_fabric_notebook/run_{ts}"
df.write.mode("overwrite").parquet(out)
n_back = spark.read.parquet(out).count()
res = {{"read_rows": n_in, "written": out, "readback_rows": n_back, "ok": n_in == 3 and n_back == 3}}
mssparkutils.fs.put("Files/poc_results/notebook_{ts}.json", json.dumps(res), True)
print(res)'''
nb={"nbformat":4,"nbformat_minor":5,"cells":[{"cell_type":"code","source":[code],"metadata":{},"outputs":[],"execution_count":None}],
 "metadata":{"language_info":{"name":"python"},"kernel_info":{"name":"synapse_pyspark"},
  "dependencies":{"lakehouse":{"default_lakehouse":lh,"default_lakehouse_name":"poc_lh","default_lakehouse_workspace_id":ws}}}}
print(json.dumps(nb))
PY
)"
  nb="$(fab "/workspaces/$WS_ID/notebooks" | j "[n['id'] for n in d['value'] if n['displayName']=='poc_nb_mpe'][0] if any(n['displayName']=='poc_nb_mpe' for n in d['value']) else ''")"
  local def="{\"format\":\"ipynb\",\"parts\":[{\"path\":\"notebook-content.ipynb\",\"payload\":\"$(printf '%s' "$ipynb" | b64)\",\"payloadType\":\"InlineBase64\"}]}"
  if [ -z "$nb" ]; then fab "/workspaces/$WS_ID/notebooks" POST --body "{\"displayName\":\"poc_nb_mpe\",\"definition\":$def}" -o none; sleep 15
    nb="$(fab "/workspaces/$WS_ID/notebooks" | j "[n['id'] for n in d['value'] if n['displayName']=='poc_nb_mpe'][0]")"
  else fab "/workspaces/$WS_ID/notebooks/$nb/updateDefinition" POST --body "{\"definition\":$def}" -o none; sleep 10; fi
  st="$(wait_job "$nb" "$(run_job "$nb" RunNotebook)")"
  echo "$st" | save E-notebook-job.txt
  out="$(onelake_get "$WS_ID/$lh/Files/poc_results/notebook_$ts.json")"; echo "$out" | save E-notebook-result.json
  # expected from Cloud Shell: blocked by the firewall (Cloud Shell's IP is not allowed) -> extra "other IP" evidence
  az storage fs file list -f "$CONTAINER" --path "$FOLDER" --account-name "$STG" --auth-mode login -o json 2>&1 | head -c 600 | save E-cloudshell-adls-listing.txt
  if echo "$out" | grep -q '"ok": true'; then result NOTEBOOK "PASS read 3 rows + wrote/read back parquet via abfss:// (managed PE); job: ${st%% *}"; else result NOTEBOOK "FAIL job: $st"; fi
}
pipeline_def() { # pipeline_def <connectionId> <lakehouseId> <sinkFolder>: Binary copy ADLS file -> Lakehouse Files
  python3 - "$1" "$2" "$WS_ID" "$CONTAINER" "$FOLDER" "$3" <<'PY'
import json,sys
conn,lh,ws,cont,folder,sink=sys.argv[1:7]
p={"properties":{"activities":[{"name":"CopyAdlsToLakehouse","type":"Copy","policy":{"timeout":"0.00:30:00","retry":0},
 "typeProperties":{
  "source":{"type":"BinarySource","storeSettings":{"type":"AzureBlobFSReadSettings","recursive":False},
    "formatSettings":{"type":"BinaryReadSettings"},
    "datasetSettings":{"type":"Binary","typeProperties":{"location":{"type":"AzureBlobFSLocation","fileSystem":cont,"folderPath":folder,"fileName":"input.csv"}},
      "externalReferences":{"connection":conn}}},
  "sink":{"type":"BinarySink","storeSettings":{"type":"LakehouseWriteSettings"},
    "datasetSettings":{"type":"Binary","typeProperties":{"location":{"type":"LakehouseLocation","folderPath":sink}},
      "linkedService":{"name":"poc_lh","properties":{"type":"Lakehouse","typeProperties":{"artifactId":lh,"workspaceId":ws,"rootFolder":"Files"}}}}}}}]}}
print(json.dumps(p))
PY
}
do_pipeline() { # do_pipeline <label> <connectionId> <pipelineName> <sinkFolder>
  local label="$1" conn="$2" name="$3" sink="$4" lh pl def st out
  lh="$(lh_id)"
  def="{\"parts\":[{\"path\":\"pipeline-content.json\",\"payload\":\"$(pipeline_def "$conn" "$lh" "$sink" | b64)\",\"payloadType\":\"InlineBase64\"}]}"
  pl="$(fab "/workspaces/$WS_ID/dataPipelines" | j "[p['id'] for p in d['value'] if p['displayName']=='$name'][0] if any(p['displayName']=='$name' for p in d['value']) else ''")"
  if [ -z "$pl" ]; then fab "/workspaces/$WS_ID/dataPipelines" POST --body "{\"displayName\":\"$name\",\"definition\":$def}" -o none; sleep 10
    pl="$(fab "/workspaces/$WS_ID/dataPipelines" | j "[p['id'] for p in d['value'] if p['displayName']=='$name'][0]")"
  else fab "/workspaces/$WS_ID/dataPipelines/$pl/updateDefinition" POST --body "{\"definition\":$def}" -o none; fi
  st="$(wait_job "$pl" "$(run_job "$pl" Pipeline)")"; echo "$st" | save "E-$label-job.txt"
  out="$(onelake_get "$WS_ID/$lh/Files/$sink/input.csv")"; echo "$out" | save "E-$label-lakehouse-file.txt"
  if echo "$out" | grep -q "gamma,30"; then result "$label" "PASS copied input.csv ADLS -> Lakehouse Files/$sink; job: ${st%% *}"; else result "$label" "FAIL job: $st"; fi
}
pipeline_def_write() { # pipeline_def_write <connectionId> <lakehouseId>: Binary copy Lakehouse Files/poc_seed/input.csv -> ADLS <container>/<folder>/input.csv
  python3 - "$1" "$2" "$WS_ID" "$CONTAINER" "$FOLDER" <<'PY'
import json,sys
conn,lh,ws,cont,folder=sys.argv[1:6]
lhls={"name":"poc_lh","properties":{"type":"Lakehouse","typeProperties":{"artifactId":lh,"workspaceId":ws,"rootFolder":"Files"}}}
p={"properties":{"activities":[{"name":"CopyLakehouseToAdls","type":"Copy","policy":{"timeout":"0.00:30:00","retry":0},
 "typeProperties":{
  "source":{"type":"BinarySource","storeSettings":{"type":"LakehouseReadSettings","recursive":False},"formatSettings":{"type":"BinaryReadSettings"},
    "datasetSettings":{"type":"Binary","typeProperties":{"location":{"type":"LakehouseLocation","folderPath":"poc_seed","fileName":"input.csv"}},"linkedService":lhls}},
  "sink":{"type":"BinarySink","storeSettings":{"type":"AzureBlobFSWriteSettings"},
    "datasetSettings":{"type":"Binary","typeProperties":{"location":{"type":"AzureBlobFSLocation","fileSystem":cont,"folderPath":folder,"fileName":"input.csv"}},
      "externalReferences":{"connection":conn}}}}}]}}
print(json.dumps(p))
PY
}
do_seed() { # test file into the lakehouse via OneLake (public endpoint, your Entra token): the source for the ADLS write test
  local lh; lh="$(lh_id)"; [ -n "$lh" ] || { do_lakehouse; lh="$(lh_id)"; }
  printf 'id,name,amount\n1,alpha,10\n2,beta,20\n3,gamma,30\n' > "$RAW_DIR/input.csv"
  if onelake_put "$WS_ID/$lh/Files/poc_seed/input.csv" "$RAW_DIR/input.csv"; then result SEED "PASS Lakehouse Files/poc_seed/input.csv (OneLake API)"; else result SEED "FAIL OneLake upload"; fi
}
do_pipeline_write() { # Lakehouse -> ADLS through the gateway connection: proves WRITE to the private storage
  local conn="$1" lh pl def st name=poc_pl_gw_write
  lh="$(lh_id)"
  def="{\"parts\":[{\"path\":\"pipeline-content.json\",\"payload\":\"$(pipeline_def_write "$conn" "$lh" | b64)\",\"payloadType\":\"InlineBase64\"}]}"
  pl="$(fab "/workspaces/$WS_ID/dataPipelines" | j "[p['id'] for p in d['value'] if p['displayName']=='$name'][0] if any(p['displayName']=='$name' for p in d['value']) else ''")"
  if [ -z "$pl" ]; then fab "/workspaces/$WS_ID/dataPipelines" POST --body "{\"displayName\":\"$name\",\"definition\":$def}" -o none; sleep 10
    pl="$(fab "/workspaces/$WS_ID/dataPipelines" | j "[p['id'] for p in d['value'] if p['displayName']=='$name'][0]")"
  else fab "/workspaces/$WS_ID/dataPipelines/$pl/updateDefinition" POST --body "{\"definition\":$def}" -o none; fi
  st="$(wait_job "$pl" "$(run_job "$pl" Pipeline)")"; echo "$st" | save "E-PIPELINE_GW_WRITE-job.txt"
  case "$st" in Completed*) result PIPELINE_GW_WRITE "PASS wrote $CONTAINER/$FOLDER/input.csv to ADLS via the VNet gateway; job: ${st%% *}";;
    *) result PIPELINE_GW_WRITE "FAIL job: $st";; esac
}
do_shortcut_verify() { # the shortcut you created in the UI (org account, TWA): read the file through OneLake
  local lh out; lh="$(lh_id)"
  out="$(onelake_get "$WS_ID/$lh/Files/$SCNAME/input.csv")"; echo "$out" | head -c 2000 | save E-shortcut-read.txt
  if echo "$out" | grep -q "gamma,30"; then result SHORTCUT "PASS read input.csv through OneLake shortcut $SCNAME (trusted workspace access)"; else result SHORTCUT "FAIL $(echo "$out" | head -c 300)"; fi
}
do_probe() { # Read-only. Run it from a public IP that is on no allow-list (Claude's container, Cloud Shell).
  # Error codes (FB38, Learn "Troubleshoot 403 errors in Azure Blob Storage", 2026-07-10):
  #   403 AuthorizationFailure            = blocked by the network (public network access disabled / firewall)
  #   403 AuthorizationPermissionMismatch = passed the network, missing data role -> REACHABLE
  # Anonymous: only blob decides (dfs answers 401 NoAuthenticationInformation before the network check, observed 2026-11-02).
  # Signed in (az works, e.g. Cloud Shell): your Entra token on blob and dfs; the token stays in memory, never printed.
  local ip tok
  ip="$(device_ip || echo unknown)"
  _p() { # _p <label> <url> [token] -> prints one line, sets P_CODE / P_ERR
    local h
    if [ -n "${3:-}" ]; then h="$(curl -sS -m 20 -o /dev/null -D - -H 'x-ms-version: 2023-11-03' -H "Authorization: Bearer $3" "$2" 2>&1 || true)"
    else h="$(curl -sS -m 20 -o /dev/null -D - -H 'x-ms-version: 2023-11-03' "$2" 2>&1 || true)"; fi
    P_CODE="$(echo "$h" | awk 'toupper($1) ~ /^HTTP/ {c=$2} END{print c}')"; P_ERR="$(echo "$h" | tr -d '\r' | awk -F': ' 'tolower($1)=="x-ms-error-code"{print $2}')"
    echo "$(date -u +%FT%TZ) $1 from $ip: HTTP $P_CODE $P_ERR"
  }
  _v() { # _v <result label> <what>
    case "$P_CODE/$P_ERR" in
      403/AuthorizationFailure) result "$1" "BLOCKED from $(echo "$ip" | mask) ($2 403 AuthorizationFailure = network)";;
      401/NoAuthenticationInformation) result "$1" "NOT DECISIVE ($2 401 before the network check)";;
      *) result "$1" "REACHABLE from $(echo "$ip" | mask) ($2 HTTP $P_CODE $P_ERR)";;
    esac
  }
  local BLOB="https://$STG.blob.core.windows.net/$CONTAINER?restype=container&comp=list" ADLS="$DFS/$CONTAINER?resource=filesystem&recursive=false"
  {
    _p "blob anonymous" "$BLOB"; _v PROBE-ANON blob
    _p "dfs anonymous" "$ADLS"
    if tok="$(az account get-access-token --resource https://storage.azure.com --query accessToken -o tsv 2>/dev/null)" && [ -n "$tok" ]; then
      _p "blob signed-in" "$BLOB" "$tok"; _v PROBE-SIGNEDIN "signed-in blob"
      _p "dfs signed-in" "$ADLS" "$tok"; _v PROBE-SIGNEDIN "signed-in dfs"
    else echo "signed-in probe skipped: no storage token for this sign-in"; fi
    unset tok
  } | save "E-probe-$(echo "$ip" | mask | tr . -).txt"
}
case "$CMD" in
  prep) do_prep;; lakehouse) do_lakehouse;; shortcut) do_shortcut;; notebook) do_notebook;;
  pipeline-twa) do_pipeline PIPELINE_TWA "$(twa_connection)" poc_pl_twa from_pipeline_twa;;
  pipeline-gw) [ -n "$GWCONN" ] || die "--connection <gateway connection id> (created by you in the UI)"; do_pipeline PIPELINE_GW "$GWCONN" poc_pl_gateway from_pipeline_gw;;
  probe) do_probe;;
  seed) do_seed;;
  pipeline-gw-write) [ -n "$GWCONN" ] || die "--connection <gateway connection id>"; do_pipeline_write "$GWCONN";;
  shortcut-verify) do_shortcut_verify;;
  gw-roundtrip) [ -n "$GWCONN" ] || die "--connection <gateway connection id>"; do_lakehouse; do_seed; do_pipeline_write "$GWCONN"; do_pipeline PIPELINE_GW "$GWCONN" poc_pl_gw from_pipeline_gw; do_probe;;
  all) do_lakehouse; do_notebook; do_shortcut; do_pipeline PIPELINE_TWA "$(twa_connection)" poc_pl_twa from_pipeline_twa; do_probe;;
  *) die "usage: e_tests.sh <prep|lakehouse|shortcut|notebook|pipeline-twa|pipeline-gw|probe|all> --container <c> [--folder <f>]";;
esac
