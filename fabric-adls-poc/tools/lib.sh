#!/usr/bin/env bash
# Shared helpers for the Fabric -> ADLS Gen2 (private) POC.
# Read-only by default. Nothing here changes Azure or Fabric.
set -Eeuo pipefail
trap 'printf "%s ERROR line %s: %s\n" "$(date -u +%H:%M:%SZ)" "$LINENO" "$BASH_COMMAND" >&2' ERR

POC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_DIR="$(cd "$POC_DIR/.." && pwd)"
RAW_DIR="${RAW_DIR:-$REPO_DIR/.local/fabric-poc}"   # raw output, never in git (.local/ is ignored)
EVIDENCE_DIR="${EVIDENCE_DIR:-$POC_DIR/evidence}"   # masked output, safe for git (tests/mocks point this elsewhere)
FABRIC_API="https://api.fabric.microsoft.com/v1"
FABRIC_RESOURCE="https://api.fabric.microsoft.com"
mkdir -p "$RAW_DIR" "$EVIDENCE_DIR"
# Working state (approvals, locks, ids) lives outside git so 'git pull' never conflicts; state/state.json is the template + handoff copy.
export STATE_FILE="${STATE_FILE:-$RAW_DIR/state.json}"
[ -f "$STATE_FILE" ] || cp "$POC_DIR/state/state.json" "$STATE_FILE"
# A newer committed template (e.g. a new tenant, approvals reset) replaces a stale working copy; the old copy is kept.
_gen() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1])).get("stateGeneration",1))' "$1" 2>/dev/null || echo 0; }
if [ "$(_gen "$STATE_FILE")" != "$(_gen "$POC_DIR/state/state.json")" ]; then
  cp "$STATE_FILE" "$STATE_FILE.gen$(_gen "$STATE_FILE").bak"; cp "$POC_DIR/state/state.json" "$STATE_FILE"
  echo "$(date -u +%H:%M:%SZ) state: working copy replaced by template generation $(_gen "$STATE_FILE") (old copy kept as .bak)" >&2
fi

log()  { printf '%s %s\n' "$(date -u +%H:%M:%SZ)" "$*" >&2; }
die()  { log "ERROR: $*"; exit 1; }
need() { command -v "$1" >/dev/null 2>&1 || die "$1 not found"; }

# Mask GUIDs (keep first 4 chars), public IPv4 (keep first two octets), e-mail local parts.
# Keeps resource names readable; removes subscription/tenant/object ids and the device IP.
# Private (RFC1918) addresses such as private endpoint IPs are kept: they are not personal.
mask() {
  python3 -c '
import re,sys,ipaddress
t=sys.stdin.read()
t=re.sub(r"\b([0-9a-fA-F]{4})[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", r"\1****-****", t)
def ip(m):
    try:
        a=ipaddress.ip_address(m.group(0))
    except ValueError:
        return m.group(0)
    return m.group(0) if (a.is_private or a.is_loopback) else ".".join(m.group(0).split(".")[:2]+["x","x"])
t=re.sub(r"\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", ip, t)
t=re.sub(r"[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})", r"***@\1", t)
t=re.sub(r"(sig=|AccountKey=|SharedAccessSignature=|client_secret=|\"secret\": ?\"|\"accessToken\": ?\")[^\"&; ]+", r"\1******", t)
sys.stdout.write(t)'
}

# save <name> : stdin -> raw file + masked evidence file
save() {
  local name="$1" raw="$RAW_DIR/$1"
  cat > "$raw"
  mask < "$raw" > "$EVIDENCE_DIR/$name"
  log "saved evidence/$name"
}

