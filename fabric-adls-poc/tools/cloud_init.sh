# shellcheck shell=bash
# Cloud Shell session start for the Fabric POC. SOURCE it (it sets STATE_FILE / STATE_PROFILE in your shell):
#   source fabric-adls-poc/tools/cloud_init.sh <profile> [--sub <subscription name or id>] [--tenant-prefix <first 4 chars>]
# <profile>  the profile named in the committed template (state/state.json "profile") -> that template's state;
#            any other name -> its own state in fabric-adls-poc/.local/state-<profile>.json, which starts empty
#            (no approvals, no ids: run discover.sh first) and is tied to the tenant you are signed in to now.
# Checks: az works, the tenant is the profile's, the subscription (az account set only), Fabric token, state.
# Read-only. Never signs in for you, never prints a token or a full id. Ends with a READY block to paste back.
# Cloud Shell drops this shell after 20 minutes idle (FB37): run the session-start block again after any restart.

(return 0 2>/dev/null) || { echo "source it, don't run it:  source ${BASH_SOURCE[0]} <profile>"; exit 2; }

_ci_main() {
  local prof="${1:-}" want_sub="" want_tp=""
  [ $# -gt 0 ] && shift
  while [ $# -gt 0 ]; do
    case "$1" in
      --sub) want_sub="${2:-}"; shift 2;;
      --tenant-prefix) want_tp="${2:-}"; shift 2;;
      *) echo "unknown argument $1"; return 2;;
    esac
  done
  local here poc tmpl tprof
  here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; poc="$(cd "$here/.." && pwd)"; tmpl="$poc/state/state.json"
  tprof="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1])).get("profile",""))' "$tmpl")"
  if [ -z "$prof" ]; then echo "usage: source fabric-adls-poc/tools/cloud_init.sh <profile>   (this template's profile: ${tprof:-none})"; return 2; fi
  case "$prof" in *[!a-z0-9-]*) echo "profile: lower-case letters, digits and '-' only"; return 2;; esac
  echo "== Fabric POC session start: profile $prof  $(date -u +%FT%TZ)"

  # 1. Azure CLI works (a stale command index breaks az in Cloud Shell)
  if ! az account show -o none 2>/dev/null; then
    rm -f ~/.azure/commandIndex.json
    if ! az account show -o none 2>/dev/null; then
      echo "FAIL    az is not signed in. Cloud Shell: restart it (Restart icon) or close and reopen the tab."
      echo "        Outside Cloud Shell: az login --tenant <tenant id or domain>  (you sign in; Claude never does)."
      return 1
    fi
  fi
  echo "OK      az $(az version --query '"azure-cli"' -o tsv 2>/dev/null)"

  # 2. Tenant = the profile's tenant
  local tid org tten exp
  tid="$(az account show --query tenantId -o tsv)"
  org="$(az rest --method get --url 'https://graph.microsoft.com/v1.0/organization?$select=displayName' --query 'value[0].displayName' -o tsv 2>/dev/null || echo '?')"
  tten="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"].get("tenant","")[:4])' "$tmpl")"
  if [ "$prof" = "$tprof" ]; then exp="$tten"; else exp="$want_tp"; fi
  if [ -n "$exp" ] && [ "${tid:0:4}" != "$exp" ]; then
    echo "FAIL    signed in to tenant ${tid:0:4}**** ($org), but profile $prof is tenant $exp****."
    echo "        Fix: Azure portal > Settings (gear) > Directories + subscriptions > Switch to the $prof directory,"
    echo "        reopen Cloud Shell, then run the session-start block again."
    return 1
  fi
  if [ "$prof" != "$tprof" ] && [ "${tid:0:4}" = "$tten" ]; then
    echo "FAIL    profile $prof, but you are signed in to the $tprof tenant (${tid:0:4}****, $org). Switch directory first."
    return 1
  fi
  if [ -n "$exp" ]; then echo "OK      tenant ${tid:0:4}**** ($org) = profile $prof"
  else echo "CHECK   tenant ${tid:0:4}**** ($org): UNVERIFIED that this is $prof. Confirm the name; next time add --tenant-prefix ${tid:0:4}"; fi

  # 3. State for this profile
  mkdir -p "$poc/.local"
  export STATE_PROFILE="$prof"
  if [ "$prof" = "$tprof" ]; then
    export STATE_FILE="$poc/.local/state.json"
    [ -f "$STATE_FILE" ] || cp "$tmpl" "$STATE_FILE"
  else
    export STATE_FILE="$poc/.local/state-$prof.json"
    if [ ! -f "$STATE_FILE" ]; then
      python3 - "$tmpl" "$STATE_FILE" "$prof" "$tid" <<'PY'
import json,sys
src,dst,prof,tid=sys.argv[1:5]
d=json.load(open(src))
d["profile"]=prof; d.pop("history",None); d["handoff"]=[]; d["decisions"]=[]
d["env"]={k:"UNVERIFIED (run discover.sh)" for k in d.get("env",{})}; d["env"]["tenant"]=tid
for w in d.get("waves",{}).values(): w["approvedBy"]=None; w["approvedAt"]=None
for a in d.get("agents",{}).values(): a["status"]="pending"; a["notes"]=[]
d["locks"]={k:None for k in d.get("locks",{})}
json.dump(d,open(dst,"w"),indent=2)
PY
      echo "NEW     empty state for profile $prof (no approvals, no ids), tied to tenant ${tid:0:4}****"
    fi
  fi
  echo "OK      state $(basename "$STATE_FILE")"

  # 4. Subscription: the one holding the profile's storage account, else --sub, else the only one
  local stg sub="" s n
  stg="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"].get("storageAccount","").split(" ")[0])' "$STATE_FILE")"
  case "$stg" in UNVERIFIED*|"") stg="";; esac
  if [ -n "$want_sub" ]; then
    az account set --subscription "$want_sub" 2>/dev/null && sub="$(az account show --query id -o tsv)" || { echo "FAIL    subscription '$want_sub' not found in this tenant"; return 1; }
  elif [ -n "$stg" ]; then
    for s in $(az account list --query "[?tenantId=='$tid' && state=='Enabled'].id" -o tsv); do
      if [ -n "$(az storage account list --subscription "$s" --query "[?name=='$stg'].id | [0]" -o tsv 2>/dev/null)" ]; then sub="$s"; break; fi
    done
    [ -n "$sub" ] || { echo "FAIL    storage account $stg not visible in any enabled subscription of this tenant"; return 1; }
    az account set --subscription "$sub"
  else
    n="$(az account list --query "length([?tenantId=='$tid' && state=='Enabled'])" -o tsv)"
    if [ "$n" = 1 ]; then sub="$(az account list --query "[?tenantId=='$tid' && state=='Enabled'].id | [0]" -o tsv)"; az account set --subscription "$sub"
    else
      echo "CHOOSE  $n enabled subscriptions; run this again with --sub \"<name>\":"
      az account list --query "[?tenantId=='$tid' && state=='Enabled'].name" -o tsv | sed 's/^/          /'
      return 1
    fi
  fi
  echo "OK      subscription $(az account show --query name -o tsv) (${sub:0:4}****)${stg:+, holds $stg}"

  # 5. Tools and the Fabric API token (value never printed)
  local t miss=""
  for t in python3 git curl; do command -v "$t" >/dev/null 2>&1 || miss="$miss $t"; done
  [ -z "$miss" ] && echo "OK      python3, git, curl" || echo "FAIL    missing:$miss"
  if az account get-access-token --resource https://api.fabric.microsoft.com --query expiresOn -o none 2>/dev/null; then echo "OK      Fabric API token"
  else echo "WARN    no Fabric API token for this sign-in (Fabric steps will fail; storage steps still work)"; fi

  # 6. READY block
  cd "$poc/.." || return 1
  echo "== READY (paste this block back)"
  python3 - "$STATE_FILE" <<'PY'
import json,sys,re
d=json.load(open(sys.argv[1])); e=d.get("env",{})
def m(v): return re.sub(r"\b([0-9a-fA-F]{4})[0-9a-fA-F]{4}-[0-9a-fA-F-]{27}\b", r"\1****-****", str(v))
for k in ("storageAccount","container","fabricWorkspace","fabricCapacity","gateway"):
    if k in e: print(f"  {k:<15} {m(e[k])[:90]}")
w=d.get("waves",{}); print("  approvals      "+"  ".join(f"wave {k}: {'yes' if v.get('approvedBy') else 'no'}" for k,v in sorted(w.items())))
print("  agents         "+"  ".join(f"{k} {v.get('status')}" for k,v in sorted(d.get("agents",{}).items())))
PY
  git -C "$poc/.." rev-parse --git-dir >/dev/null 2>&1 && echo "  code           $(git -C "$poc/.." rev-parse --abbrev-ref HEAD) @ $(git -C "$poc/.." log -1 --format=%h)"
  echo "  folder         $(pwd)   (commands: bash fabric-adls-poc/tools/<tool>.sh ...)"
  return 0
}

_ci_main "$@"; _ci_rc=$?; unset -f _ci_main; return $_ci_rc