# Fabric REST (GET by default). Token comes from the signed-in az CLI user; never printed.
fab() {
  local url="$1" method="GET"; shift
  if [ $# -gt 0 ] && [[ "$1" != -* ]]; then method="$1"; shift; fi
  az rest --method "$method" --resource "$FABRIC_RESOURCE" --url "$FABRIC_API$url" "$@"
}

# Device public IP as seen from the internet (only meaningful when run on your own device).
device_ip() { curl -fsS -m 10 https://api.ipify.org || curl -fsS -m 10 https://ifconfig.me; }

# Fail early, with a fix, when the Azure CLI itself is broken (e.g. a stale command index in Cloud Shell).
az_preflight() {
  az account show --query id -o tsv >/dev/null 2>"$RAW_DIR/.azerr" && return 0
  die "az is not working: $(grep -m1 -i error "$RAW_DIR/.azerr" || head -1 "$RAW_DIR/.azerr"). Fix: rm -f ~/.azure/commandIndex.json ; az account show -o table ; then re-run"
}

# Approval gate for any change: wave must be approved in state/state.json.
require_approval() {
  local wave="$1"
  python3 "$POC_DIR/tools/state.py" check-approval "$wave" || die "wave $wave is not approved (run: tools/state.py approve $wave --by '<name> <date>')"
}

# Read env.<key> from the working state file.
env_get() { python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["env"].get(sys.argv[2],""))' "$STATE_FILE" "$1"; }

# Subscription (in the tenant you are signed in to) that holds storage account $1; empty if none.
# Used when state holds a masked subscription id (the committed template never has full GUIDs).
stg_sub() {
  local name="$1" t s
  [ -n "$name" ] || return 1
  t="$(az account show --query tenantId -o tsv)"
  for s in $(az account list --query "[?tenantId=='$t' && state=='Enabled'].id" -o tsv); do
    if [ -n "$(az storage account list --subscription "$s" --query "[?name=='$name'].id | [0]" -o tsv 2>/dev/null)" ]; then echo "$s"; return 0; fi
  done
  return 1
}

# can <action> [data]: does the signed-in user hold <action> at scope $CAN_SCOPE? (ARM "permissions" API:
# all roles, inherited and through groups; PIM-eligible roles count only once activated). Cached per process.
can() {
  local f="$RAW_DIR/.perms-$$-$(echo "$CAN_SCOPE" | md5sum | cut -c1-8).json"
  [ -s "$f" ] || az rest --method get --url "https://management.azure.com$CAN_SCOPE/providers/Microsoft.Authorization/permissions?api-version=2022-04-01" -o json > "$f" 2>/dev/null || echo '{"value":[]}' > "$f"
  python3 - "$f" "$1" "${2:-}" <<'PY'
import json,sys,fnmatch
perms=json.load(open(sys.argv[1])).get("value",[]); a=sys.argv[2].lower(); data=sys.argv[3]=="data"
inc="dataActions" if data else "actions"; exc="notDataActions" if data else "notActions"
ok=any(any(fnmatch.fnmatch(a,x.lower()) for x in (p.get(inc) or [])) and not any(fnmatch.fnmatch(a,x.lower()) for x in (p.get(exc) or [])) for p in perms)
sys.exit(0 if ok else 1)
PY
}

# admin_req <what> <command>: a step the signed-in user may not do; collected for the resource's admins.
# The local file has full values (they need them); only the masked copy goes to evidence.
ADMIN_REQ="$RAW_DIR/ADMIN-REQUEST-$(basename "$0" .sh).txt"   # one file per agent; tools/admin_request.sh joins them
admin_req() { # admin_req <what> <command> [rollback command]
  echo "REQUEST $1  (needs the storage account's admins)"
  printf -- '- %s\n  %s\n' "$1" "$2" >> "$ADMIN_REQ"
  [ -z "${3:-}" ] || printf -- '  rollback: %s\n' "$3" >> "$ADMIN_REQ"
}
admin_req_show() { # admin_req_show <evidence name>
  [ -s "$ADMIN_REQ" ] || return 0
  echo "=================== SEND TO THE STORAGE ADMINS (full values; also in $ADMIN_REQ) ==================="
  cat "$ADMIN_REQ"; echo "=================== END ==================="
  save "$1" < "$ADMIN_REQ" >/dev/null
}
